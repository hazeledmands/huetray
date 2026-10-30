using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace HueTray.Hue;

/// Gets an app key from a bridge. The bridge only hands one out within 30 seconds of its round
/// link button being pressed, so this keeps asking until then.
public static class Pairing
{
    const int LinkButtonNotPressed = 101;

    /// Pairs with the bridge at `info` and saves the key and pinned certificate into `cfg`.
    /// Returns false if the link button wasn't pressed in time.
    public static async Task<bool> PairAsync(Config cfg, BridgeInfo info, TimeSpan wait, CancellationToken ct = default)
    {
        string? fingerprint = null;
        using var http = BridgeHttp.Create(info.Ip, pinned: null, seen: f => fingerprint = f);
        var deviceType = $"hue-tray#{Truncate(Environment.MachineName, 19)}";
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            var key = await TryOnceAsync(http, deviceType, ct);
            if (key != null)
            {
                cfg.BridgeIp = info.Ip;
                cfg.BridgeId = info.Id;
                cfg.BridgeName = info.Name;
                cfg.AppKey = key;
                cfg.CertFingerprint = fingerprint;
                return true;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        return false;
    }

    static async Task<string?> TryOnceAsync(HttpClient http, string deviceType, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var response = await http.PostAsJsonAsync("api", new { devicetype = deviceType, generateclientkey = true }, timeout.Token);
        var first = (JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token)) as JsonArray)?.FirstOrDefault();
        if (first?["success"]?["username"]?.GetValue<string>() is { } key) return key;
        var error = first?["error"];
        if (error?["type"]?.GetValue<int>() == LinkButtonNotPressed) return null;
        throw new HueException($"The bridge refused to pair: {error?["description"]?.GetValue<string>() ?? "unknown error"}");
    }

    static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;
}
