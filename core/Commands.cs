using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using HueTray.Api;
using HueTray.Hue;

namespace HueTray;

/// hue-tray: the terminal commands, shared by the Windows and macOS builds. Commands that change
/// lights go through the running tray app's API when it's up (so its menu stays in step), and
/// straight to the bridge when it isn't.
public static class Commands
{
    const string Usage = """
        Usage:
          hue-tray pair [ip]                find the bridge and pair with it (press its link button)
          hue-tray discover                 list the bridges on the network
          hue-tray list                     show rooms, zones, lights and scenes
          hue-tray scene <name> [room]      activate a scene
          hue-tray light <name> <value>...  change a light
          hue-tray room <name> <value>...   change a room
          hue-tray zone <name> <value>...   change a zone
          hue-tray off                      turn off everything the menu shows
          hue-tray status                   is the bridge reachable?
          hue-tray run                      run the bridge connection and API without the tray icon
          hue-tray token                    print the API token
          hue-tray login on|off             start (or stop starting) the tray app at login
        A <value> is on, off, a brightness (50, +10, -10), a colour temperature (2700K) or a colour (#ff8800).
        """;

    /// `trayApp` names the tray app in the usage text. `setStartAtLogin` is what `login on|off` does.
    public static async Task<int> Run(string[] args, string trayApp, Action<bool> setStartAtLogin)
    {
        try
        {
            switch (args.FirstOrDefault())
            {
                case "pair": return await Pair(args.ElementAtOrDefault(1));
                case "discover": return await Discover();
                case "list": return await List();
                case "scene" when args.Length >= 2:
                    return await Change($"scenes/{Uri.EscapeDataString(args[1])}/activate", HttpMethod.Post,
                        args.Length > 2 ? new JsonObject { ["room"] = args[2] } : null,
                        hue => hue.ActivateScene(args[1], args.ElementAtOrDefault(2)));
                case "light" or "room" or "zone" when args.Length >= 3:
                    var change = ParseValues(args.Skip(2));
                    return await Change($"{args[0]}s/{Uri.EscapeDataString(args[1])}", HttpMethod.Patch, ApiBody(change),
                        hue => args[0] == "light" ? hue.SetLight(args[1], change) : hue.SetGroup(args[0], args[1], change));
                case "off": return await Change("off", HttpMethod.Post, null, hue => { hue.AllOff(); return "ok"; });
                case "status": return await Status();
                case "run": return await RunHeadless(exitWithStdin: args.Contains("--exit-with-stdin"));
                case "token":
                    Console.WriteLine(ConfigFile.Load().ApiToken);
                    return 0;
                case "login":
                    bool on = args.ElementAtOrDefault(1) != "off";
                    setStartAtLogin(on);
                    Console.WriteLine(on ? "Hue Tray will start at login." : "Hue Tray will no longer start at login.");
                    return 0;
                default:
                    Console.WriteLine(Usage);
                    Console.WriteLine($"The tray app is {trayApp}.");
                    Console.WriteLine($"Settings: {ConfigFile.Path}");
                    return 1;
            }
        }
        catch (Exception e) when (e is HueException or FormatException or HttpRequestException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    static async Task<int> Pair(string? ip)
    {
        var cfg = ConfigFile.Load();
        BridgeInfo? bridge;
        if (ip != null)
        {
            bridge = await Discovery.DescribeAsync(ip) ?? throw new HueException($"No Hue bridge answered at {ip}");
        }
        else
        {
            Console.WriteLine("Looking for bridges…");
            var found = await Discovery.FindAsync();
            if (found.Count == 0) throw new HueException("No bridge found. If you know its address, run:  hue-tray pair <ip>");
            if (found.Count > 1)
            {
                Console.WriteLine("Found more than one bridge. Pick one with  hue-tray pair <ip>:");
                foreach (var b in found) Console.WriteLine($"  {b.Ip}   {b.Name} ({b.Id})");
                return 1;
            }
            bridge = found[0];
        }
        Console.WriteLine($"Found {bridge.Name} at {bridge.Ip}. Press the round link button on the bridge (within 60 seconds)…");
        if (!await Pairing.PairAsync(cfg, bridge, TimeSpan.FromSeconds(60)))
        {
            Console.WriteLine("The link button wasn't pressed in time. Try again.");
            return 1;
        }
        ConfigFile.Save(cfg);
        Console.WriteLine($"Paired. Saved to {ConfigFile.Path}");
        return 0;
    }

    /// One bridge per line, as address, name and id separated by tabs. The macOS menu bar app reads this.
    static async Task<int> Discover()
    {
        var found = await Discovery.FindAsync();
        var known = ConfigFile.Load().BridgeIp;
        if (found.Count == 0 && known != null && await Discovery.DescribeAsync(known) is { } bridge)
            found.Add(bridge);
        foreach (var b in found) Console.WriteLine($"{b.Ip}\t{b.Name}\t{b.Id}");
        return 0;
    }

    static async Task<int> List()
    {
        await using var hue = new HueService(ConfigFile.Load());
        await hue.LoadAsync();
        var state = hue.State;
        var lights = state.Lights().ToDictionary(l => l.Id);
        string Describe(bool on, double? bri) => on ? $"on {bri ?? 100:0}%" : "off";

        foreach (var group in state.Rooms().Concat(state.Zones()))
        {
            Console.WriteLine($"{group.Name} ({group.Kind}, {Describe(group.On, group.Brightness)})");
            foreach (var id in group.LightIds)
                if (lights.TryGetValue(id, out var l))
                    Console.WriteLine($"    {l.Name}: {Describe(l.On, l.Brightness)}{(l.Kelvin is { } k && l.On ? $", {k}K" : "")}");
            var scenes = state.Scenes().Where(s => s.GroupId == group.Id).Select(s => s.Active ? $"{s.Name} (active)" : s.Name).ToList();
            if (scenes.Count > 0) Console.WriteLine($"    scenes: {string.Join(", ", scenes)}");
        }
        return 0;
    }

    static async Task<int> Status()
    {
        var cfg = ConfigFile.Load();
        if (!cfg.IsPaired)
        {
            Console.WriteLine("Not paired with a bridge yet. Run:  hue-tray pair");
            return 1;
        }
        await using var hue = new HueService(cfg);
        await hue.LoadAsync();
        Console.WriteLine($"Connected to {hue.BridgeLabel} at {cfg.BridgeIp}: {hue.State.Lights().Count} lights, {hue.State.Scenes().Count} scenes");
        Console.WriteLine(await ApiRunning(cfg) ? $"The tray app's API is on http://127.0.0.1:{cfg.ApiPort}/api/v1/"
                                                : "The tray app isn't running.");
        return 0;
    }

    /// `exitWithStdin` also quits when stdin closes. The macOS app runs this as its helper with a pipe
    /// for stdin, so the helper can't outlive the app even if the app crashes.
    static async Task<int> RunHeadless(bool exitWithStdin)
    {
        var cfg = ConfigFile.Load();
        await using var hue = new HueService(cfg);
        if (!hue.IsPaired) throw new HueException("Not paired with a bridge yet. Run:  hue-tray pair");
        hue.StatusChanged += s => Log.Write($"Bridge: {s}");
        hue.Start();
        await using var api = new ApiServer(hue, cfg.ApiToken);
        try { await api.StartAsync(cfg.ApiPort); }
        catch (IOException)
        {
            Console.Error.WriteLine($"Port {cfg.ApiPort} is in use by another program. Change apiPort in the settings file.");
            return 1;
        }
        Log.Write($"API on http://127.0.0.1:{api.Port}/api/v1/  (token: hue-tray token)");
        Log.Write("Running. Press Ctrl-C to quit.");
        var quit = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.TrySetResult(); };
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, s => { s.Cancel = true; quit.TrySetResult(); });
        if (exitWithStdin)
            _ = Task.Run(async () =>
            {
                try { await Console.OpenStandardInput().CopyToAsync(Stream.Null); }
                finally { quit.TrySetResult(); }
            });
        await quit.Task;
        Console.WriteLine("Bye.");
        return 0;
    }

