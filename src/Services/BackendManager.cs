using Stratify.S3.Models;
using System.Collections.Concurrent;

namespace Stratify.S3.Services;

public class BackendManager(IConfiguration configuration, ILogger<BackendManager> logger, FileValidationService validationService)
{
    private readonly List<BackendConfiguration> _backends = configuration.GetSection("Backends").Get<List<BackendConfiguration>>() ?? [];
    private readonly AppConfiguration _config = configuration.GetSection("AppSettings").Get<AppConfiguration>() ?? new();
    private readonly ILogger<BackendManager> _logger = logger;
    private readonly FileValidationService _validationService = validationService;
    private Timer? _recoveryTimer;
    private bool _recoveryRunning;
    private readonly SemaphoreSlim _recoverySemaphore = new(1);
    private readonly ConcurrentDictionary<string, int> _errorCounts = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastErrorTime = new();

    public async Task<bool> CheckBackendHealthAsync(BackendConfiguration backend)
    {
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        
        if (currentTime - backend.LastCheck < backend.CheckInterval)
            return backend.Available;

        try
        {
            var backendPath = backend.Path;
            if (!Directory.Exists(backendPath))
                Directory.CreateDirectory(backendPath);

            var testFile = Path.Combine(backendPath, _config.HealthCheckFile);
            var testContent = $"health_check_{currentTime}";
            
            await File.WriteAllTextAsync(testFile, testContent);
            var content = await File.ReadAllTextAsync(testFile);
            
            if (content.Trim() == testContent)
            {
                File.Delete(testFile);
                backend.Available = true;
                backend.LastCheck = currentTime;
                _logger.LogInformation("バックエンド {BackendName} は正常です", backend.Name);
                return true;
            }
            throw new Exception("Content verification failed");
        }
        catch (Exception ex)
        {
            backend.Available = false;
            backend.LastCheck = currentTime;
            _logger.LogError(ex, "バックエンド {BackendName} のヘルスチェックが失敗しました", backend.Name);
            return false;
        }
    }

    public async Task<List<BackendConfiguration>> GetAvailableBackendsAsync()
    {
        var available = new List<BackendConfiguration>();
        foreach (var backend in _backends.OrderBy(b => b.Priority))
        {
            if (await CheckBackendHealthAsync(backend))
            {
                available.Add(backend);
            }
        }
        return available;
    }

    public async Task<FileLocation?> FindFileWithFallbackAsync(string relativePath)
    {
        foreach (var backend in _backends.OrderBy(b => b.Priority))
        {
            if (!await IsBackendUsableAsync(backend))
                continue;

            var filePath = Path.Combine(backend.Path, relativePath);
            try
            {
                if (File.Exists(filePath))
                {
                    // ファイルアクセスを試行して実際に読めることを確認
                    using var stream = File.OpenRead(filePath);
                    await stream.ReadExactlyAsync(new byte[1]);
                    
                    ResetErrorCount(backend.Name);
                    return new FileLocation { Backend = backend, Path = filePath };
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "バックエンド {BackendName} でファイル {RelativePath} へのアクセスに失敗しました、次のバックエンドを試行します", backend.Name, relativePath);
                IncrementErrorCount(backend.Name);
                continue;
            }
        }
        return null;
    }

    public void StartRecoveryMonitor()
    {
        if (_config.AutoRecoveryEnabled && _recoveryTimer == null)
        {
            _recoveryTimer = new Timer(
                async _ => await PerformRecoveryAsync(),
                null,
                TimeSpan.FromSeconds(_config.RecoveryCheckInterval),
                TimeSpan.FromSeconds(_config.RecoveryCheckInterval)
            );
            _logger.LogInformation("復旧監視を開始しました");
        }
    }

    public void StopRecoveryMonitor()
    {
        _recoveryTimer?.Dispose();
        _recoveryTimer = null;
        _logger.LogInformation("復旧監視を停止しました");
    }

