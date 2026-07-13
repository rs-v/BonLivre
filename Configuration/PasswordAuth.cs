using System.Security.Cryptography;
using System.Text;
using BonLivre.Models;

namespace BonLivre.Configuration;

/// <summary>
/// 单一共享密码认证。密码从环境变量 BONLIVRE_PASSWORD 读取。
/// 凭证支持两条通道：HTTP 走 Authorization: Bearer &lt;pw&gt; header，
/// 浏览器无法设置 header 的场景（WebSocket、&lt;img src&gt;、sendBeacon）走 ?password= query。
/// </summary>
public static class PasswordAuth
{
    public const string EnvVarName = "BONLIVRE_PASSWORD";

    public static void UseSharedPasswordAuth(this WebApplication app)
    {
        var password = Environment.GetEnvironmentVariable(EnvVarName);

        if (string.IsNullOrEmpty(password))
        {
            // 开放模式：未设置密码时放行所有请求，向后兼容。启动时告警一次。
            Console.WriteLine(
                $"[PasswordAuth] 警告：未设置环境变量 {EnvVarName}，后端处于开放模式（无需密码即可访问）。");
            return;
        }

        // 预先编码，避免每个请求重复分配
        var expected = Encoding.UTF8.GetBytes(password);

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;

            // 放行 CORS 预检（否则跨域请求会被拦截）与根路径存活探测
            if (HttpMethods.IsOptions(context.Request.Method) || path == "/")
            {
                await next();
                return;
            }

            if (IsAuthorized(context, expected))
            {
                await next();
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(
                new LeagdoApiResponse<string>(false, "未授权：密码错误或缺失", ""),
                AppJsonSerializerContext.Default.LeagdoApiResponseString);
        });
    }

    private static bool IsAuthorized(HttpContext context, byte[] expected)
    {
        // 1. HTTP 通道：Authorization: Bearer <pw>
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            var token = authHeader.Substring("Bearer ".Length);
            if (FixedTimeEquals(token, expected)) return true;
        }

        // 2. 浏览器无 header 通道：?password=
        var queryPassword = context.Request.Query["password"].ToString();
        if (!string.IsNullOrEmpty(queryPassword) && FixedTimeEquals(queryPassword, expected))
        {
            return true;
        }

        return false;
    }

    // 常量时间比较，防时序侧信道
    private static bool FixedTimeEquals(string provided, byte[] expected)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        return CryptographicOperations.FixedTimeEquals(providedBytes, expected);
    }
}
