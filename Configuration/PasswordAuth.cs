using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using BonLivre.Models;

namespace BonLivre.Configuration;

/// <summary>
/// 单一共享密码认证。密码从环境变量 BONLIVRE_PASSWORD 读取。
/// 凭证只走 Authorization: Bearer &lt;pw&gt; header——查询参数会落入 URL 与访问日志，
/// 故不再提供 ?password= 通道。浏览器无法设置 header 的场景各有专责：
/// WebSocket 握手由 /searchBook 处理器在首条消息内自行认证，图片改用 fetch+blob。
/// </summary>
public static class PasswordAuth
{
    public const string EnvVarName = "BONLIVRE_PASSWORD";

    /// <summary>
    /// WS 握手无法携带 Authorization header，此路径豁免中间件强制认证，
    /// 由处理器校验首条消息内的密码并复用同一套失败限流。
    /// </summary>
    public const string FirstMessageAuthPath = "/searchBook";

    private const int DefaultFailureLimit = 10;
    private const int DefaultFailureWindowSeconds = 300;
    private const int DefaultMaxTrackedClients = 4096;
    private const int MaximumFailureLimit = 1000;
    private const int MaximumFailureWindowSeconds = 86400;
    private const int MaximumTrackedClients = 65536;

    // 启动时在 UseSharedPasswordAuth 中一次性赋值，此后只读；null 表示开放模式。
    private static byte[]? _expected;
    private static FailedAuthenticationLimiter? _limiter;

    /// <summary>未设置 BONLIVRE_PASSWORD 时为开放模式，一切请求放行。</summary>
    public static bool IsOpenMode => _expected is null;

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

        _expected = Encoding.UTF8.GetBytes(password);
        _limiter = new FailedAuthenticationLimiter(FailureLimitSettings.FromEnvironment());

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;

            // 放行 CORS 预检（否则跨域请求会被拦截）、根路径、/health 存活探测与首条消息认证的 WS 路径
            if (HttpMethods.IsOptions(context.Request.Method)
                || path == "/" || path == "/health" || path == FirstMessageAuthPath)
            {
                await next();
                return;
            }

            if (IsRequestAuthorized(context))
            {
                await next();
                return;
            }