    private async Task PerformRecoveryAsync()
    {
        if (!await _recoverySemaphore.WaitAsync(0))
            return;

        try
        {
            _recoveryRunning = true;
            _logger.LogInformation("復旧チェックを開始しています...");

            var availableBackends = await GetAvailableBackendsAsync();
            if (availableBackends.Count <= 1)
            {
                return;
            }

            var primaryBackend = availableBackends[0];
            var filesToRecover = await FindRecoveryCandidatesAsync(primaryBackend, availableBackends.Skip(1).ToList());

            if (filesToRecover.Count == 0)
            {
                _logger.LogInformation("復旧が必要なファイルはありません");
                return;
            }

            _logger.LogInformation("復旧対象のファイルを {Count} 個見つけました", filesToRecover.Count);

            for (int i = 0; i < filesToRecover.Count; i += _config.RecoveryBatchSize)
            {
                var batch = filesToRecover.Skip(i).Take(_config.RecoveryBatchSize).ToList();
                await RecoverFileBatchAsync(primaryBackend, batch);
                await Task.Delay(1000);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "復旧監視でエラーが発生しました");
        }
        finally
        {
            _recoveryRunning = false;
            _recoverySemaphore.Release();
        }
    }

    private Task<List<RecoveryCandidate>> FindRecoveryCandidatesAsync(
        BackendConfiguration primaryBackend, 
        List<BackendConfiguration> otherBackends)
    {
        var candidates = new List<RecoveryCandidate>();

        foreach (var backend in otherBackends)
        {
            if (!Directory.Exists(backend.Path))
                continue;

            try
            {
                var files = Directory.EnumerateFiles(backend.Path, "*", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).StartsWith('.'));

                foreach (var filePath in files)
                {
                    var relativePath = Path.GetRelativePath(backend.Path, filePath);
                    var primaryFile = Path.Combine(primaryBackend.Path, relativePath);

                    var shouldRecover = false;
                    var reason = "";

                    if (!File.Exists(primaryFile))
                    {
                        shouldRecover = true;
                        reason = "missing_in_primary";
                    }
                    else
                    {
                        var sourceInfo = new FileInfo(filePath);
                        var targetInfo = new FileInfo(primaryFile);
                        if (sourceInfo.LastWriteTimeUtc > targetInfo.LastWriteTimeUtc)
                        {
                            shouldRecover = true;
                            reason = "newer_version";
                        }
                        else
                        {
                            // プライマリにファイルが存在する場合、セカンダリのファイルを削除
                            try
                            {
                                File.Delete(filePath);
                                _logger.LogInformation("セカンダリのファイルを削除しました: {RelativePath} from {BackendName} (プライマリに存在するため)", 
                                    relativePath, backend.Name);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "セカンダリのファイル削除に失敗しました: {RelativePath} from {BackendName}", 
                                    relativePath, backend.Name);
                            }
                        }
                    }

                    if (shouldRecover)
                    {
                        candidates.Add(new RecoveryCandidate
                        {
                            SourcePath = filePath,
                            SourceBackend = backend.Name,
                            TargetPath = primaryFile,
                            RelativePath = relativePath,
                            Reason = reason
                        });

                        if (candidates.Count >= _config.RecoveryBatchSize * 10)
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "バックエンド {BackendName} のスキャン中にエラーが発生しました", backend.Name);
            }
        }

