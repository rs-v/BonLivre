using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using BonLivre.Models;
using BonLivre.Services;
using BonLivre.Configuration;

namespace BonLivre.Endpoints;

public static class BookshelfEndpoints
{
    private static List<Book> _bookshelf = new();
    // _bookshelf 采用 copy-on-write：Results.Json 在 handler 返回（锁已释放）后才真正序列化，
    // 所以任何已交给 Results.Json 的 List 实例都不能再被原地修改；
    // 修改一律在锁内“复制 → 改副本 → 替换引用”，锁只保护这一步。
    private static readonly object _bookshelfLock = new();

    private const string ReadConfigKey = "readConfig";
    private const int MaxUploadFilesPerRequest = 10;

    public static void MapBookshelfEndpoints(this IEndpointRouteBuilder app)
    {
        var localService = new LocalBookService();
        lock (_bookshelfLock)
        {
            _bookshelf = localService.GetLocalBooks();
        }
        var database = app.ServiceProvider.GetRequiredService<LiteDbStore>();
        var progressStore = new BookProgressStore(database);
        var bookmarkStore = new BookmarkStore(database);
        var settingsStore = new SettingsStore(database);

        app.MapGet("/getReadConfig", () =>
        {
            var readConfig = settingsStore.Get(ReadConfigKey, "{}");
            return Results.Json(new LeagdoApiResponse<string>(true, "", readConfig), AppJsonSerializerContext.Default.LeagdoApiResponseString);
        });

        app.MapPost("/saveReadConfig", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var readConfig = await reader.ReadToEndAsync();
            settingsStore.Set(ReadConfigKey, readConfig);
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

        app.MapGet("/getBookmarks", (string? bookUrl) =>
        {
            if (string.IsNullOrWhiteSpace(bookUrl))
            {
                return Results.Json(
                    new LeagdoApiResponse<List<Bookmark>>(false, "缺少书籍地址", []),
                    AppJsonSerializerContext.Default.LeagdoApiResponseListBookmark);
            }

            var bookmarks = bookmarkStore.GetByBookUrl(bookUrl);
            return Results.Json(
                new LeagdoApiResponse<List<Bookmark>>(true, "", bookmarks),
                AppJsonSerializerContext.Default.LeagdoApiResponseListBookmark);
        });

        app.MapPost("/createBookmark", async (HttpRequest request) =>
        {
            try
            {
                var bookmarkRequest = await JsonSerializer.DeserializeAsync(
                    request.Body,
                    AppJsonSerializerContext.Default.CreateBookmarkRequest,
                    request.HttpContext.RequestAborted);
                if (bookmarkRequest == null ||
                    string.IsNullOrWhiteSpace(bookmarkRequest.BookUrl) ||
                    bookmarkRequest.ChapterIndex < 0 ||
                    bookmarkRequest.ChapterPos < 0)
                {
                    return Results.Json(
                        new LeagdoApiResponse<Bookmark>(false, "书签位置无效", new Bookmark(0, "", 0, 0, 0)),
                        AppJsonSerializerContext.Default.LeagdoApiResponseBookmark);
                }

                var bookmark = bookmarkStore.Create(
                    bookmarkRequest.BookUrl,
                    bookmarkRequest.ChapterIndex,
                    bookmarkRequest.ChapterPos);
                if (bookmark == null)
                {
                    return Results.Json(
                        new LeagdoApiResponse<Bookmark>(false, "当前位置已有书签", new Bookmark(0, "", 0, 0, 0)),
                        AppJsonSerializerContext.Default.LeagdoApiResponseBookmark);
                }

                return Results.Json(
                    new LeagdoApiResponse<Bookmark>(true, "", bookmark),
                    AppJsonSerializerContext.Default.LeagdoApiResponseBookmark);
            }
            catch (OperationCanceledException)
            {
                return Results.Json(
                    new LeagdoApiResponse<Bookmark>(false, "请求已取消", new Bookmark(0, "", 0, 0, 0)),
                    AppJsonSerializerContext.Default.LeagdoApiResponseBookmark);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[createBookmark Error]: {ex}");
                return Results.Json(
                    new LeagdoApiResponse<Bookmark>(false, "添加书签失败", new Bookmark(0, "", 0, 0, 0)),
                    AppJsonSerializerContext.Default.LeagdoApiResponseBookmark);
            }
        });

        app.MapPost("/deleteBookmark", async (HttpRequest request) =>
        {
            try
            {
                var bookmarkRequest = await JsonSerializer.DeserializeAsync(
                    request.Body,
                    AppJsonSerializerContext.Default.DeleteBookmarkRequest,
                    request.HttpContext.RequestAborted);
                if (bookmarkRequest == null ||
                    string.IsNullOrWhiteSpace(bookmarkRequest.BookUrl) ||
                    bookmarkRequest.Id <= 0)
                {
                    return Results.Json(
                        new LeagdoApiResponse<string>(false, "书签无效", ""),
                        AppJsonSerializerContext.Default.LeagdoApiResponseString);
                }

                if (!bookmarkStore.Delete(bookmarkRequest.BookUrl, bookmarkRequest.Id))
                {
                    return Results.Json(
                        new LeagdoApiResponse<string>(false, "书签不存在或已删除", ""),
                        AppJsonSerializerContext.Default.LeagdoApiResponseString);
                }

                return Results.Json(
                    new LeagdoApiResponse<string>(true, "", ""),
                    AppJsonSerializerContext.Default.LeagdoApiResponseString);
            }
            catch (OperationCanceledException)
            {
                return Results.Json(
                    new LeagdoApiResponse<string>(false, "请求已取消", ""),
                    AppJsonSerializerContext.Default.LeagdoApiResponseString);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[deleteBookmark Error]: {ex}");
                return Results.Json(
                    new LeagdoApiResponse<string>(false, "删除书签失败", ""),
                    AppJsonSerializerContext.Default.LeagdoApiResponseString);
            }
        });

        app.MapGet("/getBookshelf", () =>
        {
            var books = localService.GetLocalBooks();
            var allProgress = progressStore.GetAllProgress();

            var mergedBooks = books.Select(book =>
            {
                // 优先按唯一的 BookUrl 匹配；迁移自旧库的行 BookUrl 是合成键
                // 'local://' + 旧 Name（旧 Name 即无扩展名文件名），其次按它匹配；
                // 最后回退到 (Name, Author)。
                var legacyKey = $"local://{Path.GetFileNameWithoutExtension(book.BookUrl.Replace("local://", ""))}";
                var progress = allProgress.FirstOrDefault(p => p.BookUrl == book.BookUrl)
                    ?? allProgress.FirstOrDefault(p => p.BookUrl == legacyKey)
                    ?? allProgress.FirstOrDefault(p => p.Name == book.Name && p.Author == book.Author);
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

            lock (_bookshelfLock)
            {
                _bookshelf = mergedBooks;
            }
            // 序列化局部 mergedBooks，而非共享字段：saveBook/deleteBook 会替换 _bookshelf 引用，
            // 但绝不原地改动，故这个局部列表在序列化期间不会被并发修改。
            return Results.Json(new LeagdoApiResponse<List<Book>>(true, "", mergedBooks), AppJsonSerializerContext.Default.LeagdoApiResponseListBook);
        });

        // saveBook / deleteBook 共用：读取请求体并反序列化为 Book。失败返回 null。
        static async Task<Book?> ReadBookAsync(HttpRequest request)
        {
            using var reader = new StreamReader(request.Body);
            var content = await reader.ReadToEndAsync();
            return JsonSerializer.Deserialize(content, AppJsonSerializerContext.Default.Book);
        }

        app.MapPost("/saveBook", async (HttpRequest request) =>
        {
            try
            {
                var book = await ReadBookAsync(request);
                if (book != null)
                {
                    lock (_bookshelfLock)
                    {
                        // copy-on-write：新建列表并替换引用，绝不原地修改可能正被序列化的旧列表。
                        var updated = _bookshelf.Where(b => b.BookUrl != book.BookUrl).ToList();
                        updated.Add(book);
                        _bookshelf = updated;
                    }
                }
                return Results.Json(new LeagdoApiResponse<string>(true, "", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[saveBook Error]: {ex.Message}");
                return Results.Json(new LeagdoApiResponse<string>(false, "解析书籍数据失败", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);
            }
        });

        app.MapPost("/deleteBook", async (HttpRequest request) =>
        {
            try
            {
                var book = await ReadBookAsync(request);
                if (book != null)
                {
                    lock (_bookshelfLock)
                    {
                        // copy-on-write，同 saveBook。
                        _bookshelf = _bookshelf.Where(b => b.BookUrl != book.BookUrl).ToList();
                    }
                    // 仅移内存的话，下一次 getBookshelf 重扫 books/ 书又会回来。
                    // 真正把文件移入 books/.trash/ 回收站（可手动恢复）。
                    if (book.BookUrl.StartsWith("local://"))
                    {
                        localService.DeleteLocalBook(book.BookUrl);
                    }
                }
                return Results.Json(new LeagdoApiResponse<string>(true, "", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[deleteBook Error]: {ex.Message}");
                return Results.Json(new LeagdoApiResponse<string>(false, "解析书籍数据失败", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);
            }
        });

        // 上传书籍：multipart/form-data，字段名任意，取每个上传文件的原始文件名存入 books/。
        // ?overwrite=true 允许覆盖同名文件。前端暂无对应入口，供脚本/curl 使用：
        //   curl -F "file=@book.epub" http://host:5000/uploadBook
        app.MapPost("/uploadBook", async (HttpRequest request, bool? overwrite) =>
        {
            try
            {
                if (!request.HasFormContentType)
                {
                    return Results.Json(new LeagdoApiResponse<string>(false, "需要 multipart/form-data 上传", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);
                }

                var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
                if (form.Files.Count == 0)
                {
                    return Results.Json(new LeagdoApiResponse<string>(false, "未收到文件", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);
                }
                if (form.Files.Count > MaxUploadFilesPerRequest)
                {
                    return Results.Json(
                        new LeagdoApiResponse<string>(false, $"单次最多上传 {MaxUploadFilesPerRequest} 个文件", ""),
                        AppJsonSerializerContext.Default.LeagdoApiResponseString);
                }

                var saved = new List<string>();
                foreach (var file in form.Files)
                {
                    await using var stream = file.OpenReadStream();
                    var (ok, message, bookUrl) = await localService.SaveUploadedBookAsync(
                        file.FileName, stream, overwrite == true);
                    if (!ok)
                    {
                        return Results.Json(new LeagdoApiResponse<string>(false, $"{file.FileName}: {message}", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);
                    }
                    saved.Add(bookUrl);
                }
                return Results.Json(new LeagdoApiResponse<string>(true, "", string.Join("\n", saved)), AppJsonSerializerContext.Default.LeagdoApiResponseString);
            }
            catch (OperationCanceledException)
            {
                return Results.Json(new LeagdoApiResponse<string>(false, "请求已取消", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[uploadBook Error]: {ex}");
                return Results.Json(new LeagdoApiResponse<string>(false, "保存上传文件失败", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);
            }
        });

        app.MapGet("/searchBookContent", (string? url, string? key) =>
        {
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("local://", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(
                    new LeagdoApiResponse<List<BookContentSearchResult>>(false, "仅支持搜索本地书籍", []),
                    AppJsonSerializerContext.Default.LeagdoApiResponseListBookContentSearchResult);
            }

            key = key?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                return Results.Json(
                    new LeagdoApiResponse<List<BookContentSearchResult>>(false, "请输入搜索内容", []),
                    AppJsonSerializerContext.Default.LeagdoApiResponseListBookContentSearchResult);
            }
            if (key.Length > 100)
            {
                return Results.Json(
                    new LeagdoApiResponse<List<BookContentSearchResult>>(false, "搜索内容不能超过 100 个字符", []),
                    AppJsonSerializerContext.Default.LeagdoApiResponseListBookContentSearchResult);
            }

            const int maxResults = 100;
            var results = localService.SearchBookContent(url, key, maxResults);
            if (results == null)
            {
                return Results.Json(
                    new LeagdoApiResponse<List<BookContentSearchResult>>(false, "搜索书籍内容失败", []),
                    AppJsonSerializerContext.Default.LeagdoApiResponseListBookContentSearchResult);
            }
            return Results.Json(
                new LeagdoApiResponse<List<BookContentSearchResult>>(true, "", results),
                AppJsonSerializerContext.Default.LeagdoApiResponseListBookContentSearchResult);
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
            if (path.StartsWith("local://"))
            {
                if (path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
                {
                    var cover = localService.GetEpubCover(path);
                    if (cover != null) return Results.File(cover, LocalBookService.DetectImageMime(cover));
                }

                // EPUB 无内嵌封面、或 TXT 书籍：用标题生成一张 SVG 占位封面。
                var title = localService.GetLocalBookTitle(path);
                var svg = LocalBookService.GenerateTitleCoverSvg(title);
                return Results.File(svg, "image/svg+xml");
            }
            // 非本地封面：仅允许重定向到合法的 http(s) 绝对地址，避免开放重定向。
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return Results.Redirect(uri.AbsoluteUri);
            }
            return Results.NotFound();
        });

        app.MapGet("/image", (string url, string path, int? width) =>
        {
            try
            {
                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(path))
                    return Results.BadRequest("Missing url or path");

                if (url.StartsWith("local://") && url.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
                {
                    var resource = localService.GetEpubResource(url, path);
                    if (resource != null && resource.Value.Content != null)
                    {
                        return Results.File(resource.Value.Content, resource.Value.MimeType);
                    }
                }
                return Results.NotFound();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Image EndPoint Critical Error]: url={url}, path={path}\n{ex}");
                return Results.Problem(
                    detail: "读取图片资源失败",
                    statusCode: 500,
                    title: "EPUB Image Resource Error"
                );
            }
        });
    }
}
