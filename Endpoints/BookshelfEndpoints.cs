using System.Buffers;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using BonLivre.Models;
using BonLivre.Services;
using BonLivre.Configuration;

namespace BonLivre.Endpoints;

public static class BookshelfEndpoints
{
    private const string ReadConfigKey = "readConfig";
    private const int MaxUploadFilesPerRequest = 10;

    public static void MapBookshelfEndpoints(this IEndpointRouteBuilder app)
    {
        var localService = new LocalBookService();
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

            return Results.Json(new LeagdoApiResponse<List<Book>>(true, "", mergedBooks), AppJsonSerializerContext.Default.LeagdoApiResponseListBook);
        });

        // saveBook / deleteBook 共用：读取请求体并反序列化为 Book。失败返回 null。
        static async Task<Book?> ReadBookAsync(HttpRequest request)
        {
            using var reader = new StreamReader(request.Body);
            var content = await reader.ReadToEndAsync();
            return JsonSerializer.Deserialize(content, AppJsonSerializerContext.Default.Book);
        }

        // saveBook：本地后端的书架完全由 books/ 目录推导（getBookshelf 每次重扫），
        // 没有可写入的书架存储。保留端点是为了 legado 客户端「搜索结果入库」的流程不报错。
        app.MapPost("/saveBook", async (HttpRequest request) =>
        {
            try
            {
                await ReadBookAsync(request);
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
                if (book != null && book.BookUrl.StartsWith("local://"))
                {
                    // 书架内容每次都由 books/ 重扫得出，只从内存移除没有意义——
                    // 必须真正把文件移进 books/.trash/ 回收站（可手动恢复）。
                    localService.DeleteLocalBook(book.BookUrl);
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

        app.MapGet("/searchBookContent", async (HttpContext ctx, string? url, string? key) =>
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
            List<BookContentSearchResult>? results;
            try
            {
                results = await localService.SearchBookContentAsync(
                    url, key, maxResults, ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                return Results.Json(
                    new LeagdoApiResponse<List<BookContentSearchResult>>(false, "请求已取消", []),
                    AppJsonSerializerContext.Default.LeagdoApiResponseListBookContentSearchResult);
            }

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
                // 本地书解析失败必须如实报错：回退到模拟目录会让用户打开一本
                // 有一章「Chapter 1」、内容是占位符的书，而不是看到出了什么问题。
                return chapters != null
                    ? Results.Json(new LeagdoApiResponse<List<BookChapter>>(true, "", chapters), AppJsonSerializerContext.Default.LeagdoApiResponseListBookChapter)
                    : Results.Json(new LeagdoApiResponse<List<BookChapter>>(false, "读取本地书籍目录失败，文件可能已被移动或损坏", []), AppJsonSerializerContext.Default.LeagdoApiResponseListBookChapter);
            }

            var mockChapters = new List<BookChapter> { new BookChapter("Chapter 1", $"{url}/1", 0) };
            return Results.Json(new LeagdoApiResponse<List<BookChapter>>(true, "", mockChapters), AppJsonSerializerContext.Default.LeagdoApiResponseListBookChapter);
        });

        // 分块流式写出正文：手写 JSON 到响应流，章正文逐块做 JSON 字符串转义后 flush，
        // 避免 Results.Json 把整章 string 整体缓冲成大 byte[]。对前端透明（fetch().json() 自动拼合 chunked）。
        app.MapGet("/getBookContent", async (HttpContext ctx, string url, int index) =>
        {
            if (url.StartsWith("local://"))
            {
                ctx.Response.ContentType = "application/json; charset=utf-8";
                var ct = ctx.RequestAborted;
                // 不要 dispose ctx.Response.Body——它归服务器所有，而且失败回退分支
                // 还要继续往同一个响应里写。
                var stream = ctx.Response.Body;
                // 直接用 UTF-8 编码的 byte 缓冲写到响应流，避免 StreamWriter 的同步 Write 路径
                // （Kestrel 默认禁用同步 IO）。逐块转义后 WriteAsync + FlushAsync。
                var wroteHeader = false;
                var escapeBuffer = new ArrayBufferWriter<byte>(16 * 1024);
                try
                {
                    var ok = await localService.StreamChapterContentAsync(
                        url, index,
                        async (chunk, token) =>
                        {
                            if (!wroteHeader)
                            {
                                // 首块到来前先确认响应未提交：此时 Content-Length 未设，Kestrel 自动 chunked。
                                await stream.WriteAsync(JsonHeaderBytes, token).ConfigureAwait(false);
                                wroteHeader = true;
                            }
                            token.ThrowIfCancellationRequested();
                            escapeBuffer.ResetWrittenCount();
                            WriteEscapedJsonStringChunk(escapeBuffer, chunk.Span);
                            await stream.WriteAsync(escapeBuffer.WrittenMemory, token).ConfigureAwait(false);
                            await stream.FlushAsync(token).ConfigureAwait(false);
                        },
                        ct);

                    if (ok && wroteHeader)
                    {
                        await stream.WriteAsync(JsonTailBytes, ct).ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);
                        return;
                    }
                    // ok 但未写出任何块（空章节）：补一个空 data。
                    if (ok)
                    {
                        await stream.WriteAsync(EmptyDataJsonBytes, ct).ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);
                        return;
                    }
                    // ok=false 时必然一块都没写出（wroteHeader 为 false），响应尚未提交，可以正常返回错误。
                    // 本地书读不出来就如实报错，不要拿模拟正文糊弄——用户会以为书坏了却看不出原因。
                    await Results.Json(
                        new LeagdoApiResponse<string>(false, "读取本地章节内容失败，文件可能已被移动或损坏", ""),
                        AppJsonSerializerContext.Default.LeagdoApiResponseString)
                        .ExecuteAsync(ctx);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            // 非 local://：legado 兼容回退（isSuccess 仍为 true，前端不区分真假）。
            await Results.Json(
                new LeagdoApiResponse<string>(true, "", $"这是章节 {index} 的模拟正文内容..."),
                AppJsonSerializerContext.Default.LeagdoApiResponseString)
                .ExecuteAsync(ctx);
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

    // {"isSuccess":true,"errorMsg":"","data":"  —— 不含 BOM 的 UTF-8 字节。
    private static readonly byte[] JsonHeaderBytes = "{\"isSuccess\":true,\"errorMsg\":\"\",\"data\":\""u8.ToArray();
    // "}
    private static readonly byte[] JsonTailBytes = "\"}"u8.ToArray();
    // {"isSuccess":true,"errorMsg":"","data":""}
    private static readonly byte[] EmptyDataJsonBytes = "{\"isSuccess\":true,\"errorMsg\":\"\",\"data\":\"\"}"u8.ToArray();

    /// <summary>
    /// 把一段 char 内容作为 JSON 字符串值的内部片段写进缓冲（不含外层引号）：
    /// 转义 "、\ 与控制字符（&lt; 0x20），其中 \n→\n、\r→\r、\t→\t，其余控制字符走 \uXXXX。
    /// 与 System.Text.Json 的字符串转义规则保持一致，使拼出的整体 JSON 合法。
    /// 按 rune/代理对编码：单 char 路径会把 emoji 等高平面字符拆成两个 U+FFFD。
    /// 整块转义完再由调用方一次写出——早先版本每个字符 await 一次 WriteAsync，
    /// 一章正文就是上万次异步状态机往返。
    /// </summary>
    private static void WriteEscapedJsonStringChunk(IBufferWriter<byte> writer, ReadOnlySpan<char> chunk)
    {
        // 每个 char 最多产出 6 字节（控制字符 \uXXXX）；代理对两个 char 合成 4 字节，不会超。
        var dst = writer.GetSpan(chunk.Length * 6);
        var written = 0;
        for (var i = 0; i < chunk.Length; i++)
        {
            var ch = chunk[i];
            switch (ch)
            {
                case '"': written += CopyAscii(dst[written..], "\\\""u8); break;
                case '\\': written += CopyAscii(dst[written..], "\\\\"u8); break;
                case '\b': written += CopyAscii(dst[written..], "\\b"u8); break;
                case '\f': written += CopyAscii(dst[written..], "\\f"u8); break;
                case '\n': written += CopyAscii(dst[written..], "\\n"u8); break;
                case '\r': written += CopyAscii(dst[written..], "\\r"u8); break;
                case '\t': written += CopyAscii(dst[written..], "\\t"u8); break;
                case < (char)0x20:
                    written += WriteUnicodeEscape(dst[written..], ch);
                    break;
                default:
                    // 高代理 + 低代理成对编码为 4 字节 UTF-8；成对消费两个 char。
                    if (char.IsHighSurrogate(ch) && i + 1 < chunk.Length && char.IsLowSurrogate(chunk[i + 1]))
                    {
                        written += Encoding.UTF8.GetBytes(chunk.Slice(i, 2), dst[written..]);
                        i++; // 消费低代理
                    }
                    else if (char.IsSurrogate(ch))
                    {
                        // 孤立代理（含被分块切开的代理对）：按 JSON \uXXXX 写出。
                        // 相邻两块各写一半会拼成合法的 😀 转义对，客户端解析后仍是原字符。
                        written += WriteUnicodeEscape(dst[written..], ch);
                    }
                    else
                    {
                        written += Encoding.UTF8.GetBytes(chunk.Slice(i, 1), dst[written..]);
                    }
                    break;
            }
        }
        writer.Advance(written);

        static int CopyAscii(Span<byte> dst, ReadOnlySpan<byte> src)
        {
            src.CopyTo(dst);
            return src.Length;
        }
        static int WriteUnicodeEscape(Span<byte> dst, char ch)
        {
            dst[0] = (byte)'\\'; dst[1] = (byte)'u';
            int v = ch;
            for (var i = 5; i >= 2; i--)
            {
                var h = v & 0xF;
                dst[i] = (byte)(h < 10 ? '0' + h : 'A' + h - 10);
                v >>= 4;
            }
            return 6;
        }
    }
}
