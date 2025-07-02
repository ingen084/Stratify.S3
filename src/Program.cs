using Stratify.S3.Helpers;
using Stratify.S3.Middleware;
using Stratify.S3.Models;
using Stratify.S3.Services;
using System.Security.Cryptography;
using System.Text;
using System.Web;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddSingleton<FileValidationService>();
builder.Services.AddSingleton<BackendManager>();
builder.Services.AddSingleton<AuthenticationService>();
builder.Services.AddHostedService<RecoveryHostedService>();
builder.Services.AddLogging();

// Configure Kestrel to allow synchronous IO for streaming
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.AllowSynchronousIO = true;
});

var app = builder.Build();

// Add authentication middleware
app.UseMiddleware<AuthenticationMiddleware>();

// Get services
var backendManager = app.Services.GetRequiredService<BackendManager>();
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var config = app.Configuration.GetSection("AppSettings").Get<AppConfiguration>() ?? new AppConfiguration();

// Initialize backends on startup
var backends = app.Configuration.GetSection("Backends").Get<List<BackendConfiguration>>() ?? new List<BackendConfiguration>();
foreach (var backend in backends)
{
    try
    {
        Directory.CreateDirectory(backend.Path);
        logger.LogInformation("バックエンドを初期化しました: {BackendName} at {Path}", backend.Name, backend.Path);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "バックエンド {BackendName} の初期化に失敗しました", backend.Name);
    }
}

// S3 API Endpoints

// ListBuckets
app.MapGet("/", async () =>
{
    try
    {
        var bucketNames = await backendManager.GetAllBucketsAsync();
        var buckets = new List<(string name, DateTime creationDate)>();
        
        // 各バケットの作成日時を取得（最初に見つかったバックエンドから）
        foreach (var bucketName in bucketNames)
        {
            DateTime creationDate = DateTime.UtcNow; // デフォルト値
            
            // 優先度順でバケットの作成日時を探す
            foreach (var backend in (await backendManager.GetAvailableBackendsAsync()))
            {
                var bucketPath = Path.Combine(backend.Path, bucketName);
                if (Directory.Exists(bucketPath))
                {
                    creationDate = new DirectoryInfo(bucketPath).CreationTimeUtc;
                    break;
                }
            }
            
            buckets.Add((bucketName, creationDate));
        }

        return Results.Content(
            S3XmlHelper.CreateListBucketsResponse(buckets),
            "application/xml"
        );
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "バケット一覧の取得に失敗しました");
        return Results.Content(
            S3XmlHelper.CreateErrorResponse("InternalError", "バケット一覧の取得に失敗しました", "/"),
            "application/xml",
            statusCode: 500
        );
    }
});

// ListObjects
app.MapGet("/{bucket}", async (string bucket, string? prefix, string? delimiter, int? maxKeys, string? marker) =>
{
    bucket = HttpUtility.UrlDecode(bucket);
    prefix = HttpUtility.UrlDecode(prefix ?? "");
    maxKeys ??= 1000;
    marker ??= "";

    try
    {
        // 全バックエンドから集約してオブジェクトを取得
        var fileObjects = await backendManager.GetAllObjectsInBucketAsync(bucket, prefix, maxKeys.Value, marker);
        
        // バケットが存在しない場合のチェック
        if (fileObjects.Count == 0)
        {
            // どのバックエンドにもバケットが存在しないかチェック
            var bucketExists = false;
            foreach (var backend in await backendManager.GetAvailableBackendsAsync())
            {
                var bucketPath = Path.Combine(backend.Path, bucket);
                if (Directory.Exists(bucketPath))
                {
                    bucketExists = true;
                    break;
                }
            }
            
            if (!bucketExists)
            {
                return Results.Content(
                    S3XmlHelper.CreateErrorResponse("NoSuchBucket", "指定されたバケットが存在しません", $"/{bucket}"),
                    "application/xml",
                    statusCode: 404
                );
            }
        }

        var objects = new List<(string key, long size, DateTime lastModified, string etag)>();
        var truncated = fileObjects.Count >= maxKeys.Value;

        foreach (var fileObject in fileObjects)
        {
            try
            {
                var etagContent = $"{fileObject.Key}:{fileObject.Size}:{fileObject.LastModified.Ticks}";
                var etag = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(etagContent))).ToLower();
                objects.Add((fileObject.Key, fileObject.Size, fileObject.LastModified, etag));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "バックエンド {Backend} のファイルオブジェクト {Key} の処理に失敗しました", fileObject.BackendName, fileObject.Key);
            }
        }

        return Results.Content(
            S3XmlHelper.CreateListObjectsResponse(bucket, prefix, marker, maxKeys.Value, truncated, objects),
            "application/xml"
        );
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "オブジェクト一覧の取得に失敗しました");
        return Results.Content(
            S3XmlHelper.CreateErrorResponse("InternalError", "オブジェクト一覧の取得に失敗しました", $"/{bucket}"),
            "application/xml",
            statusCode: 500
        );
    }
});

