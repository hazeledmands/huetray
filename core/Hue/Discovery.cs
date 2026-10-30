using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HueTray.Hue;

public sealed record BridgeInfo(string Ip, string Id, string Name);

/// Finds bridges on the local network. Asks over mDNS (bridges answer for _hue._tcp.local) and
/// Philips' discovery service at the same time, then confirms each address really is a bridge.
public static class Discovery
{
    public static async Task<List<BridgeInfo>> FindAsync(CancellationToken ct = default)
    {
        var mdns = MdnsAsync(TimeSpan.FromSeconds(3), ct);
        var cloud = CloudAsync(ct);
        var ips = (await mdns).Concat(await cloud).Distinct().ToList();

        var found = await Task.WhenAll(ips.Select(ip => DescribeAsync(ip, ct)));
        return found.OfType<BridgeInfo>().DistinctBy(b => b.Id).ToList();
    }

    /// Reads a bridge's public config (no key needed). Returns null if `ip` isn't a bridge.
    public static async Task<BridgeInfo?> DescribeAsync(string ip, CancellationToken ct = default)
    {
        try
        {
            using var http = BridgeHttp.Create(ip, pinned: null);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var config = JsonNode.Parse(await http.GetStringAsync("api/0/config", timeout.Token));
            var id = config?["bridgeid"]?.GetValue<string>();
            if (id == null) return null;
            return new BridgeInfo(ip, id, config?["name"]?.GetValue<string>() ?? "Hue Bridge");
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    static async Task<List<string>> CloudAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var list = JsonNode.Parse(await http.GetStringAsync("https://discovery.meethue.com/", ct)) as JsonArray;
            return list?.Select(b => b?["internalipaddress"]?.GetValue<string>()).OfType<string>().ToList() ?? new();
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        {
            return new();   // offline, or rate limited (it allows one request per 15 minutes)
        }
    }

    /// Sends one mDNS question and collects the addresses that answer it. Only bridges answer for
    /// _hue._tcp, so the sender's address is all we need from the reply.
    static async Task<List<string>> MdnsAsync(TimeSpan wait, CancellationToken ct)
    {
        var found = new List<string>();
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            await udp.SendAsync(MdnsQuery("_hue._tcp.local"), new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353), ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(wait);
            var marker = Encoding.ASCII.GetBytes("_hue");
            while (true)
            {
                var reply = await udp.ReceiveAsync(timeout.Token);
                if (reply.Buffer.AsSpan().IndexOf(marker) >= 0)
                    found.Add(reply.RemoteEndPoint.Address.ToString());
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        return found;
    }

    // Sent from an ordinary port, so responders answer us directly (RFC 6762 "legacy unicast").
    static byte[] MdnsQuery(string name)
    {
        var packet = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };   // id, flags, 1 question
        foreach (var label in name.Split('.'))
        {
            packet.Add((byte)label.Length);
            packet.AddRange(Encoding.ASCII.GetBytes(label));
        }
        packet.AddRange(new byte[] { 0, 0, 12, 0, 1 });   // end of name, type PTR, class IN
        return packet.ToArray();
    }
}
