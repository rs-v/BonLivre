using BonLivre.Endpoints;
using BonLivre.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5000", "http://0.0.0.0:5001");

// 配置 CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// 配置 JSON 选项，支持 Native AOT
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

var app = builder.Build();

app.UseCors("AllowAll");
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromMinutes(2)
});

// 托管前端 SPA（wwwroot/，由 csproj 的 BuildFrontend target 在 publish 时填充）：
// 先 UseDefaultFiles 让 / 映射到 index.html，再 UseStaticFiles 提供资源。
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var headers = ctx.Context.Response.Headers;
        // index.html 文件名固定，必须 no-cache，否则用户拿到旧首页会加载到已删除的旧 hash 资源；
        // 带内容 hash 的资源（Vite 产物）文件名随内容变化，可长期 immutable 缓存。
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            headers.CacheControl = "no-cache";
        else
            headers.CacheControl = "public,max-age=31536000,immutable";
    }
});

// 共享密码认证：放在静态文件之后（前端页面不保护），API 与 WS 端点之前（受保护）
app.UseSharedPasswordAuth();

// 注册模块化路由
app.MapBookshelfEndpoints();
app.MapSourceEndpoints();

// 存活探测：/ 已交给前端 index.html，探测挪到 /health
app.MapGet("/health", () => "BonLiver Backend (Modular Minimal API) is running.");

app.Run();
