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
builder.Services.AddSingleton<MultipartUploadManager>();
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
var multipartUploadManager = app.Services.GetRequiredService<MultipartUploadManager>();
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

// Permission check helper functions
static bool CheckPermission(HttpContext context, string requiredOperation, string bucket = "*")
{
    // Admin operations always require admin permission
    if (requiredOperation == "admin")
    {
        return context.Items.ContainsKey("IsAdmin") && (bool)context.Items["IsAdmin"];
    }
    
    // Get user permissions from context (set by authentication middleware)
    if (context.Items.TryGetValue("UserPermissions", out var permissionsObj) && 
        permissionsObj is string[] permissions)
    {
        return permissions.Contains("*") || permissions.Contains(requiredOperation);
    }
    
    // Fallback: check if user is authenticated
    var user = context.Items["User"] as string;
    return !string.IsNullOrEmpty(user);
}

static IResult PermissionDenied(string operation, string resource)
{
    return Results.Content(
        S3XmlHelper.CreateErrorResponse("AccessDenied", 
            $"Operation '{operation}' not permitted on resource '{resource}'", 
            resource),
        "application/xml",
        statusCode: 403
    );
}

// S3 API Endpoints

// ListBuckets
app.MapGet("/", async (HttpContext context) =>
{
    // Check read permission
    if (!CheckPermission(context, "read", "*"))
    {
        return PermissionDenied("read", "/");
    }
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
app.MapGet("/{bucket}", async (string bucket, string? prefix, string? delimiter, int? maxKeys, string? marker, HttpContext context) =>
{
    bucket = HttpUtility.UrlDecode(bucket);
    prefix = HttpUtility.UrlDecode(prefix ?? "");
    maxKeys ??= 1000;
    marker ??= "";
    
    // Check read permission
    if (!CheckPermission(context, "read", bucket))
    {
        return PermissionDenied("read", $"/{bucket}");
    }

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
                var etag = ETagService.ComputeETag(fileObject.Key, fileObject.Size, fileObject.LastModified);
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

// HeadObject - explicit mapping
app.MapMethods("/{bucket}/{**key}", ["HEAD"], async (string bucket, string key, HttpContext context) =>
{
    bucket = HttpUtility.UrlDecode(bucket);
    key = HttpUtility.UrlDecode(key);
    
    // Check read permission
    if (!CheckPermission(context, "read", bucket))
    {
        return PermissionDenied("read", $"/{bucket}/{key}");
    }
    
    logger.LogInformation("Processing HEAD request for {Bucket}/{Key}", bucket, key);
    var relativePath = Path.Combine(bucket, key);
    var fileLocation = await backendManager.FindFileWithFallbackAsync(relativePath);
    if (fileLocation == null)
    {
        context.Response.StatusCode = 404;
        return Results.Empty;
    }

    var fileInfo = new FileInfo(fileLocation.Path);
    var etag = ETagService.ComputeETag(fileInfo, key);

    context.Response.Headers.ETag = $"\"{etag}\"";
    context.Response.Headers.LastModified = fileInfo.LastWriteTimeUtc.ToString("R");
    context.Response.Headers.AcceptRanges = "bytes";
    context.Response.Headers.ContentLength = fileInfo.Length;
    context.Response.Headers.ContentType = "application/octet-stream";

    return Results.Empty;
});

// GetObject / ListParts
app.MapGet("/{bucket}/{**key}", async (string bucket, string key, HttpContext context) =>
{
    bucket = HttpUtility.UrlDecode(bucket);
    key = HttpUtility.UrlDecode(key);
    
    // Check read permission
    if (!CheckPermission(context, "read", bucket))
    {
        return PermissionDenied("read", $"/{bucket}/{key}");
    }
    
    // Check if this is a List Parts request
    if (context.Request.Query.ContainsKey("uploadId"))
    {
        var uploadId = context.Request.Query["uploadId"].ToString();
        var upload = multipartUploadManager.GetUpload(uploadId);
        
        if (upload == null)
        {
            return Results.Content(
                S3XmlHelper.CreateErrorResponse("NoSuchUpload", "指定されたマルチパートアップロードは存在しません", $"/{bucket}/{key}"),
                "application/xml",
                statusCode: 404
            );
        }

        var partNumberMarker = 0;
        if (context.Request.Query.ContainsKey("part-number-marker"))
        {
            int.TryParse(context.Request.Query["part-number-marker"], out partNumberMarker);
        }

        var maxParts = 1000;
        if (context.Request.Query.ContainsKey("max-parts"))
        {
            int.TryParse(context.Request.Query["max-parts"], out maxParts);
            maxParts = Math.Min(Math.Max(maxParts, 1), 1000);
        }

        var parts = multipartUploadManager.GetParts(uploadId);
        var filteredParts = parts.Where(p => p.PartNumber > partNumberMarker).Take(maxParts + 1).ToList();
        
        var isTruncated = filteredParts.Count > maxParts;
        if (isTruncated)
        {
            filteredParts = filteredParts.Take(maxParts).ToList();
        }

        var nextPartNumberMarker = isTruncated && filteredParts.Any() ? filteredParts.Last().PartNumber : 0;
        
        return Results.Content(
            S3XmlHelper.CreateListPartsResponse(
                bucket, key, uploadId, partNumberMarker, nextPartNumberMarker, 
                maxParts, isTruncated, filteredParts),
            "application/xml"
        );
    }
    
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

    var etag = ETagService.ComputeETag(fileInfo, key);

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

// Create Bucket (must be before PutObject endpoint)
app.MapPut("/{bucket:regex(^[a-z0-9.-]{{3,63}}$)}", async (string bucket, HttpContext context) =>
{
    // Check write permission for bucket creation
    if (!CheckPermission(context, "write", bucket))
    {
        return PermissionDenied("write", $"/{bucket}");
    }
    
    // Ensure this is a bucket creation request (no additional path segments)
    var path = context.Request.Path.Value;
    if (path != null && path.Count(c => c == '/') == 1) // Only one slash (bucket name only)
    {
        try
        {
            logger.LogInformation("Creating bucket: {Bucket}", bucket);
            
            // Check if bucket already exists
            var existingBuckets = await backendManager.GetAllBucketsAsync();
            if (existingBuckets.Contains(bucket))
            {
                // S3 behavior: return 200 if bucket exists and you own it
                return Results.Ok();
            }
            
            // Create bucket directory in available backends
            var availableBackends = await backendManager.GetAvailableBackendsAsync();
            if (availableBackends.Count == 0)
            {
                return Results.Content(
                    S3XmlHelper.CreateErrorResponse("ServiceUnavailable", "利用可能なバックエンドがありません", $"/{bucket}"),
                    "application/xml",
                    statusCode: 503
                );
            }
            
            // Create bucket in primary backend (highest priority available)
            var primaryBackend = availableBackends.OrderBy(b => b.Priority).First();
            var bucketPath = Path.Combine(primaryBackend.Path, bucket);
            
            Directory.CreateDirectory(bucketPath);
            
            logger.LogInformation("Successfully created bucket {Bucket} in backend {Backend}", bucket, primaryBackend.Name);
            return Results.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create bucket {Bucket}", bucket);
            return Results.Content(
                S3XmlHelper.CreateErrorResponse("InternalError", "バケットの作成に失敗しました", $"/{bucket}"),
                "application/xml",
                statusCode: 500
            );
        }
    }
    
    return Results.NotFound();
});

// PutObject / UploadPart
app.MapPut("/{bucket}/{**key}", async (string bucket, string key, HttpContext context) =>
{
    bucket = HttpUtility.UrlDecode(bucket);
    key = HttpUtility.UrlDecode(key);
    
    // Check write permission
    if (!CheckPermission(context, "write", bucket))
    {
        return PermissionDenied("write", $"/{bucket}/{key}");
    }

    // Check if this is an Upload Part request
    var query = context.Request.Query;
    if (query.ContainsKey("partNumber") && query.ContainsKey("uploadId"))
    {
        var uploadId = query["uploadId"].ToString();
        if (!int.TryParse(query["partNumber"], out var partNumber) || partNumber < 1 || partNumber > 10000)
        {
            return Results.Content(
                S3XmlHelper.CreateErrorResponse("InvalidArgument", "パート番号は1から10000の間である必要があります", $"/{bucket}/{key}"),
                "application/xml",
                statusCode: 400
            );
        }

        var upload = multipartUploadManager.GetUpload(uploadId);
        if (upload == null)
        {
            return Results.Content(
                S3XmlHelper.CreateErrorResponse("NoSuchUpload", "指定されたマルチパートアップロードは存在しません", $"/{bucket}/{key}"),
                "application/xml",
                statusCode: 404
            );
        }

        using var memoryStream = new MemoryStream();
        await context.Request.Body.CopyToAsync(memoryStream);
        var data = memoryStream.ToArray();

        if (multipartUploadManager.AddPart(uploadId, partNumber, data, out var etag))
        {
            context.Response.Headers.ETag = $"\"{etag}\"";
            return Results.Ok();
        }

        return Results.Content(
            S3XmlHelper.CreateErrorResponse("InternalError", "パートのアップロードに失敗しました", $"/{bucket}/{key}"),
            "application/xml",
            statusCode: 500
        );
    }

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
        var etag = ETagService.ComputeETag(key, content.Length, DateTime.UtcNow);

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

// Delete Bucket (must be before DeleteObject endpoint)
app.MapDelete("/{bucket:regex(^[a-z0-9.-]{{3,63}}$)}", async (string bucket, HttpContext context) =>
{
    // Check delete permission
    if (!CheckPermission(context, "delete", bucket))
    {
        return PermissionDenied("delete", $"/{bucket}");
    }
    
    // Ensure this is a bucket deletion request (no additional path segments)
    var path = context.Request.Path.Value;
    if (path != null && path.Count(c => c == '/') == 1) // Only one slash (bucket name only)
    {
        try
        {
            logger.LogInformation("Deleting bucket: {Bucket}", bucket);
            
            // Check if bucket exists
            var existingBuckets = await backendManager.GetAllBucketsAsync();
            if (!existingBuckets.Contains(bucket))
            {
                return Results.Content(
                    S3XmlHelper.CreateErrorResponse("NoSuchBucket", "指定されたバケットは存在しません", $"/{bucket}"),
                    "application/xml",
                    statusCode: 404
                );
            }
            
            // Check if bucket is empty by checking all backends
            bool bucketHasFiles = false;
            var backends = await backendManager.GetAvailableBackendsAsync();
            
            foreach (var backend in backends)
            {
                var bucketPath = Path.Combine(backend.Path, bucket);
                if (Directory.Exists(bucketPath))
                {
                    var files = Directory.GetFiles(bucketPath, "*", SearchOption.AllDirectories);
                    if (files.Length > 0)
                    {
                        bucketHasFiles = true;
                        break;
                    }
                }
            }
            
            if (bucketHasFiles)
            {
                return Results.Content(
                    S3XmlHelper.CreateErrorResponse("BucketNotEmpty", "バケットが空ではないため削除できません", $"/{bucket}"),
                    "application/xml",
                    statusCode: 409
                );
            }
            
            // Delete bucket from all backends
            foreach (var backend in backends)
            {
                var bucketPath = Path.Combine(backend.Path, bucket);
                if (Directory.Exists(bucketPath))
                {
                    try
                    {
                        Directory.Delete(bucketPath, false); // false = don't delete if not empty
                        logger.LogInformation("Deleted bucket {Bucket} from backend {Backend}", bucket, backend.Name);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to delete bucket {Bucket} from backend {Backend}", bucket, backend.Name);
                    }
                }
            }
            
            logger.LogInformation("Successfully deleted bucket {Bucket}", bucket);
            return Results.NoContent();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete bucket {Bucket}", bucket);
            return Results.Content(
                S3XmlHelper.CreateErrorResponse("InternalError", "バケットの削除に失敗しました", $"/{bucket}"),
                "application/xml",
                statusCode: 500
            );
        }
    }
    
    return Results.NotFound();
});

// DeleteObject / AbortMultipartUpload
app.MapDelete("/{bucket}/{**key}", async (string bucket, string key, HttpContext context) =>
{
    bucket = HttpUtility.UrlDecode(bucket);
    key = HttpUtility.UrlDecode(key);
    
    // Check delete permission
    if (!CheckPermission(context, "delete", bucket))
    {
        return PermissionDenied("delete", $"/{bucket}/{key}");
    }
    
    // Check if this is an Abort Multipart Upload request
    if (context.Request.Query.ContainsKey("uploadId"))
    {
        var uploadId = context.Request.Query["uploadId"].ToString();
        
        if (multipartUploadManager.AbortUpload(uploadId))
        {
            return Results.NoContent();
        }

        return Results.Content(
            S3XmlHelper.CreateErrorResponse("NoSuchUpload", "指定されたマルチパートアップロードは存在しません", $"/{bucket}/{key}"),
            "application/xml",
            statusCode: 404
        );
    }
    
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

// Initiate Multipart Upload / Complete Multipart Upload
app.MapPost("/{bucket}/{**key}", async (string bucket, string key, HttpContext context) =>
{
    bucket = HttpUtility.UrlDecode(bucket);
    key = HttpUtility.UrlDecode(key);
    var query = context.Request.Query;

    // Initiate Multipart Upload
    if (query.ContainsKey("uploads"))
    {
        var metadata = new Dictionary<string, string>();
        var contentType = context.Request.ContentType ?? "application/octet-stream";
        
        foreach (var header in context.Request.Headers)
        {
            if (header.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
            {
                metadata[header.Key] = header.Value.ToString();
            }
        }

        // Note: We'll use a temporary path for initiation, actual backend selection happens during completion
        var uploadId = multipartUploadManager.InitiateUpload(bucket, key, "/tmp", metadata, contentType);
        return Results.Content(
            S3XmlHelper.CreateInitiateMultipartUploadResponse(bucket, key, uploadId),
            "application/xml"
        );
    }

    // Complete Multipart Upload
    if (query.ContainsKey("uploadId"))
    {
        var uploadId = query["uploadId"].ToString();
        var upload = multipartUploadManager.GetUpload(uploadId);
        
        if (upload == null)
        {
            return Results.Content(
                S3XmlHelper.CreateErrorResponse("NoSuchUpload", "指定されたマルチパートアップロードは存在しません", $"/{bucket}/{key}"),
                "application/xml",
                statusCode: 404
            );
        }

        // Parse request body to get the list of parts
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync();
        
        var parts = new List<UploadPart>();
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(body);
            var ns = doc.Root?.Name.Namespace ?? "";
            
            foreach (var partElem in doc.Descendants(ns + "Part"))
            {
                var partNumber = int.Parse(partElem.Element(ns + "PartNumber")?.Value ?? "0");
                var etag = partElem.Element(ns + "ETag")?.Value?.Trim('"') ?? "";
                
                var existingPart = upload.Parts.FirstOrDefault(p => p.PartNumber == partNumber && p.ETag == etag);
                if (existingPart != null)
                {
                    parts.Add(existingPart);
                }
            }
        }
        catch
        {
            return Results.Content(
                S3XmlHelper.CreateErrorResponse("MalformedXML", "提供されたXMLは整形式ではありません", $"/{bucket}/{key}"),
                "application/xml",
                statusCode: 400
            );
        }

        if (multipartUploadManager.CompleteUpload(uploadId, parts, out var tempFilePath))
        {
            try
            {
                // Read the completed file and use existing WriteFileWithFallbackAsync
                var fileContent = await File.ReadAllBytesAsync(tempFilePath);
                var relativePath = Path.Combine(bucket, key);
                var success = await backendManager.WriteFileWithFallbackAsync(relativePath, fileContent);
                
                if (!success)
                {
                    return Results.Content(
                        S3XmlHelper.CreateErrorResponse("ServiceUnavailable", "利用可能なバックエンドがありません", $"/{bucket}/{key}"),
                        "application/xml",
                        statusCode: 503
                    );
                }

                // Generate ETag for the completed file
                var etag = ETagService.ComputeETag(key, fileContent.Length, DateTime.UtcNow);
                
                var location = $"{context.Request.Scheme}://{context.Request.Host}/{bucket}/{key}";
                return Results.Content(
                    S3XmlHelper.CreateCompleteMultipartUploadResponse(location, bucket, key, etag),
                    "application/xml"
                );
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "マルチパートアップロード完了後のファイル保存に失敗しました: {Bucket}/{Key}", bucket, key);
                return Results.Content(
                    S3XmlHelper.CreateErrorResponse("InternalError", "ファイルの保存に失敗しました", $"/{bucket}/{key}"),
                    "application/xml",
                    statusCode: 500
                );
            }
        }

        return Results.Content(
            S3XmlHelper.CreateErrorResponse("InternalError", "マルチパートアップロードの完了に失敗しました", $"/{bucket}/{key}"),
            "application/xml",
            statusCode: 500
        );
    }

    return Results.BadRequest();
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
