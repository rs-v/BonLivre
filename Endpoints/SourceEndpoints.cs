using BonLivre.Models;
using BonLivre.Configuration;

namespace BonLivre.Endpoints;

public static class SourceEndpoints
{
    private static List<object> _bookSources = new();

    public static void MapSourceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/getBookSources", () =>
            Results.Json(new LeagdoApiResponse<List<object>>(true, "", _bookSources), AppJsonSerializerContext.Default.LeagdoApiResponseListObject));

        app.MapPost("/saveBookSource", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var content = await reader.ReadToEndAsync();
            return Results.Json(new LeagdoApiResponse<string>(true, "", ""), AppJsonSerializerContext.Default.LeagdoApiResponseString);
        });

        app.MapGet("/searchBook", async (HttpContext context) =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                await Task.Delay(1000);
                await webSocket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
        });
    }
}
