using System.Security.Cryptography;
using System.Text;

namespace Stratify.S3.Services;

public class FileValidationService
{
    private readonly ILogger<FileValidationService> _logger;

    public FileValidationService(ILogger<FileValidationService> logger)
    {
        _logger = logger;
    }

    public async Task<bool> ValidateFileTransferAsync(string sourcePath, string targetPath)
    {
        try
        {
            // ファイル内容のMD5ハッシュを比較
            var sourceHash = await ComputeFileMD5HashAsync(sourcePath);
            var targetHash = await ComputeFileMD5HashAsync(targetPath);
            
            var isValid = sourceHash.Equals(targetHash, StringComparison.OrdinalIgnoreCase);
            if (!isValid)
            {
                _logger.LogWarning("ファイル検証が失敗しました: ソースMD5={SourceHash}, ターゲットMD5={TargetHash}", 
                    sourceHash, targetHash);
            }
            else
            {
                _logger.LogDebug("ファイル検証が成功しました: {SourcePath} -> {TargetPath}", 
                    sourcePath, targetPath);
            }
            
            return isValid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ファイル転送の検証中にエラーが発生しました: {Source} -> {Target}", sourcePath, targetPath);
            return false;
        }
    }

    private async Task<string> ComputeFileMD5HashAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"ファイルが見つかりません: {filePath}");
        }

        using var md5 = MD5.Create();
        await using var stream = File.OpenRead(filePath);
        var hash = await md5.ComputeHashAsync(stream);
        return Convert.ToHexString(hash).ToLower();
    }

    private Task<string> ComputeETagAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"ファイルが見つかりません: {filePath}");
        }

        var fileInfo = new FileInfo(filePath);
        
        // Program.csと同じETag生成方式を使用
        var relativePath = Path.GetFileName(filePath);
        var etagContent = $"{relativePath}:{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}";
        var etag = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(etagContent))).ToLower();
        
        return Task.FromResult(etag);
    }

    public async Task<string> GetFileETagAsync(string filePath)
    {
        return await ComputeETagAsync(filePath);
    }

    public async Task<string> GetFileMD5HashAsync(string filePath)
    {
        return await ComputeFileMD5HashAsync(filePath);
    }
}