        return Task.FromResult(candidates);
    }

    private async Task RecoverFileBatchAsync(BackendConfiguration primaryBackend, List<RecoveryCandidate> fileBatch)
    {
        foreach (var fileInfo in fileBatch)
        {
            await MoveFileWithValidationAsync(fileInfo, primaryBackend);
        }
    }

    private async Task MoveFileWithValidationAsync(RecoveryCandidate fileInfo, BackendConfiguration targetBackend)
    {
        var tempTargetPath = $"{fileInfo.TargetPath}.tmp";
        var moved = false;

        try
        {
            // ターゲットディレクトリを作成
            var targetDir = Path.GetDirectoryName(fileInfo.TargetPath);
            if (targetDir != null && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // 一時ファイルにコピー
            await using (var sourceStream = File.OpenRead(fileInfo.SourcePath))
            await using (var tempStream = File.Create(tempTargetPath))
            {
                await sourceStream.CopyToAsync(tempStream);
            }

            // ファイル属性を保持
            var sourceFileInfo = new FileInfo(fileInfo.SourcePath);
            File.SetLastWriteTimeUtc(tempTargetPath, sourceFileInfo.LastWriteTimeUtc);

            // ファイル転送の検証
            var isValid = await _validationService.ValidateFileTransferAsync(fileInfo.SourcePath, tempTargetPath);
            if (!isValid)
            {
                _logger.LogError("ファイル検証が失敗しました: {RelativePath}、移動を中止します", fileInfo.RelativePath);
                return;
            }

            // 既存のターゲットファイルがある場合はバックアップ
            string? backupPath = null;
            if (File.Exists(fileInfo.TargetPath))
            {
                backupPath = $"{fileInfo.TargetPath}.backup";
                File.Move(fileInfo.TargetPath, backupPath);
            }

            try
            {
                // 一時ファイルを最終的な場所に移動
                File.Move(tempTargetPath, fileInfo.TargetPath);
                moved = true;

                // 元のファイルを削除（移動完了）
                File.Delete(fileInfo.SourcePath);

                // バックアップファイルを削除
                if (backupPath != null && File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }

                _logger.LogInformation("ファイルの移動が完了しました: {RelativePath} を {SourceBackend} から {TargetBackend} へ ({Reason})", 
                    fileInfo.RelativePath, fileInfo.SourceBackend, targetBackend.Name, fileInfo.Reason);
            }
            catch (Exception ex)
            {
                // 移動に失敗した場合、バックアップから復元
                if (backupPath != null && File.Exists(backupPath))
                {
                    if (File.Exists(fileInfo.TargetPath))
                    {
                        File.Delete(fileInfo.TargetPath);
                    }
                    File.Move(backupPath, fileInfo.TargetPath);
                }
                throw new Exception($"Failed to move file to final location: {ex.Message}", ex);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ファイルの移動に失敗しました: {RelativePath} を {SourceBackend} から {TargetBackend} へ", 
                fileInfo.RelativePath, fileInfo.SourceBackend, targetBackend.Name);

            // クリーンアップ：一時ファイルを削除
            if (File.Exists(tempTargetPath))
            {
                try { File.Delete(tempTargetPath); } catch { }
            }

            // 移動が部分的に完了していた場合は元の状態を保持
            if (!moved && File.Exists(fileInfo.SourcePath))
            {
                _logger.LogInformation("移動失敗のため、ソースファイルを {SourcePath} に保持しました", fileInfo.SourcePath);
            }
        }
    }

    public async Task<Dictionary<string, object>> TriggerImmediateRecoveryAsync()
    {
        if (_recoveryRunning)
        {
            return new Dictionary<string, object> { ["status"] = "recovery already running" };
        }

        try
        {
            await PerformRecoveryAsync();
            return new Dictionary<string, object> { ["status"] = "recovery completed" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "手動復旧が失敗しました");
            return new Dictionary<string, object> 
            { 
                ["status"] = "recovery failed",
                ["error"] = ex.Message
            };
        }
    }

    public Task<List<string>> GetAllBucketsAsync()
    {
        var allBuckets = new HashSet<string>();
        
        // 優先度の低い順（数値の大きい順）でバックエンドを処理
        foreach (var backend in _backends.OrderByDescending(b => b.Priority))
        {
            if (!backend.Available)
                continue;

            try
            {
                var backendPath = backend.Path;
                if (!Directory.Exists(backendPath))
                    continue;

                foreach (var dir in Directory.GetDirectories(backendPath))
                {
                    var dirInfo = new DirectoryInfo(dir);
                    if (!dirInfo.Name.StartsWith('.'))
                    {
                        allBuckets.Add(dirInfo.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "バックエンド {BackendName} からのバケット一覧取得でエラーが発生しました", backend.Name);
            }
        }

        return Task.FromResult(allBuckets.OrderBy(b => b).ToList());
    }

    public Task<List<FileObjectInfo>> GetAllObjectsInBucketAsync(string bucketName, string prefix = "", int maxKeys = 1000, string marker = "")
    {
        var allObjects = new Dictionary<string, FileObjectInfo>();
        
        // 優先度の低い順（数値の大きい順）でバックエンドを処理
        // 同じファイルがある場合、優先度の高いもので上書きされる
        foreach (var backend in _backends.OrderByDescending(b => b.Priority))
        {
            if (!backend.Available)
                continue;

            try
            {
                var bucketPath = Path.Combine(backend.Path, bucketName);
                if (!Directory.Exists(bucketPath))
                    continue;

                var files = Directory.EnumerateFiles(bucketPath, "*", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).StartsWith('.'));

                foreach (var filePath in files)
                {
                    try
                    {
                        var relativePath = Path.GetRelativePath(bucketPath, filePath).Replace(Path.DirectorySeparatorChar, '/');
                        
                        // プレフィックスフィルタ
                        if (!string.IsNullOrEmpty(prefix) && !relativePath.StartsWith(prefix))
                            continue;

                        // マーカーフィルタ
                        if (!string.IsNullOrEmpty(marker) && string.CompareOrdinal(relativePath, marker) <= 0)
                            continue;

                        var fileInfo = new FileInfo(filePath);
                        var objectInfo = new FileObjectInfo
                        {
                            Key = relativePath,
                            Size = fileInfo.Length,
                            LastModified = fileInfo.LastWriteTimeUtc,
                            BackendName = backend.Name,
                            BackendPriority = backend.Priority,
                            FilePath = filePath
                        };

                        // 既存のオブジェクトより優先度が高い場合、または新しいオブジェクトの場合は追加/更新
                        if (!allObjects.ContainsKey(relativePath) || 
                            allObjects[relativePath].BackendPriority > backend.Priority)
                        {
                            allObjects[relativePath] = objectInfo;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "バックエンド {BackendName} のファイル {FilePath} の処理中にエラーが発生しました", backend.Name, filePath);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "バックエンド {BackendName} からのオブジェクト一覧取得でエラーが発生しました", backend.Name);
            }
        }

        // ソートして制限を適用
        var sortedObjects = allObjects.Values
            .OrderBy(o => o.Key)
            .Take(maxKeys)
            .ToList();

        return Task.FromResult(sortedObjects);
    }

    public async Task<bool> WriteFileWithFallbackAsync(string relativePath, byte[] content, string? excludeBackend = null)
    {
        foreach (var backend in _backends.OrderBy(b => b.Priority))
        {
            if (backend.Name == excludeBackend || !await IsBackendUsableAsync(backend))
                continue;

            try
            {
                var targetPath = Path.Combine(backend.Path, relativePath);
                var targetDir = Path.GetDirectoryName(targetPath);
                if (targetDir != null && !Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                await File.WriteAllBytesAsync(targetPath, content);
                
                _logger.LogInformation("ファイル {RelativePath} を {BackendName} に書き込みました", relativePath, backend.Name);
                ResetErrorCount(backend.Name);
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "バックエンド {BackendName} へのファイル {RelativePath} 書き込みに失敗しました、次のバックエンドを試行します", backend.Name, relativePath);
                IncrementErrorCount(backend.Name);
                continue;
            }
        }
        
        _logger.LogError("全てのバックエンドでファイル {RelativePath} の書き込みに失敗しました", relativePath);
        return false;
    }


    public async Task<bool> DeleteFileWithFallbackAsync(string relativePath)
    {
        bool primarySuccess = false;
        bool anySuccess = false;
        var errors = new List<Exception>();
        var orderedBackends = _backends.OrderBy(b => b.Priority).ToList();
        var primaryBackend = orderedBackends.FirstOrDefault();

        foreach (var backend in orderedBackends)
        {
            if (!await IsBackendUsableAsync(backend))
                continue;

            try
            {
                var filePath = Path.Combine(backend.Path, relativePath);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    _logger.LogInformation("{RelativePath} を {BackendName} から削除しました", relativePath, backend.Name);
                    anySuccess = true;
                    ResetErrorCount(backend.Name);
                    
                    // プライマリストレージの削除が成功したかを記録
                    if (backend == primaryBackend)
                    {
                        primarySuccess = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "{BackendName} からの削除に失敗しました", backend.Name);
                IncrementErrorCount(backend.Name);
                errors.Add(ex);
                
                // プライマリストレージの削除が失敗した場合
                if (backend == primaryBackend)
                {
                    _logger.LogError("プライマリストレージ {BackendName} からの削除に失敗しました: {RelativePath}", backend.Name, relativePath);
                }
            }
        }

        if (!anySuccess && errors.Count > 0)
        {
            _logger.LogDebug("全てのバックエンドでファイル {RelativePath} の削除に失敗しました", relativePath);
        }

        // プライマリストレージが使用可能な場合は、そのストレージでの削除が成功している必要がある
        if (primaryBackend != null && await IsBackendUsableAsync(primaryBackend))
        {
            if (!primarySuccess)
            {
                _logger.LogError("プライマリストレージでの削除が失敗したため、削除操作を失敗として扱います: {RelativePath}", relativePath);
                return false;
            }
        }

        // S3の仕様では存在しないファイルの削除も成功として扱う
        return true;
    }

    public Task<bool> SetBackendAvailabilityAsync(string backendName, bool available)
    {
        var backend = _backends.FirstOrDefault(b => b.Name.Equals(backendName, StringComparison.OrdinalIgnoreCase));
        if (backend == null)
        {
            _logger.LogWarning("バックエンド {BackendName} が見つかりません", backendName);
            return Task.FromResult(false);
        }

        backend.Available = available;
        var action = available ? "enabled" : "disabled";
        _logger.LogInformation("バックエンド {BackendName} を手動で {Action} しました", backendName, action == "enabled" ? "有効化" : "無効化");
        
        return Task.FromResult(true);
    }

    private async Task<bool> IsBackendUsableAsync(BackendConfiguration backend)
    {
        // 手動で無効化されている場合は使用不可
        if (!backend.Available)
            return false;

        // エラー回数が閾値を超えている場合は使用不可
        if (_errorCounts.TryGetValue(backend.Name, out var errorCount) && errorCount >= backend.MaxRetries)
        {
            // 最後のエラーから一定時間経過していれば復旧を試行
            if (_lastErrorTime.TryGetValue(backend.Name, out var lastError) && 
                DateTime.UtcNow - lastError > TimeSpan.FromMinutes(2))
            {
                _logger.LogInformation("バックエンド {BackendName} の復旧を試行します", backend.Name);
                
                // 既存のヘルスチェックを使用して復旧を確認
                if (await CheckBackendHealthAsync(backend))
                {
                    ResetErrorCount(backend.Name);
                    _logger.LogInformation("バックエンド {BackendName} が正常に復旧しました", backend.Name);
                    return true;
                }
                else
                {
                    // 復旧失敗時は最後のエラー時刻を更新（次回の復旧試行まで待機）
                    _lastErrorTime[backend.Name] = DateTime.UtcNow;
                    _logger.LogDebug("バックエンド {BackendName} の復旧に失敗しました、次回再試行します", backend.Name);
                }
            }
            return false;
        }

        return true;
    }

    private void IncrementErrorCount(string backendName)
    {
        var errorCount = _errorCounts.AddOrUpdate(backendName, 1, (key, oldValue) => oldValue + 1);
        _lastErrorTime[backendName] = DateTime.UtcNow;
        
        var backend = _backends.FirstOrDefault(b => b.Name == backendName);
        
        if (backend != null && errorCount >= backend.MaxRetries)
        {
            _logger.LogWarning("バックエンド {BackendName} でエラーが {ErrorCount} 回連続発生したため一時的に無効化します", 
                backendName, errorCount);
        }
    }

    private void ResetErrorCount(string backendName)
    {
        _errorCounts.TryRemove(backendName, out _);
        _lastErrorTime.TryRemove(backendName, out _);
    }

    public Task<List<object>> GetBackendStatusAsync()
    {
        var status = _backends.Select(b => {
            var errorCount = _errorCounts.GetValueOrDefault(b.Name, 0);
            _lastErrorTime.TryGetValue(b.Name, out var lastErrorTime);
            var lastError = (DateTime?)lastErrorTime;
            
            // エラー回数が閾値を超えているかチェック（同期版）
            var isTemporarilyDown = b.Available && errorCount >= b.MaxRetries;
            var status = !b.Available ? "Manually Disabled" : 
                        isTemporarilyDown ? "Temporarily Unavailable" : "Available";
            
            return new
            {
                b.Name,
                b.Path,
                b.Priority,
                b.Available,
                IsUsable = !isTemporarilyDown,
                ErrorCount = errorCount,
                LastError = lastError?.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                LastCheck = DateTimeOffset.FromUnixTimeSeconds((long)b.LastCheck).ToString("yyyy-MM-dd HH:mm:ss UTC"),
                b.CheckInterval,
                b.Timeout,
                b.MaxRetries,
                Status = status
            };
        }).Cast<object>().ToList();

        return Task.FromResult(status);
    }
}