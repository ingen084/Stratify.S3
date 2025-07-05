using Stratify.S3.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Stratify.S3.Services;

public class AuthenticationService
{
    private readonly AuthConfiguration _authConfig;
    private readonly ILogger<AuthenticationService> _logger;

    public AuthenticationService(IConfiguration configuration, ILogger<AuthenticationService> logger)
    {
        _authConfig = configuration.GetSection("Authentication").Get<AuthConfiguration>() ?? new();
        _logger = logger;
    }

    public async Task<AuthResult> AuthenticateAsync(HttpContext context)
    {
        if (!_authConfig.Enabled)
        {
            return AuthResult.Success();
        }

        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        var apiKeyHeader = context.Request.Headers["X-API-Key"].FirstOrDefault();

        // API Key認証を試行
        if (_authConfig.Mode is AuthMode.ApiKey or AuthMode.Both && !string.IsNullOrEmpty(apiKeyHeader))
        {
            return await AuthenticateApiKeyAsync(apiKeyHeader, context);
        }

        // AWS署名認証を試行
        if (_authConfig.Mode is AuthMode.AwsSignature or AuthMode.Both && !string.IsNullOrEmpty(authHeader))
        {
            return await AuthenticateAwsSignatureAsync(authHeader, context);
        }

        _logger.LogWarning("認証が必要ですが、有効な認証情報が提供されていません");
        return AuthResult.Failure("Authentication required");
    }

    private Task<AuthResult> AuthenticateApiKeyAsync(string apiKey, HttpContext context)
    {
        var keyConfig = _authConfig.ApiKeys.FirstOrDefault(k => k.Key == apiKey && k.Enabled);
        if (keyConfig == null)
        {
            _logger.LogWarning("無効なAPIキーが提供されました");
            return Task.FromResult(AuthResult.Failure("Invalid API key"));
        }

        var operation = GetOperationFromRequest(context.Request);
        var bucket = GetBucketFromRequest(context.Request);

        if (!IsOperationAllowed(keyConfig.AllowedOperations, operation) ||
            !IsBucketAllowed(keyConfig.AllowedBuckets, bucket))
        {
            _logger.LogWarning("APIキー {KeyName} にはバケット {Bucket} での操作 {Operation} が許可されていません", 
                keyConfig.Name, bucket, operation);
            return Task.FromResult(AuthResult.Failure("Operation not permitted"));
        }

        _logger.LogInformation("APIキー認証が成功しました: {KeyName}", keyConfig.Name);
        return Task.FromResult(AuthResult.Success(keyConfig.Name, keyConfig.AllowedOperations));
    }

    private async Task<AuthResult> AuthenticateAwsSignatureAsync(string authHeader, HttpContext context)
    {
        try
        {
            // AWS Signature V4の解析
            var signatureData = ParseAwsSignature(authHeader);
            if (signatureData == null)
            {
                return AuthResult.Failure("Invalid AWS signature format");
            }

            var credential = _authConfig.AwsCredentials.FirstOrDefault(c => 
                c.AccessKeyId == signatureData.AccessKeyId && c.Enabled);
            
            if (credential == null)
            {
                _logger.LogWarning("未知のアクセスキー: {AccessKeyId}", signatureData.AccessKeyId);
                return AuthResult.Failure("Invalid access key");
            }

            // 署名を検証
            if (!await VerifyAwsSignatureAsync(context.Request, signatureData, credential.SecretAccessKey))
            {
                _logger.LogWarning("AWS署名の検証が失敗しました: {AccessKeyId}", signatureData.AccessKeyId);
                return AuthResult.Failure("Signature verification failed");
            }

            var operation = GetOperationFromRequest(context.Request);
            var bucket = GetBucketFromRequest(context.Request);

            if (!IsOperationAllowed(credential.AllowedOperations, operation) ||
                !IsBucketAllowed(credential.AllowedBuckets, bucket))
            {
                _logger.LogWarning("認証情報 {Name} にはバケット {Bucket} での操作 {Operation} が許可されていません", 
                    credential.Name, bucket, operation);
                return AuthResult.Failure("Operation not permitted");
            }

            _logger.LogInformation("AWS署名認証が成功しました: {Name}", credential.Name);
            return AuthResult.Success(credential.Name, credential.AllowedOperations);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AWS署名認証中にエラーが発生しました");
            return AuthResult.Failure("Authentication error");
        }
    }

