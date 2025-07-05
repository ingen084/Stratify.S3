using Stratify.S3.Helpers;
using Stratify.S3.Services;

namespace Stratify.S3.Middleware;

public class AuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AuthenticationService _authService;
    private readonly ILogger<AuthenticationMiddleware> _logger;

    public AuthenticationMiddleware(RequestDelegate next, AuthenticationService authService, ILogger<AuthenticationMiddleware> logger)
    {
        _next = next;
        _authService = authService;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // ヘルスチェックエンドポイントは認証をスキップ
        if (IsPublicEndpoint(context.Request.Path))
        {
            await _next(context);
            return;
        }

        // 管理エンドポイントは別の認証ロジック
        if (IsAdminEndpoint(context.Request.Path))
        {
            var adminAuthResult = await _authService.AuthenticateAdminAsync(context);
            if (!adminAuthResult.IsAuthenticated)
            {
                await HandleAuthenticationFailureAsync(context, adminAuthResult.ErrorMessage ?? "Admin authentication failed");
                return;
            }
            
            if (!string.IsNullOrEmpty(adminAuthResult.UserName))
            {
                context.Items["User"] = adminAuthResult.UserName;
                context.Items["UserPermissions"] = adminAuthResult.Permissions;
                context.Items["IsAdmin"] = true;
            }
            
            await _next(context);
            return;
        }

        var authResult = await _authService.AuthenticateAsync(context);
        
        if (!authResult.IsAuthenticated)
        {
            await HandleAuthenticationFailureAsync(context, authResult.ErrorMessage ?? "Authentication failed");
            return;
        }

        // 認証成功時はユーザー情報をコンテキストに追加
        if (!string.IsNullOrEmpty(authResult.UserName))
        {
            context.Items["User"] = authResult.UserName;
            context.Items["UserPermissions"] = authResult.Permissions;
        }

        await _next(context);
    }

    private bool IsPublicEndpoint(PathString path)
    {
        var pathValue = path.Value?.ToLowerInvariant() ?? "";
        
        return pathValue switch
        {
            "/health" => true,
            "/metrics" => true,
            "/" when pathValue == "/" => false, // S3 ListBuckets - 認証が必要
            _ => false
        };
    }
    
    private bool IsAdminEndpoint(PathString path)
    {
        var pathValue = path.Value?.ToLowerInvariant() ?? "";
        return pathValue.StartsWith("/admin/");
    }

    private async Task HandleAuthenticationFailureAsync(HttpContext context, string errorMessage)
    {
        _logger.LogWarning("パス {Path} の認証に失敗しました: {Error}", context.Request.Path, errorMessage);
        
        context.Response.StatusCode = 403;
        context.Response.ContentType = "application/xml";
        
        var errorXml = S3XmlHelper.CreateErrorResponse(
            "AccessDenied", 
            errorMessage, 
            context.Request.Path.Value ?? "/"
        );
        
        await context.Response.WriteAsync(errorXml);
    }
}