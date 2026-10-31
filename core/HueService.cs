using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HueTray.Hue;

namespace HueTray;

public enum BridgeStatus { Unpaired, Connecting, Connected, Unreachable }

/// Everything a shell or the HTTP API does with the lights: keeps the state cache in sync with the
/// bridge, and turns requests like "dim the living room by 5%" into throttled bridge commands.
public sealed class HueService : IAsyncDisposable
{
    readonly Config cfg;
    readonly HttpClient? http;
    readonly HueClient? client;
    readonly CommandQueue? queue;
    readonly CancellationTokenSource stop = new();
    Task? events;

    public HueState State { get; } = new();
    public Scopes Scopes { get; }
    public BridgeStatus Status { get; private set; }
    public string? LastError { get; private set; }

    /// Raised when Status changes, on a background thread.
    public event Action<BridgeStatus>? StatusChanged;

    /// `http` replaces the real connection to the bridge, for tests.
    public HueService(Config cfg, HttpClient? http = null)
    {
        this.cfg = cfg;
        Scopes = new Scopes(cfg.Scopes);
        if (!cfg.IsPaired && http == null)
        {
            Status = BridgeStatus.Unpaired;
            return;
        }
        this.http = http ?? BridgeHttp.Create(cfg.BridgeIp!, cfg.CertFingerprint);
        client = new HueClient(this.http, cfg.AppKey ?? "");
        queue = new CommandQueue(client.PutAsync, cfg.LightCommandsPerSecond, cfg.GroupCommandsPerSecond);
        Status = BridgeStatus.Connecting;
    }

    public bool IsPaired => client != null;
    public string BridgeLabel => cfg.BridgeName ?? cfg.BridgeIp ?? "Hue Bridge";

    /// Loads everything from the bridge once. Enough for one-off commands.
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (client == null) throw new HueException("Not paired with a bridge yet. Run:  hue-tray pair");
        State.Load(await client.GetAllAsync(ct));
    }

    /// Keeps the state in sync until disposed: loads, follows the event stream, and reconnects
    /// (and reloads, to catch anything missed) whenever the connection drops.
    public void Start()
    {
        if (client == null || events != null) return;
        events = Task.Run(() => FollowAsync(stop.Token));
    }

    async Task FollowAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await LoadAsync(ct);
                using var stream = await client!.OpenEventStreamAsync(ct);
                SetStatus(BridgeStatus.Connected, null);
                backoff = TimeSpan.FromSeconds(1);
                await ReadEventsAsync(stream, ct);
                Log.Write("The bridge closed the event stream; reconnecting");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                if (Status != BridgeStatus.Unreachable) Log.Write($"Lost the bridge: {e.Message}");
                SetStatus(BridgeStatus.Unreachable, e.Message);
            }
            try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
        }
    }

    async Task ReadEventsAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("data:")) data.Append(line.AsSpan(5));
            else if (line.Length == 0 && data.Length > 0)
            {
                ApplyEventBatch(data.ToString());
                data.Clear();
            }
        }
    }

    internal void ApplyEventBatch(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonArray batch) return;
            State.ApplyEvents(
                from e in batch
                let kind = e?["type"]?.GetValue<string>()
                from d in (e?["data"] as JsonArray ?? new()).OfType<JsonObject>()
                where kind != null
                select (kind, d));
        }
        catch (JsonException e) { Log.Write($"Ignored a garbled event: {e.Message}"); }
    }

    void SetStatus(BridgeStatus status, string? error)
    {
        LastError = error;
        if (status == Status) return;
        Status = status;
        StatusChanged?.Invoke(status);
    }

    // MARK: Changing things. These return at once; the queue sends the change when the bridge allows.

    public Light SetLight(string target, LightChange change)
    {
        var light = State.FindLight(target);
        Send("light", light.Id, change.ToHue(light.Brightness, light.On, light.MirekMin, light.MirekMax));
        return State.LightById(light.Id) ?? light;
    }

    /// kind is "room", "zone" or "home".
    public Group SetGroup(string kind, string target, LightChange change)
    {
        var group = State.FindGroup(kind, target);
        if (group.GroupedLightId == null) throw new HueException($"{group.Name} has no lights to control");
        Send("grouped_light", group.GroupedLightId, change.ToHue(group.Brightness, group.On));
        return State.FindGroup(kind, group.Id);
    }

    public Scene ActivateScene(string target, string? group = null, int? transitionMs = null)
    {
        var scene = State.FindScene(target, group);
        var recall = new JsonObject { ["action"] = "active" };
        if (transitionMs is { } ms) recall["duration"] = Math.Max(0, ms);
        Queue().Enqueue("scene", scene.Id, new JsonObject { ["recall"] = recall });
        return scene;
    }

    /// Changes every light, room and zone in the chosen scope. Returns their new states.
    public IReadOnlyList<object> SetScope(LightChange change)
    {
        var scope = Scopes.Current;
        if (scope.Count == 0) throw new NotFoundException("There are no scopes yet. Add them to \"scopes\" in Hue Tray's settings file");
        // Find them all first, so a misspelt target changes nothing rather than half the scope.
        List<Func<object>> changes;
        try { changes = scope.Targets.Select(target => Target(target, change)).ToList(); }
        catch (HueException e) when (e is NotFoundException or AmbiguousException)
        {
            throw new NotFoundException($"In the scope \"{scope.Name}\": {e.Message}");
        }
        return changes.Select(apply => apply()).ToList();
    }

    /// "lights/NAME", "rooms/NAME", "zones/NAME" or "home", like the API's paths. Returns what changes it.
    Func<object> Target(string target, LightChange change)
    {
        int slash = target.IndexOf('/');
        string kind = (slash < 0 ? target : target[..slash]).Trim().ToLowerInvariant().TrimEnd('s');
        string name = slash < 0 ? "" : target[(slash + 1)..];
        switch (kind)
        {
            case "light":
                var light = State.FindLight(name);
                return () => SetLight(light.Id, change);
            case "room" or "zone":
                var group = State.FindGroup(kind, name);
                return () => SetGroup(kind, group.Id, change);
            case "home" when slash < 0:
                State.FindGroup("home", "");
                return () => SetGroup("home", "", change);
            default:
                throw new FormatException($"\"{target}\" isn't something a scope can change: use lights/NAME, rooms/NAME, zones/NAME or home");
        }
    }

    /// Turns off what the menu shows: the configured rooms and lights, or every light if none are configured.
    public void AllOff()
    {
        var off = new LightChange { On = false };
        if (cfg.Menu.Rooms.Count == 0 && cfg.Menu.Lights.Count == 0)
        {
            SetGroup("home", "", off);
            return;
        }
        foreach (var room in cfg.Menu.Rooms) Try(() => SetGroup("room", room, off));
        foreach (var light in cfg.Menu.Lights) Try(() => SetLight(light, off));
    }

    static void Try(Action action)
    {
        try { action(); }
        catch (HueException e) { Log.Write(e.Message); }
    }

    void Send(string type, string id, JsonObject body)
    {
        if (body.Count == 0) return;   // e.g. dimming a light that's already off
        State.ApplyLocal(id, body);
        Queue().Enqueue(type, id, body);
    }

    CommandQueue Queue() => queue ?? throw new HueException("Not paired with a bridge yet. Run:  hue-tray pair");

    /// Completes once every queued change has been sent.
    public Task WhenIdle() => queue?.WhenIdle() ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (events != null) await events;
        if (queue != null) await queue.DisposeAsync();
        http?.Dispose();
        stop.Dispose();
    }
}