    private AwsSignatureData? ParseAwsSignature(string authHeader)
    {
        // AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20230101/us-east-1/s3/aws4_request, SignedHeaders=host;range;x-amz-date, Signature=...
        var match = Regex.Match(authHeader, 
            @"AWS4-HMAC-SHA256 Credential=([^/]+)/([^/]+)/([^/]+)/([^/]+)/([^,]+),\s*SignedHeaders=([^,]+),\s*Signature=(.+)");
        
        if (!match.Success)
            return null;

        return new AwsSignatureData
        {
            AccessKeyId = match.Groups[1].Value,
            Date = match.Groups[2].Value,
            Region = match.Groups[3].Value,
            Service = match.Groups[4].Value,
            RequestType = match.Groups[5].Value,
            SignedHeaders = match.Groups[6].Value,
            Signature = match.Groups[7].Value
        };
    }

    private async Task<bool> VerifyAwsSignatureAsync(HttpRequest request, AwsSignatureData signatureData, string secretKey)
    {
        try
        {
            // AWS Signature V4の検証
            
            var dateHeader = request.Headers["X-Amz-Date"].FirstOrDefault() ?? 
                            request.Headers["Date"].FirstOrDefault();
            
            if (string.IsNullOrEmpty(dateHeader))
            {
                _logger.LogWarning("X-Amz-DateまたはDateヘッダーが見つかりません");
                return false;
            }

            // タイムスタンプの有効性チェック（15分以内）
            if (DateTime.TryParseExact(dateHeader, "yyyyMMddTHHmmssZ", null, 
                System.Globalization.DateTimeStyles.AssumeUniversal, out var requestTime))
            {
                var timeDiff = Math.Abs((DateTime.UtcNow - requestTime).TotalMinutes);
                if (timeDiff > 15)
                {
                    _logger.LogWarning("リクエストのタイムスタンプが古すぎるか、未来すぎます: {Minutes} 分", timeDiff);
                    return false;
                }
            }
            else
            {
                _logger.LogWarning("日付ヘッダーの形式が無効です: {DateHeader}", dateHeader);
                return false;
            }

            // 署名検証を実行
            var canonicalRequest = await BuildCanonicalRequestAsync(request, signatureData.SignedHeaders);
            var stringToSign = BuildStringToSign(signatureData, canonicalRequest, dateHeader);
            var calculatedSignature = CalculateSignature(secretKey, signatureData, stringToSign);

            var isValid = calculatedSignature.Equals(signatureData.Signature, StringComparison.OrdinalIgnoreCase);
            
            if (!isValid)
            {
                _logger.LogWarning("AWS署名の検証が失敗しました。計算された署名: {Calculated}, 提供された署名: {Provided}", 
                    calculatedSignature, signatureData.Signature);
                _logger.LogDebug("StringToSign: {StringToSign}", stringToSign);
                _logger.LogDebug("CanonicalRequest: {CanonicalRequest}", canonicalRequest);
            }

            return isValid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AWS署名の検証中にエラーが発生しました");
            return false;
        }
    }

    private Task<string> BuildCanonicalRequestAsync(HttpRequest request, string signedHeaders)
    {
        var method = request.Method.ToUpperInvariant();
        var uri = request.Path.Value ?? "/";
        
        // クエリ文字列の正規化
        var queryString = "";
        if (!string.IsNullOrEmpty(request.QueryString.Value))
        {
            var queryParams = new List<string>();
            var pairs = request.QueryString.Value.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                var keyValue = pair.Split('=', 2);
                var key = Uri.EscapeDataString(keyValue[0]);
                var value = keyValue.Length > 1 ? Uri.EscapeDataString(keyValue[1]) : "";
                queryParams.Add($"{key}={value}");
            }
            queryParams.Sort();
            queryString = string.Join("&", queryParams);
        }
        
        var headers = new StringBuilder();
        var headerNames = signedHeaders.Split(';').OrderBy(h => h).ToArray();
        
        foreach (var headerName in headerNames)
        {
            var headerValue = request.Headers[headerName].FirstOrDefault() ?? "";
            // ヘッダー値の正規化（連続する空白を1つの空白に）
            headerValue = System.Text.RegularExpressions.Regex.Replace(headerValue.Trim(), @"\s+", " ");
            headers.Append($"{headerName.ToLowerInvariant()}:{headerValue}\n");
        }

