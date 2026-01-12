using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using BonLivre.Models;
using BonLivre.Services;
using BonLivre.Configuration;

namespace BonLivre.Endpoints;

public static class BookshelfEndpoints
{
    private static List<Book> _bookshelf = new();
    private static string _readConfig = "{}";

    public static void MapBookshelfEndpoints(this IEndpointRouteBuilder app)
    {
        var localService = new LocalBookService();
        _bookshelf = localService.GetLocalBooks();
        var progressStore = new BookProgressStore();

        app.MapGet("/getReadConfig", () =>
            Results.Json(new LeagdoApiResponse<string>(true, "", _readConfig), AppJsonSerializerContext.Default.LeagdoApiResponseString));

        app.MapPost("/saveReadConfig", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            _readConfig = await reader.ReadToEndAsync();
            return Results.Json(new LeagdoApiResponse<string>(true, "", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);
        });

        app.MapPost("/saveBookProgress", async (HttpRequest request) =>
        {
            try
            {
                var progress = await JsonSerializer.DeserializeAsync(
                    request.Body,
                    AppJsonSerializerContext.Default.BookProgress,
                    request.HttpContext.RequestAborted);

                if (progress == null)
                {
                    return Results.Json(
                        new LeagdoApiResponse<string>(false, "缺少书籍进度数据", ""),
                        AppJsonSerializerContext.Default.LeagdoApiResponseString);
                }

                progressStore.Save(progress);
                return Results.Json(new LeagdoApiResponse<string>(true, "", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);
            }
            catch (OperationCanceledException)
            {
                return Results.Json(
                    new LeagdoApiResponse<string>(false, "请求已取消", ""),
                    AppJsonSerializerContext.Default.LeagdoApiResponseString);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[saveBookProgress Error]: {ex}");
                return Results.Json(
                    new LeagdoApiResponse<string>(false, "保存进度失败", ""),
                    AppJsonSerializerContext.Default.LeagdoApiResponseString);
            }
        });

        app.MapGet("/getBookshelf", () =>
        {
            var books = localService.GetLocalBooks();
            var allProgress = progressStore.GetAllProgress();

            var mergedBooks = books.Select(book =>
            {
                var progress = allProgress.FirstOrDefault(p => p.Name == book.Name && p.Author == book.Author);
                if (progress != null)
                {
                    return book with
                    {
                        DurChapterIndex = progress.DurChapterIndex,
                        DurChapterPos = progress.DurChapterPos,
                        DurChapterTime = progress.DurChapterTime,
                        DurChapterTitle = progress.DurChapterTitle
                    };
                }
                return book;
            }).ToList();

            _bookshelf = mergedBooks;
            return Results.Json(new LeagdoApiResponse<List<Book>>(true, "", _bookshelf), AppJsonSerializerContext.Default.LeagdoApiResponseListBook);
        });

        app.MapPost("/saveBook", async (HttpRequest request) =>
        {
            try {
                using var reader = new StreamReader(request.Body);
                var content = await reader.ReadToEndAsync();
                var book = System.Text.Json.JsonSerializer.Deserialize(content, AppJsonSerializerContext.Default.Book);
                if (book != null) {
                    _bookshelf.RemoveAll(b => b.BookUrl == book.BookUrl);
                    _bookshelf.Add(book);
                }
                return Results.Json(new LeagdoApiResponse<string>(true, "", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);
            } catch {
                return Results.Json(new LeagdoApiResponse<string>(false, "Deserialization failed", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);
            }
        });

        app.MapPost("/deleteBook", async (HttpRequest request) =>
        {
             try {
                using var reader = new StreamReader(request.Body);
                var content = await reader.ReadToEndAsync();
                var book = System.Text.Json.JsonSerializer.Deserialize(content, AppJsonSerializerContext.Default.Book);
                if (book != null) {
                    _bookshelf.RemoveAll(b => b.BookUrl == book.BookUrl);
                }
                return Results.Json(new LeagdoApiResponse<string>(true, "", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);
            } catch {
                return Results.Json(new LeagdoApiResponse<string>(false, "Deserialization failed", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);
            }
        });

        app.MapGet("/getChapterList", (string url) =>
        {
            if (url.StartsWith("local://"))
            {
                var chapters = localService.GetChapterList(url);
                if (chapters != null)
                {
                    return Results.Json(new LeagdoApiResponse<List<BookChapter>>(true, "", chapters), AppJsonSerializerContext.Default.LeagdoApiResponseListBookChapter);
                }
            }

            var mockChapters = new List<BookChapter> { new BookChapter("Chapter 1", $"{url}/1", 0) };
            return Results.Json(new LeagdoApiResponse<List<BookChapter>>(true, "", mockChapters), AppJsonSerializerContext.Default.LeagdoApiResponseListBookChapter);
        });

        app.MapGet("/getBookContent", (string url, int index) =>
        {
            if (url.StartsWith("local://"))
            {
                var content = localService.GetBookContent(url, index);
                if (content != null)
                {
                    return Results.Json(new LeagdoApiResponse<string>(true, "", content), AppJsonSerializerContext.Default.LeagdoApiResponseString);
                }
            }
            return Results.Json(new LeagdoApiResponse<string>(true, "", $"这是章节 {index} 的模拟正文内容..."), AppJsonSerializerContext.Default.LeagdoApiResponseString);
        });

        app.MapGet("/cover", (string path) =>
        {
            if (path.StartsWith("local://") && path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
            {
                var cover = localService.GetEpubCover(path);
                if (cover != null) return Results.File(cover, "image/jpeg");
            }
            return Results.Redirect(path);
        });

        app.MapGet("/image", (string url, string path) =>
        {
            try
            {
                if (url.StartsWith("local://") && url.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
                {
                    var resource = localService.GetEpubResource(url, path);
                    if (resource != null)
                    {
                        return Results.File(resource.Value.Content, resource.Value.MimeType);
                    }
                }
                return Results.NotFound();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Image EndPoint Error]: {ex}");
                return Results.Problem(ex.Message);
            }
        });
    }
}
