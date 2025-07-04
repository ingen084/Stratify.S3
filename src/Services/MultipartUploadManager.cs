using System.Collections.Concurrent;
using Stratify.S3.Models;

namespace Stratify.S3.Services;

public class MultipartUploadManager
{
    private readonly ConcurrentDictionary<string, MultipartUpload> _uploads = new();
    private readonly ILogger<MultipartUploadManager> _logger;
    private readonly string _tempDirectory;

    public MultipartUploadManager(ILogger<MultipartUploadManager> logger, IConfiguration configuration)
    {
        _logger = logger;
        _tempDirectory = configuration["MultipartUpload:TempDirectory"] ?? Path.Combine(Path.GetTempPath(), "stratify-s3-multipart");
        Directory.CreateDirectory(_tempDirectory);
    }

    public string InitiateUpload(string bucket, string key, string storageBackendPath, Dictionary<string, string> metadata, string contentType)
    {
        var uploadId = GenerateUploadId();
        var upload = new MultipartUpload
        {
            UploadId = uploadId,
            Bucket = bucket,
            Key = key,
            Initiated = DateTime.UtcNow,
            StorageBackendPath = storageBackendPath,
            Metadata = metadata,
            ContentType = contentType
        };

        _uploads[uploadId] = upload;
        
        var uploadDir = GetUploadDirectory(uploadId);
        Directory.CreateDirectory(uploadDir);
        
        _logger.LogInformation("Initiated multipart upload {UploadId} for {Bucket}/{Key}", uploadId, bucket, key);
        return uploadId;
    }

    public MultipartUpload? GetUpload(string uploadId)
    {
        return _uploads.TryGetValue(uploadId, out var upload) ? upload : null;
    }

    public bool AddPart(string uploadId, int partNumber, byte[] data, out string etag)
    {
        etag = string.Empty;
        
        if (!_uploads.TryGetValue(uploadId, out var upload))
        {
            _logger.LogWarning("Upload {UploadId} not found", uploadId);
            return false;
        }

        var partPath = GetPartPath(uploadId, partNumber);
        
        try
        {
            File.WriteAllBytes(partPath, data);
            etag = ETagService.ComputeContentETag(data);
            
            var part = new UploadPart
            {
                PartNumber = partNumber,
                ETag = etag,
                Size = data.Length,
                LastModified = DateTime.UtcNow,
                FilePath = partPath
            };

            lock (upload.Parts)
            {
                upload.Parts.RemoveAll(p => p.PartNumber == partNumber);
                upload.Parts.Add(part);
                upload.Parts.Sort((a, b) => a.PartNumber.CompareTo(b.PartNumber));
            }

            _logger.LogInformation("Added part {PartNumber} to upload {UploadId}", partNumber, uploadId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add part {PartNumber} to upload {UploadId}", partNumber, uploadId);
            return false;
        }
    }

    public bool CompleteUpload(string uploadId, List<UploadPart> parts, out string finalPath)
    {
        finalPath = string.Empty;
        
        if (!_uploads.TryGetValue(uploadId, out var upload))
        {
            _logger.LogWarning("Upload {UploadId} not found", uploadId);
            return false;
        }

        try
        {
            var uploadDir = GetUploadDirectory(uploadId);
            finalPath = Path.Combine(upload.StorageBackendPath, upload.Bucket, upload.Key);
            var finalDir = Path.GetDirectoryName(finalPath);
            
            if (!string.IsNullOrEmpty(finalDir))
            {
                Directory.CreateDirectory(finalDir);
            }

            using (var finalStream = new FileStream(finalPath, FileMode.Create, FileAccess.Write))
            {
                foreach (var part in parts.OrderBy(p => p.PartNumber))
                {
                    var storedPart = upload.Parts.FirstOrDefault(p => p.PartNumber == part.PartNumber && p.ETag == part.ETag);
                    if (storedPart == null)
                    {
                        _logger.LogError("Part {PartNumber} with ETag {ETag} not found for upload {UploadId}", 
                            part.PartNumber, part.ETag, uploadId);
                        return false;
                    }

                    using (var partStream = File.OpenRead(storedPart.FilePath))
                    {
                        partStream.CopyTo(finalStream);
                    }
                }
            }

            CleanupUpload(uploadId);
            _uploads.TryRemove(uploadId, out _);
            
            _logger.LogInformation("Completed multipart upload {UploadId} to {FinalPath}", uploadId, finalPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete upload {UploadId}", uploadId);
            return false;
        }
    }

    public bool AbortUpload(string uploadId)
    {
        if (_uploads.TryRemove(uploadId, out var upload))
        {
            CleanupUpload(uploadId);
            _logger.LogInformation("Aborted multipart upload {UploadId}", uploadId);
            return true;
        }
        
        return false;
    }

    public List<UploadPart> GetParts(string uploadId)
    {
        if (_uploads.TryGetValue(uploadId, out var upload))
        {
            lock (upload.Parts)
            {
                return new List<UploadPart>(upload.Parts);
            }
        }
        
        return new List<UploadPart>();
    }

    public void CleanupExpiredUploads(TimeSpan expiration)
    {
        var cutoff = DateTime.UtcNow - expiration;
        var expiredUploads = _uploads.Where(kvp => kvp.Value.Initiated < cutoff).ToList();

        foreach (var kvp in expiredUploads)
        {
            AbortUpload(kvp.Key);
            _logger.LogInformation("Cleaned up expired upload {UploadId}", kvp.Key);
        }
    }

    private string GenerateUploadId()
    {
        return Guid.NewGuid().ToString("N");
    }

    private string GetUploadDirectory(string uploadId)
    {
        return Path.Combine(_tempDirectory, uploadId);
    }

    private string GetPartPath(string uploadId, int partNumber)
    {
        return Path.Combine(GetUploadDirectory(uploadId), $"part-{partNumber:D8}");
    }

    private void CleanupUpload(string uploadId)
    {
        try
        {
            var uploadDir = GetUploadDirectory(uploadId);
            if (Directory.Exists(uploadDir))
            {
                Directory.Delete(uploadDir, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup upload directory for {UploadId}", uploadId);
        }
    }

}