        // ペイロードハッシュを取得（通常はX-Amz-Content-Sha256ヘッダーから）
        var hashedPayload = request.Headers["X-Amz-Content-Sha256"].FirstOrDefault() ?? "UNSIGNED-PAYLOAD";
        if (string.IsNullOrEmpty(hashedPayload))
        {
            hashedPayload = "UNSIGNED-PAYLOAD";
        }

        var canonicalRequest = $"{method}\n{uri}\n{queryString}\n{headers}\n{signedHeaders}\n{hashedPayload}";
        return Task.FromResult(canonicalRequest);
    }

    private string BuildStringToSign(AwsSignatureData signatureData, string canonicalRequest, string dateHeader)
    {
        var hashedCanonicalRequest = ComputeSHA256Hash(canonicalRequest);
        var scope = $"{signatureData.Date}/{signatureData.Region}/{signatureData.Service}/{signatureData.RequestType}";
        
        return $"AWS4-HMAC-SHA256\n{dateHeader}\n{scope}\n{hashedCanonicalRequest}";
    }

    private string CalculateSignature(string secretKey, AwsSignatureData signatureData, string stringToSign)
    {
        var dateKey = ComputeHMACSHA256($"AWS4{secretKey}", signatureData.Date);
        var regionKey = ComputeHMACSHA256(dateKey, signatureData.Region);
        var serviceKey = ComputeHMACSHA256(regionKey, signatureData.Service);
        var requestKey = ComputeHMACSHA256(serviceKey, signatureData.RequestType);
        var signature = ComputeHMACSHA256(requestKey, stringToSign);
        
        return Convert.ToHexString(signature).ToLowerInvariant();
    }

    private byte[] ComputeHMACSHA256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private byte[] ComputeHMACSHA256(string key, string data)
    {
        return ComputeHMACSHA256(Encoding.UTF8.GetBytes(key), data);
    }

    private string ComputeSHA256Hash(string data)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public Task<AuthResult> AuthenticateAdminAsync(HttpContext context)
    {
        if (!_authConfig.Enabled)
        {
            return Task.FromResult(AuthResult.Success());
        }

        var apiKeyHeader = context.Request.Headers["X-Admin-Key"].FirstOrDefault();
        
        // 管理APIキー認証のみをサポート
        if (string.IsNullOrEmpty(apiKeyHeader))
        {
            _logger.LogWarning("管理APIキーが提供されていません");
            return Task.FromResult(AuthResult.Failure("Admin API key required"));
        }

        var keyConfig = _authConfig.ApiKeys.FirstOrDefault(k => 
            k.Key == apiKeyHeader && 
            k.Enabled && 
            IsOperationAllowed(k.AllowedOperations, "admin"));
            
        if (keyConfig == null)
        {
            _logger.LogWarning("無効な管理APIキーが提供されました");
            return Task.FromResult(AuthResult.Failure("Invalid admin API key"));
        }

        _logger.LogInformation("管理APIキー認証が成功しました: {KeyName}", keyConfig.Name);
        return Task.FromResult(AuthResult.Success(keyConfig.Name, keyConfig.AllowedOperations));
    }

    private string GetOperationFromRequest(HttpRequest request)
    {
        return request.Method.ToLowerInvariant() switch
        {
            "get" => "read",
            "put" => "write",
            "post" => "write",
            "delete" => "delete",
            _ => "unknown"
        };
    }

    private string GetBucketFromRequest(HttpRequest request)
    {
        var path = request.Path.Value?.TrimStart('/') ?? "";
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? segments[0] : "*";
    }

    private bool IsOperationAllowed(string[] allowedOperations, string operation)
    {
        return allowedOperations.Contains("*") || allowedOperations.Contains(operation);
    }

    private bool IsBucketAllowed(string[] allowedBuckets, string bucket)
    {
        return allowedBuckets.Contains("*") || allowedBuckets.Contains(bucket);
    }
}

public class AuthResult
{
    public bool IsAuthenticated { get; set; }
    public string? UserName { get; set; }
    public string? ErrorMessage { get; set; }
    public string[] Permissions { get; set; } = Array.Empty<string>();

    public static AuthResult Success(string? userName = null, string[]? permissions = null) => new()
    {
        IsAuthenticated = true,
        UserName = userName,
        Permissions = permissions ?? Array.Empty<string>()
    };

    public static AuthResult Failure(string errorMessage) => new()
    {
        IsAuthenticated = false,
        ErrorMessage = errorMessage
    };
}

public class AwsSignatureData
{
    public string AccessKeyId { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string RequestType { get; set; } = string.Empty;
    public string SignedHeaders { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
}