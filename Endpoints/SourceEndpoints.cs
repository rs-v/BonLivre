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

        app.MapPost("/saveBookSource", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var content = await reader.ReadToEndAsync();
            return Results.Json(new LeagdoApiResponse<string>(true, "", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);
        });

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
                    if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "Error occurred", CancellationToken.None);
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