            if (TryRegisterFailure(context, out var retryAfter))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(
                    new LeagdoApiResponse<string>(false, "未授权：密码错误或缺失", ""),
                    AppJsonSerializerContext.Default.LeagdoApiResponseString);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            await context.Response.WriteAsJsonAsync(
                new LeagdoApiResponse<string>(false, "认证失败次数过多，请稍后重试", ""),
                AppJsonSerializerContext.Default.LeagdoApiResponseString);
        });
    }

    /// <summary>HTTP 通道：仅认 Authorization: Bearer &lt;pw&gt;。开放模式下恒为 true。</summary>
    public static bool IsRequestAuthorized(HttpContext context)
    {
        var expected = _expected;
        if (expected is null) return true;

        var authHeader = context.Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        return FixedTimeEquals(authHeader.Substring("Bearer ".Length), expected);
    }

    /// <summary>WS 首条消息通道：校验消息携带的明文密码。开放模式下恒为 true。</summary>
    public static bool TryValidate(string candidate)
    {
        var expected = _expected;
        if (expected is null) return true;
        return !string.IsNullOrEmpty(candidate) && FixedTimeEquals(candidate, expected);
    }

    /// <summary>登记一次认证失败（按来源 IP 限流）。返回 false 表示已超限，应拒绝并提示稍后再试。</summary>
    public static bool TryRegisterFailure(HttpContext context, out TimeSpan retryAfter)
    {
        var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return _limiter!.TryRegisterFailure(client, out retryAfter);
    }

    // 常量时间比较，防时序侧信道。
    // CryptographicOperations.FixedTimeEquals 在长度不等时抛 ArgumentException，
    // 错误密码与正确密码 UTF-8 字节长度不同会直接 500，故先对齐到定长缓冲再比较。
    private static bool FixedTimeEquals(string provided, byte[] expected)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        // 用 max 长度缓冲，两侧不足部分补 0；再额外比较真实长度，避免前缀碰撞。
        var len = Math.Max(providedBytes.Length, expected.Length);
        if (len == 0) return true;
        var a = new byte[len];
        var b = new byte[len];
        providedBytes.CopyTo(a, 0);
        expected.CopyTo(b, 0);
        // 长度也纳入比较，防止 "ab" 对齐后与 "ab\0" 误判相等。
        var lengthMatch = providedBytes.Length == expected.Length ? 1 : 0;
        return CryptographicOperations.FixedTimeEquals(a, b) & (lengthMatch == 1);
    }

    private sealed record FailureLimitSettings(int Limit, TimeSpan Window, int MaxTrackedClients)
    {
        public static FailureLimitSettings FromEnvironment()
        {
            var limit = ReadPositiveInt("BONLIVRE_AUTH_FAILURE_LIMIT", DefaultFailureLimit, MaximumFailureLimit);
            var windowSeconds = ReadPositiveInt(
                "BONLIVRE_AUTH_FAILURE_WINDOW_SECONDS", DefaultFailureWindowSeconds, MaximumFailureWindowSeconds);
            var maxClients = ReadPositiveInt(
                "BONLIVRE_AUTH_FAILURE_MAX_TRACKED_CLIENTS", DefaultMaxTrackedClients, MaximumTrackedClients);
            return new FailureLimitSettings(limit, TimeSpan.FromSeconds(windowSeconds), maxClients);
        }

        private static int ReadPositiveInt(string name, int defaultValue, int maximum)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) return defaultValue;

            if (int.TryParse(value, out var parsed) && parsed > 0 && parsed <= maximum) return parsed;

            Console.WriteLine($"[PasswordAuth] 警告：{name} 无效，使用默认值 {defaultValue}。");
            return defaultValue;
        }
    }

    private sealed class FailedAuthenticationLimiter(FailureLimitSettings settings)
    {
        private const string OverflowClient = "__overflow__";
        private readonly ConcurrentDictionary<string, FailureWindow> _windows = new();

        /// <summary>登记一次失败。返回 true 时仍处于限额内，应返回 401；false 时应返回 429。</summary>
        public bool TryRegisterFailure(string client, out TimeSpan retryAfter)
        {
            var now = DateTimeOffset.UtcNow;
            PruneExpiredWindows(now);

            var key = _windows.ContainsKey(client) || _windows.Count < settings.MaxTrackedClients
                ? client
                : OverflowClient;
            var window = _windows.GetOrAdd(key, static _ => new FailureWindow());
            return window.TryAdd(now, settings, out retryAfter);
        }

        private void PruneExpiredWindows(DateTimeOffset now)
        {
            foreach (var (client, window) in _windows)
            {
                if (window.IsExpired(now, settings.Window))
                {
                    _windows.TryRemove(new KeyValuePair<string, FailureWindow>(client, window));
                }
            }
        }
    }

    private sealed class FailureWindow
    {
        private readonly Queue<DateTimeOffset> _failures = new();
        private readonly object _lock = new();

        public bool TryAdd(DateTimeOffset now, FailureLimitSettings settings, out TimeSpan retryAfter)
        {
            lock (_lock)
            {
                RemoveExpired(now, settings.Window);
                if (_failures.Count >= settings.Limit)
                {
                    retryAfter = _failures.Peek().Add(settings.Window) - now;
                    return false;
                }

                _failures.Enqueue(now);
                retryAfter = TimeSpan.Zero;
                return true;
            }
        }

        public bool IsExpired(DateTimeOffset now, TimeSpan window)
        {
            lock (_lock)
            {
                RemoveExpired(now, window);
                return _failures.Count == 0;
            }
        }

        private void RemoveExpired(DateTimeOffset now, TimeSpan window)
        {
            while (_failures.TryPeek(out var failure) && failure.Add(window) <= now)
            {
                _failures.Dequeue();
            }
        }
    }
}