// GetObject
app.MapGet("/{bucket}/{**key}", async (string bucket, string key, HttpContext context) =>
{
    bucket = HttpUtility.UrlDecode(bucket);
    key = HttpUtility.UrlDecode(key);
    var relativePath = Path.Combine(bucket, key);

    var fileLocation = await backendManager.FindFileWithFallbackAsync(relativePath);
    if (fileLocation == null)
    {
        return Results.Content(
            S3XmlHelper.CreateErrorResponse("NoSuchKey", "指定されたキーが存在しません", $"/{bucket}/{key}"),
            "application/xml",
            statusCode: 404
        );
    }

    var fileInfo = new FileInfo(fileLocation.Path);
    var fileSize = fileInfo.Length;

    // Handle Range requests
    long start = 0;
    long end = fileSize - 1;
    var rangeHeader = context.Request.Headers.Range.FirstOrDefault();
    
    if (!string.IsNullOrEmpty(rangeHeader))
    {
        try
        {
            var range = rangeHeader.Replace("bytes=", "").Split('-');
            if (!string.IsNullOrEmpty(range[0]))
                start = long.Parse(range[0]);
            if (range.Length > 1 && !string.IsNullOrEmpty(range[1]))
                end = long.Parse(range[1]);
        }
        catch
        {
            return Results.Content(
                S3XmlHelper.CreateErrorResponse("InvalidRange", "リクエストされた範囲が無効です", $"/{bucket}/{key}"),
                "application/xml",
                statusCode: 416
            );
        }
    }

    var etagContent = $"{key}:{fileSize}:{fileInfo.LastWriteTimeUtc.Ticks}";
    var etag = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(etagContent))).ToLower();

    context.Response.Headers.ETag = $"\"{etag}\"";
    context.Response.Headers.LastModified = fileInfo.LastWriteTimeUtc.ToString("R");
    context.Response.Headers.AcceptRanges = "bytes";
    context.Response.Headers.ContentLength = end - start + 1;

    if (!string.IsNullOrEmpty(rangeHeader))
    {
        context.Response.StatusCode = 206;
        context.Response.Headers.ContentRange = $"bytes {start}-{end}/{fileSize}";
    }

    await using var fileStream = new FileStream(fileLocation.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
    fileStream.Seek(start, SeekOrigin.Begin);
    
    var buffer = new byte[config.ChunkSize];
    var remaining = end - start + 1;
    
    while (remaining > 0)
    {
        var toRead = (int)Math.Min(buffer.Length, remaining);
        var read = await fileStream.ReadAsync(buffer, 0, toRead);
        if (read == 0) break;
        
        await context.Response.Body.WriteAsync(buffer, 0, read);
        remaining -= read;
    }

    return Results.Empty;
});