    /// Sends the change to the running app if there is one, otherwise to the bridge directly.
    static async Task<int> Change(string path, HttpMethod method, JsonObject? body, Func<HueService, object> direct)
    {
        var cfg = ConfigFile.Load();
        if (await ApiRunning(cfg))
        {
            using var http = ApiClient(cfg);
            var request = new HttpRequestMessage(method, path);
            if (body != null) request.Content = JsonContent.Create(body);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return 0;
            var error = (await response.Content.ReadFromJsonAsync<JsonObject>())?["error"]?.GetValue<string>();
            Console.Error.WriteLine(error ?? $"HTTP {(int)response.StatusCode}");
            return 1;
        }
        await using var hue = new HueService(cfg);
        await hue.LoadAsync();
        direct(hue);
        await hue.WhenIdle();
        return 0;
    }

    static async Task<bool> ApiRunning(Config cfg)
    {
        try
        {
            using var http = ApiClient(cfg);
            http.Timeout = TimeSpan.FromSeconds(1);
            return (await http.GetAsync("status")).IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    static HttpClient ApiClient(Config cfg)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{cfg.ApiPort}/api/v1/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiToken);
        return http;
    }

    static LightChange ParseValues(IEnumerable<string> values)
    {
        var change = new LightChange();
        foreach (var v in values)
        {
            var lower = v.ToLowerInvariant();
            change = lower switch
            {
                "on" => change with { On = true },
                "off" => change with { On = false },
                _ when lower.StartsWith('#') => change with { Color = ColorMath.Normalize(v) },
                _ when lower.EndsWith('k') && int.TryParse(lower[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var k)
                    => change with { Kelvin = k },
                _ => change with { Brightness = Level.Parse(v) },
            };
        }
        return change;
    }

    static JsonObject ApiBody(LightChange c)
    {
        var body = new JsonObject();
        if (c.On is { } on) body["on"] = on;
        if (c.Brightness is { } b) body["brightness"] = b.Relative ? (b.Value >= 0 ? "+" : "") + b.Value.ToString(CultureInfo.InvariantCulture) : b.Value;
        if (c.Kelvin is { } k) body["colorTemp"] = k;
        if (c.Color is { } color) body["color"] = color;
        return body;
    }
}
