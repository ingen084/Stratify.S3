namespace Stratify.S3.Models;

public class MultipartUpload
{
    public string UploadId { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public DateTime Initiated { get; set; }
    public List<UploadPart> Parts { get; set; } = new();
    public string StorageBackendPath { get; set; } = string.Empty;
    public Dictionary<string, string> Metadata { get; set; } = new();
    public string ContentType { get; set; } = "application/octet-stream";
}

public class UploadPart
{
    public int PartNumber { get; set; }
    public string ETag { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public string FilePath { get; set; } = string.Empty;
}