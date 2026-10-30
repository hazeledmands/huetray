using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace HueTray.Hue;

/// The bridge's CLIP v2 API: read every resource, change one, and open the event stream.
public sealed class HueClient(HttpClient http, string appKey)
{
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// The resource types Hue Tray uses. The bridge has many more (sensors, zigbee details, …).
    public static readonly HashSet<string> Types = new() { "light", "device", "room", "zone", "grouped_light", "scene", "bridge_home" };

    public async Task<List<JsonObject>> GetAllAsync(CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Get, "clip/v2/resource");
        var body = await SendAsync(request, ct);
        return (body["data"] as JsonArray ?? new())
            .OfType<JsonObject>()
            .Where(r => r["type"]?.GetValue<string>() is { } t && Types.Contains(t))
            .ToList();
    }

    public async Task PutAsync(string type, string id, JsonObject change, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Put, $"clip/v2/resource/{type}/{id}");
        request.Content = new StringContent(change.ToJsonString(), Encoding.UTF8, "application/json");
        await SendAsync(request, ct);
    }

    /// The server-sent event stream. Each event is a "data:" line holding a JSON array.
    public async Task<Stream> OpenEventStreamAsync(CancellationToken ct)
    {
        var request = Request(HttpMethod.Get, "eventstream/clip/v2");
        request.Headers.Accept.ParseAdd("text/event-stream");
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new HueException($"The bridge refused the event stream ({(int)response.StatusCode})");
        }
        return await response.Content.ReadAsStreamAsync(ct);
    }

    HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("hue-application-key", appKey);
        return request;
    }

    async Task<JsonObject> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using var response = await http.SendAsync(request, timeout.Token);
        var text = await response.Content.ReadAsStringAsync(timeout.Token);
        JsonObject? body = null;
        try { body = JsonNode.Parse(text) as JsonObject; } catch (System.Text.Json.JsonException) { }

        var errors = (body?["errors"] as JsonArray)?.Select(e => e?["description"]?.GetValue<string>()).OfType<string>().ToList();
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new HueException("The bridge is busy (too many requests)");
        if (!response.IsSuccessStatusCode || errors is { Count: > 0 })
        {
            var detail = errors is { Count: > 0 } ? string.Join("; ", errors) : $"HTTP {(int)response.StatusCode}";
            throw new HueException($"{request.Method} {request.RequestUri}: {detail}");
        }
        return body ?? new JsonObject();
    }
}