// PutObject
app.MapPut("/{bucket}/{**key}", async (string bucket, string key, HttpContext context) =>
{
    bucket = HttpUtility.UrlDecode(bucket);
    key = HttpUtility.UrlDecode(key);

    try
    {
        // Check Content-Length
        if (context.Request.Headers.ContentLength.HasValue && 
            context.Request.Headers.ContentLength.Value > config.MaxFileSize)
        {
            return Results.Content(
                S3XmlHelper.CreateErrorResponse("EntityTooLarge", "アップロードサイズが最大許可サイズを超えています", $"/{bucket}/{key}"),
                "application/xml",
                statusCode: 413
            );
        }

        // Read file content
        using var memoryStream = new MemoryStream();
        await context.Request.Body.CopyToAsync(memoryStream);
        var content = memoryStream.ToArray();
        
        // Write with fallback
        var relativePath = Path.Combine(bucket, key);
        var success = await backendManager.WriteFileWithFallbackAsync(relativePath, content);
        
        if (!success)
        {
            return Results.Content(
                S3XmlHelper.CreateErrorResponse("ServiceUnavailable", "利用可能なバックエンドがありません", $"/{bucket}/{key}"),
                "application/xml",
                statusCode: 503
            );
        }
        
        // Generate ETag
        var etag = Convert.ToHexString(MD5.HashData(content)).ToLower();

        context.Response.Headers.ETag = $"\"{etag}\"";
        return Results.Ok();
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "{Bucket}/{Key} の保存に失敗しました", bucket, key);
        
        return Results.Content(
            S3XmlHelper.CreateErrorResponse("InternalError", "オブジェクトの保存に失敗しました", $"/{bucket}/{key}"),
            "application/xml",
            statusCode: 500
        );
    }
});

// DeleteObject
app.MapDelete("/{bucket}/{**key}", async (string bucket, string key) =>
{
    bucket = HttpUtility.UrlDecode(bucket);
    key = HttpUtility.UrlDecode(key);
    var relativePath = Path.Combine(bucket, key);

    // Delete with fallback
    await backendManager.DeleteFileWithFallbackAsync(relativePath);
    
    // S3 spec: deleting non-existent objects returns success
    return Results.NoContent();
});

// Health check endpoint
app.MapGet("/health", async () =>
{
    var backends = app.Configuration.GetSection("Backends").Get<List<BackendConfiguration>>() ?? new List<BackendConfiguration>();
    var backendStatuses = new List<object>();

    foreach (var backend in backends)
    {
        var isHealthy = await backendManager.CheckBackendHealthAsync(backend);
        backendStatuses.Add(new
        {
            backend.Name,
            backend.Path,
            backend.Priority,
            Available = isHealthy,
            backend.LastCheck
        });
    }

    var availableCount = backendStatuses.Count(b => ((dynamic)b).Available);

    return Results.Ok(new
    {
        Status = availableCount > 0 ? "healthy" : "unhealthy",
        AvailableBackends = availableCount,
        TotalBackends = backendStatuses.Count,
        Backends = backendStatuses,
        Config = config
    });
});


// Admin recovery trigger
app.MapPost("/admin/recovery", async () =>
{
    var result = await backendManager.TriggerImmediateRecoveryAsync();
    return Results.Ok(result);
});

// Admin backend control
app.MapPost("/admin/backend/{backendName}/disable", async (string backendName) =>
{
    var result = await backendManager.SetBackendAvailabilityAsync(backendName, false);
    return Results.Ok(new { 
        BackendName = backendName, 
        Action = "disabled", 
        Success = result,
        Message = result ? $"Backend '{backendName}' has been disabled" : $"Backend '{backendName}' not found"
    });
});

app.MapPost("/admin/backend/{backendName}/enable", async (string backendName) =>
{
    var result = await backendManager.SetBackendAvailabilityAsync(backendName, true);
    return Results.Ok(new { 
        BackendName = backendName, 
        Action = "enabled", 
        Success = result,
        Message = result ? $"Backend '{backendName}' has been enabled" : $"Backend '{backendName}' not found"
    });
});

app.MapGet("/admin/backends", async () =>
{
    var status = await backendManager.GetBackendStatusAsync();
    return Results.Ok(status);
});

app.Run();

// Background service for recovery monitoring
public class RecoveryHostedService : BackgroundService
{
    private readonly BackendManager _backendManager;

    public RecoveryHostedService(BackendManager backendManager)
    {
        _backendManager = backendManager;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _backendManager.StartRecoveryMonitor();
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _backendManager.StopRecoveryMonitor();
        return base.StopAsync(cancellationToken);
    }
}
