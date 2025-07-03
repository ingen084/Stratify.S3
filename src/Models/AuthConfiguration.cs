namespace Stratify.S3.Models;

public class AuthConfiguration
{
    public bool Enabled { get; set; } = false;
    public AuthMode Mode { get; set; } = AuthMode.ApiKey;
    public int TokenExpirationMinutes { get; set; } = 60;
    public List<ApiKeyConfig> ApiKeys { get; set; } = new();
    public List<AwsCredentialConfig> AwsCredentials { get; set; } = new();
}

public enum AuthMode
{
    None,
    ApiKey,
    AwsSignature,
    Both
}

public class ApiKeyConfig
{
    public string Name { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string[] AllowedOperations { get; set; } = { "*" }; // "*", "read", "write", "delete", "admin"
    public string[] AllowedBuckets { get; set; } = { "*" };
    public bool Enabled { get; set; } = true;
}

public class AwsCredentialConfig
{
    public string AccessKeyId { get; set; } = string.Empty;
    public string SecretAccessKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string[] AllowedOperations { get; set; } = { "*" };
    public string[] AllowedBuckets { get; set; } = { "*" };
    public bool Enabled { get; set; } = true;
}