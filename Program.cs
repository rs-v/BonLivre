using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

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

// 配置 JSON 选项，使用源代码生成上下文以支持 AOT
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

var app = builder.Build();

app.UseCors("AllowAll");
app.UseWebSockets(); // 启用 WebSocket 支持
app.UseStaticFiles();

// 模拟数据存储
var bookshelf = new List<Book>();
var bookSources = new List<object>();
var readConfig = "{}";

// 初始化本地书籍
var booksDir = Path.Combine(Directory.GetCurrentDirectory(), "books");
if (!Directory.Exists(booksDir)) Directory.CreateDirectory(booksDir);

var localFiles = Directory.GetFiles(booksDir, "*.txt");
foreach (var file in localFiles)
{
    var fileName = Path.GetFileNameWithoutExtension(file);
    bookshelf.Add(new Book(
        fileName,
        "本地作者",
        $"local://{Path.GetFileName(file)}",
        $"local://{Path.GetFileName(file)}",
        "local",
        "本地导入",
        0, 0, 0));
}

// --- 书架 API ---

// 获取阅读配置
app.MapGet("/getReadConfig", () =>
    Results.Json(new LeagdoApiResponse<string>(true, "", readConfig), AppJsonSerializerContext.Default.LeagdoApiResponseString));

// 保存阅读配置
app.MapPost("/saveReadConfig", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    readConfig = await reader.ReadToEndAsync();
    return Results.Json(new LeagdoApiResponse<string>(true, "", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);
});

// 保存书籍进度
app.MapPost("/saveBookProgress", ([FromBody] BookProgress progress) =>
{
    var book = bookshelf.FirstOrDefault(b => b.name == progress.name && b.author == progress.author);
    if (book != null)
    {
        // 演示逻辑：记录中更新建议使用数据库，这里仅返回成功
    }
    return Results.Json(new LeagdoApiResponse<string>(true, "", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);
});

// 获取书架
app.MapGet("/getBookshelf", () =>
    Results.Json(new LeagdoApiResponse<List<Book>>(true, "", bookshelf), AppJsonSerializerContext.Default.LeagdoApiResponseListBook));

// 保存书籍
app.MapPost("/saveBook", ([FromBody] Book book) =>
{
    bookshelf.RemoveAll(b => b.bookUrl == book.bookUrl);
    bookshelf.Add(book);
    return Results.Json(new LeagdoApiResponse<string>(true, "", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);
});

// 删除书籍
app.MapPost("/deleteBook", ([FromBody] Book book) =>
{
    bookshelf.RemoveAll(b => b.bookUrl == book.bookUrl);
    return Results.Json(new LeagdoApiResponse<string>(true, "", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);
});

// 获取目录
app.MapGet("/getChapterList", (string url) =>
{
    if (url.StartsWith("local://"))
    {
        var fileName = url.Replace("local://", "");
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "books", fileName);
        if (File.Exists(filePath))
        {
            var content = File.ReadAllText(filePath, Encoding.UTF8);
            var chapters = new List<BookChapter>();
            var matches = Regex.Matches(content, @"^\s*(第[零一二三四五六七八九十百千万\d]+[章节回卷集部篇]).*$", RegexOptions.Multiline);

            if (matches.Count == 0)
            {
                chapters.Add(new BookChapter("正文", url, 0));
            }
            else
            {
                for (int i = 0; i < matches.Count; i++)
                {
                    chapters.Add(new BookChapter(matches[i].Value.Trim(), $"{url}#{i}", i));
                }
            }
            return Results.Json(new LeagdoApiResponse<List<BookChapter>>(true, "", chapters), AppJsonSerializerContext.Default.LeagdoApiResponseListBookChapter);
        }
    }

    var mockChapters = new List<BookChapter>
    {
        new BookChapter("Chapter 1", $"{url}/1", 0),
        new BookChapter("Chapter 2", $"{url}/2", 1)
    };
    return Results.Json(new LeagdoApiResponse<List<BookChapter>>(true, "", mockChapters), AppJsonSerializerContext.Default.LeagdoApiResponseListBookChapter);
});

// 获取正文
app.MapGet("/getBookContent", (string url, int index) =>
{
    if (url.StartsWith("local://"))
    {
        var rawUrl = url.Split('#')[0];
        var fileName = rawUrl.Replace("local://", "");
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "books", fileName);

        if (File.Exists(filePath))
        {
            var content = File.ReadAllText(filePath, Encoding.UTF8);
            var matches = Regex.Matches(content, @"^\s*(第[零一二三四五六七八九十百千万\d]+[章节回卷集部篇]).*$", RegexOptions.Multiline);

            if (matches.Count == 0) return Results.Json(new LeagdoApiResponse<string>(true, "", content), AppJsonSerializerContext.Default.LeagdoApiResponseString);

            int startPos = matches[index].Index;
            int endPos = (index + 1 < matches.Count) ? matches[index + 1].Index : content.Length;

            var chapterContent = content.Substring(startPos, endPos - startPos);
            return Results.Json(new LeagdoApiResponse<string>(true, "", chapterContent), AppJsonSerializerContext.Default.LeagdoApiResponseString);
        }
    }
    return Results.Json(new LeagdoApiResponse<string>(true, "", $"这是章节 {index} 的模拟正文内容..."), AppJsonSerializerContext.Default.LeagdoApiResponseString);
});

// --- 书源 API ---

app.MapGet("/getBookSources", () =>
    Results.Json(new LeagdoApiResponse<List<object>>(true, "", bookSources), AppJsonSerializerContext.Default.LeagdoApiResponseListObject));

app.MapPost("/saveBookSource", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var content = await reader.ReadToEndAsync();
    return Results.Json(new LeagdoApiResponse<string>(true, "", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);
});

// --- WebSocket 模拟 ---
app.MapGet("/searchBook", async (HttpContext context) =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        // 简单模拟搜索过程
        await Task.Delay(1000);
        await webSocket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
    }
    else
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
    }
});

// --- 代理 API ---
app.MapGet("/cover", (string path) => Results.Redirect(path));
app.MapGet("/image", (string path, string url, int width) => Results.Redirect(path));

app.MapGet("/", () => "BonLiver Backend (Minimal API) is running.");

app.Run();

// --- 模型定义 ---

public record Book(
    string name,
    string author,
    string bookUrl,
    string tocUrl,
    string origin,
    string originName,
    int type,
    int durChapterIndex,
    int durChapterPos,
    string? coverUrl = null
);

public record BookProgress(
    string name,
    string author,
    int durChapterIndex,
    int durChapterPos,
    long durChapterTime,
    string durChapterTitle
);

public record BookChapter(
    string title,
    string url,
    int index
);

// API 响应包装类
public record LeagdoApiResponse<T>(bool isSuccess, string errorMsg, T data);

// --- JSON 源代码生成上下文 ---
[JsonSerializable(typeof(Book))]
[JsonSerializable(typeof(BookProgress))]
[JsonSerializable(typeof(BookChapter))]
[JsonSerializable(typeof(List<Book>))]
[JsonSerializable(typeof(List<BookChapter>))]
[JsonSerializable(typeof(LeagdoApiResponse<string>))]
[JsonSerializable(typeof(LeagdoApiResponse<List<Book>>))]
[JsonSerializable(typeof(LeagdoApiResponse<List<BookChapter>>))]
[JsonSerializable(typeof(LeagdoApiResponse<List<object>>))]
[JsonSerializable(typeof(List<object>))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}
