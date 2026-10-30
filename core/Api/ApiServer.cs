using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using HueTray.Hue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HueTray.Api;

/// The local HTTP API that lets other programs (jog-scroll, scripts, Stream Deck…) drive the
/// lights. Listens on 127.0.0.1 only, and every request needs the token from the settings file,
/// so web pages open in a browser can't use it.
public sealed class ApiServer(HueService hue, string token) : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    WebApplication? app;

    /// The port it's listening on (useful when started on port 0).
    public int Port { get; private set; }

    public async Task StartAsync(int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        app = builder.Build();
        app.UseWebSockets();
        app.Use(Authorize);
        app.Use(MapErrors);
        Map(app.MapGroup("/api/v1"));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Port = new Uri(address).Port;
    }

    async Task Authorize(HttpContext context, RequestDelegate next)
    {
        var header = context.Request.Headers.Authorization.ToString();
        // Browsers can't set headers on a WebSocket, so /events also takes ?token=.
        var given = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim()
            : context.Request.Query["token"].ToString();
        if (!CryptographicEquals(given, token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing or wrong API token. It's apiToken in Hue Tray's config.json." }, Json);
            return;
        }
        await next(context);
    }

    static bool CryptographicEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    static async Task MapErrors(HttpContext context, RequestDelegate next)
    {
        try { await next(context); }
        catch (Exception e) when (e is HueException or FormatException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            context.Response.StatusCode = e switch
            {
                NotFoundException => StatusCodes.Status404NotFound,
                AmbiguousException => StatusCodes.Status409Conflict,
                HueException => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status400BadRequest,
            };
            await context.Response.WriteAsJsonAsync(new { error = e.Message }, Json);
        }
    }

    void Map(RouteGroupBuilder api)
    {
        api.MapGet("/status", () => new { status = hue.Status.ToString().ToLowerInvariant(), bridge = hue.BridgeLabel, error = hue.LastError });
        api.MapGet("/lights", () => hue.State.Lights());
        api.MapGet("/rooms", () => hue.State.Rooms());
        api.MapGet("/zones", () => hue.State.Zones());
        api.MapGet("/scenes", () => hue.State.Scenes());

        api.MapPost("/scenes/{scene}/activate", async (string scene, HttpRequest request) =>
        {
            var body = await OptionalBody(request);
            string? room = body is { } b && b.TryGetProperty("room", out var r) ? r.GetString() : null;
            int? ms = body is { } b2 && b2.TryGetProperty("transitionMs", out var t) ? t.GetInt32() : null;
            return hue.ActivateScene(scene, room, ms);
        });

        api.MapPatch("/lights/{target}", async (string target, HttpRequest request) =>
            hue.SetLight(target, LightChange.Parse(await RequiredBody(request))));
        api.MapPatch("/rooms/{target}", async (string target, HttpRequest request) =>
            hue.SetGroup("room", target, LightChange.Parse(await RequiredBody(request))));
        api.MapPatch("/zones/{target}", async (string target, HttpRequest request) =>
            hue.SetGroup("zone", target, LightChange.Parse(await RequiredBody(request))));
        api.MapPatch("/home", async (HttpRequest request) =>
            hue.SetGroup("home", "", LightChange.Parse(await RequiredBody(request))));

        api.MapPost("/off", () =>
        {
            hue.AllOff();
            return Results.NoContent();
        });

        api.Map("/events", Events);
    }

    static async Task<JsonElement?> OptionalBody(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        var text = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    static async Task<JsonElement> RequiredBody(HttpRequest request)
    {
        using var doc = await JsonDocument.ParseAsync(request.Body);
        return doc.RootElement.Clone();
    }

    /// A WebSocket that sends a JSON message for every change: {"type": "light"|"room"|"zone"|"home"|"scene"|"status"|"reload", ...}.
    async Task Events(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "Connect with a WebSocket" }, Json);
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var outbox = Channel.CreateBounded<object>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        void OnChange(IReadOnlyList<StateChange> changes)
        {
            foreach (var c in changes)
                outbox.Writer.TryWrite(c.Item == null ? new { type = c.Type, id = c.Id } : new { type = c.Type, id = c.Id, item = c.Item });
        }
        void OnStatus(BridgeStatus s) => outbox.Writer.TryWrite(new { type = "status", status = s.ToString().ToLowerInvariant() });

        hue.State.Changed += OnChange;
        hue.StatusChanged += OnStatus;
        OnStatus(hue.Status);
        try
        {
            var closed = DrainAsync(socket, context.RequestAborted);
            var reader = outbox.Reader;
            while (!closed.IsCompleted)
            {
                var ready = reader.WaitToReadAsync(context.RequestAborted).AsTask();
                if (await Task.WhenAny(ready, closed) == closed || !await ready) break;
                while (reader.TryRead(out var message))
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, context.RequestAborted);
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }
        finally
        {
            hue.State.Changed -= OnChange;
            hue.StatusChanged -= OnStatus;
        }
    }

    // Reads (and ignores) whatever the client sends, to notice when it closes.
    static async Task DrainAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                    break;
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (app != null) await app.DisposeAsync();
    }
}
