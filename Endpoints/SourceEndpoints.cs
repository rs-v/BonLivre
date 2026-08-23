using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BonLivre.Models;
using BonLivre.Configuration;
using BonLivre.Services;

namespace BonLivre.Endpoints;

public static class SourceEndpoints
{
    private static List<object> _bookSources = new();

    public static void MapSourceEndpoints(this IEndpointRouteBuilder app)
    {
        var localService = new LocalBookService();

        app.MapGet("/getBookSources", () =>
            Results.Json(new LeagdoApiResponse<List<object>>(true, "", _bookSources), AppJsonSerializerContext.Default.LeagdoApiResponseListObject));

        // rss 源与书源的批量保存/删除：本地后端不支持源，全部返回成功空结果，
        // 让前端源编辑页可以打开而不是收到 404 报错。
        app.MapGet("/getRssSources", () =>
            Results.Json(new LeagdoApiResponse<List<object>>(true, "", new List<object>()), AppJsonSerializerContext.Default.LeagdoApiResponseListObject));

        // 保存/删除类端点共用的成功空响应。请求体不读取也无妨：中间件会替我们排干。
        static IResult OkStub() =>
            Results.Json(new LeagdoApiResponse<string>(true, "", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);

        app.MapPost("/saveBookSource", OkStub);
        app.MapPost("/saveBookSources", OkStub);
        app.MapPost("/deleteBookSources", OkStub);
        app.MapPost("/saveRssSource", OkStub);
        app.MapPost("/saveRssSources", OkStub);
        app.MapPost("/deleteRssSources", OkStub);

        // 源调试 WebSocket 桩：接受连接，说明本地后端不支持调试，然后正常关闭，
        // 避免前端调试面板报连接错误。
        static void MapDebugStub(IEndpointRouteBuilder routes, string path)
        {
            routes.Map(path, async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                using var ws = await context.WebSockets.AcceptWebSocketAsync();
                var msg = Encoding.UTF8.GetBytes("本地后端不支持源调试。");
                await ws.SendAsync(new ArraySegment<byte>(msg), WebSocketMessageType.Text, true, CancellationToken.None);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
            });
        }
        MapDebugStub(app, "/bookSourceDebug");
        MapDebugStub(app, "/rssSourceDebug");

        app.Map("/searchBook", async (context) =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                Console.WriteLine("[WebSocket] Incoming connection request...");
                using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                Console.WriteLine("[WebSocket] Connection accepted.");
                var buffer = new byte[1024 * 4];

                try
                {
                    while (webSocket.State == WebSocketState.Open)
                    {
                        using var ms = new MemoryStream();
                        WebSocketReceiveResult receiveResult;
                        try
                        {
                            do
                            {
                                receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                                ms.Write(buffer, 0, receiveResult.Count);
                            } while (!receiveResult.EndOfMessage && !receiveResult.CloseStatus.HasValue);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[WebSocket] Receive error: {ex.Message}");
                            break;
                        }

                        if (receiveResult.MessageType == WebSocketMessageType.Close || receiveResult.CloseStatus.HasValue)
                        {
                            Console.WriteLine("[WebSocket] Close message received.");
                            break;
                        }

                        if (receiveResult.MessageType == WebSocketMessageType.Text)
                        {
                            var json = Encoding.UTF8.GetString(ms.ToArray());
                            Console.WriteLine($"[WebSocket] Received: {json}");

                            if (string.IsNullOrWhiteSpace(json)) continue;

                            SearchRequest? searchRequest = null;
                            try
                            {
                                searchRequest = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.SearchRequest);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[WebSocket] Deserialization error: {ex.Message}");
                            }

                            if (searchRequest != null && !string.IsNullOrWhiteSpace(searchRequest.Key))
                            {
                                Console.WriteLine($"[WebSocket] Searching for: {searchRequest.Key}");
                                var localBooks = localService.GetLocalBooks();

                                var searchResults = localBooks
                                    .Where(b => b.Name.Contains(searchRequest.Key, StringComparison.OrdinalIgnoreCase) ||
                                                b.Author.Contains(searchRequest.Key, StringComparison.OrdinalIgnoreCase))
                                    .Select(b => new SearchBook(
                                        Name: b.Name,
                                        Author: b.Author,
                                        BookUrl: b.BookUrl,
                                        Origin: b.Origin,
                                        OriginName: b.OriginName,
                                        Type: b.Type,
                                        TocUrl: b.TocUrl,
                                        LatestChapterTitle: b.LatestChapterTitle
                                    ))
                                    .ToList();

                                var responseJson = JsonSerializer.Serialize(searchResults, AppJsonSerializerContext.Default.ListSearchBook);
                                var responseBytes = Encoding.UTF8.GetBytes(responseJson);
                                await webSocket.SendAsync(new ArraySegment<byte>(responseBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                                Console.WriteLine($"[WebSocket] Sent {searchResults.Count} results.");

                                // 本地搜索是同步、一次性的：结果已全部发出，关闭连接以通知客户端搜索结束。
                                // legado 与前端都把 socket 关闭当作「搜索完成」信号；不关会让前端永远停在
                                // 「搜索中…」（空结果时尤其明显），onclose/onerror 里的收尾逻辑也不会触发。
                                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Search done", CancellationToken.None);
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebSocket] Error: {ex}");
                }
                finally
                {
                    // 正常关闭（客户端主动断开或搜索结束）用 NormalClosure；
                    // 之前误用 InternalServerError，前端会把正常断开当作错误提示。
                    if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
                    {
                        try
                        {
                            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[WebSocket] Close error: {ex.Message}");
                        }
                    }
                    Console.WriteLine("[WebSocket] Connection closed.");
                }
            }
            else
            {
                Console.WriteLine("[WebSocket] Not a WebSocket request.");
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
        });
    }
}
