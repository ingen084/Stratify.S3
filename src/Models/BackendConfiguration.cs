namespace Stratify.S3.Models;

public class BackendConfiguration
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int Priority { get; set; }
    public bool Available { get; set; } = true;
    public double LastCheck { get; set; }
    public int CheckInterval { get; set; } = 30;
    public double Timeout { get; set; } = 5.0;
    public int MaxRetries { get; set; } = 2;
}

public class AppConfiguration
{
    public int ChunkSize { get; set; } = 8192;
    public long MaxFileSize { get; set; } = 10L * 1024 * 1024 * 1024;
    public string HealthCheckFile { get; set; } = ".s3proxy_health";
    public string MetadataDir { get; set; } = ".s3proxy_metadata";
    public bool AutoRecoveryEnabled { get; set; } = true;
    public int RecoveryCheckInterval { get; set; } = 300;
    public int RecoveryBatchSize { get; set; } = 10;
    public int RecoveryTimeout { get; set; } = 300;
    public bool PreferPrimary { get; set; } = true;
}

public class FileLocation
{
    public BackendConfiguration Backend { get; set; } = null!;
    public string Path { get; set; } = string.Empty;
}

public class RecoveryCandidate
{
    public string SourcePath { get; set; } = string.Empty;
    public string SourceBackend { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public class FileObjectInfo
{
    public string Key { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public string BackendName { get; set; } = string.Empty;
    public int BackendPriority { get; set; }
    public string FilePath { get; set; } = string.Empty;
}