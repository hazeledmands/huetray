using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HueTray.Hue;

namespace HueTray;

// Snapshots of the bridge's resources, simplified for menus and the HTTP API.

public sealed record Light(string Id, string Name, bool On, double? Brightness, int? Kelvin,
                           bool SupportsColor, bool SupportsColorTemp, string? Room)
{
    [JsonIgnore] public int MirekMin { get; init; } = 153;
    [JsonIgnore] public int MirekMax { get; init; } = 500;
}

/// A room, a zone, or "home" (every light). Hue switches and dims these through a grouped_light.
public sealed record Group(string Id, string Kind, string Name, bool On, double? Brightness, IReadOnlyList<string> LightIds)
{
    [JsonIgnore] public string? GroupedLightId { get; init; }
}

public sealed record Scene(string Id, string Name, string? GroupId, string? Group, bool Active);

/// What changed: Type is light, room, zone, scene or "reload" (everything); Item is the new
/// snapshot, or null if it was deleted.
public sealed record StateChange(string Type, string Id, object? Item);

/// The live copy of the bridge's resources, kept as the bridge's own JSON so partial updates from
/// the event stream can be merged in. Safe to use from any thread.
public sealed class HueState
{
    // After we change something, ignore the bridge's reports about it for a moment. They lag
    // behind, so during a fast jog spin they'd drag the value back to where it was.
    static readonly string[] HeldFields = { "on", "dimming", "color", "color_temperature" };
    public static TimeSpan Hold = TimeSpan.FromSeconds(1.5);

    readonly object gate = new();
    Dictionary<string, JsonObject> byId = new();
    readonly Dictionary<string, DateTime> heldUntil = new();

    /// Raised after every change, on whichever thread made it.
    public event Action<IReadOnlyList<StateChange>>? Changed;

    public bool IsLoaded { get; private set; }

    public void Load(IEnumerable<JsonObject> resources)
    {
        lock (gate)
        {
            byId = resources.ToDictionary(r => Id(r), r => (JsonObject)r.DeepClone());
            heldUntil.Clear();
            IsLoaded = true;
        }
        Changed?.Invoke(new[] { new StateChange("reload", "", null) });
    }

    /// Applies events from the event stream: kind is "add", "update" or "delete".
    public void ApplyEvents(IEnumerable<(string Kind, JsonObject Data)> events)
    {
        var changed = new List<string>();
        lock (gate)
        {
            var now = DateTime.UtcNow;
            foreach (var (kind, data) in events)
            {
                if (data["type"]?.GetValue<string>() is not { } type || !HueClient.Types.Contains(type)) continue;
                var id = Id(data);
                switch (kind)
                {
                    case "add":
                        byId[id] = (JsonObject)data.DeepClone();
                        break;
                    case "delete":
                        byId.Remove(id);
                        break;
                    case "update" when byId.TryGetValue(id, out var existing):
                        var update = (JsonObject)data.DeepClone();
                        if (heldUntil.TryGetValue(id, out var until) && until > now)
                            foreach (var field in HeldFields) update.Remove(field);
                        Merge(existing, update);
                        break;
                    default:
                        continue;
                }
                changed.Add(id);
            }
        }
        Raise(changed);
    }

    /// Records a change we've just asked the bridge to make, so menus and the API show it at once.
    public void ApplyLocal(string id, JsonObject change)
    {
        lock (gate)
        {
            if (!byId.TryGetValue(id, out var existing)) return;
            var copy = (JsonObject)change.DeepClone();
            copy.Remove("dynamics");
            Merge(existing, copy);
            heldUntil[id] = DateTime.UtcNow + Hold;
        }
        Raise(new[] { id });
    }

    void Raise(IEnumerable<string> ids)
    {
        var handler = Changed;
        if (handler == null) return;
        List<StateChange> changes;
        lock (gate) changes = ids.Distinct().SelectMany(Describe).DistinctBy(c => (c.Type, c.Id)).ToList();
        if (changes.Count > 0) handler(changes);
    }

    // Must hold `gate`.
    IEnumerable<StateChange> Describe(string id)
    {
        if (!byId.TryGetValue(id, out var r))
        {
            yield return new StateChange("deleted", id, null);
            yield break;
        }
        switch (Type(r))
        {
            case "light":
                yield return new StateChange("light", id, LightView(r));
                break;
            case "room" or "zone":
                yield return new StateChange(Type(r), id, GroupView(r));
                break;
            case "grouped_light":
                if (Owner(r) is { } owner && byId.TryGetValue(owner, out var group) && Type(group) is "room" or "zone" or "bridge_home")
                    yield return new StateChange(Type(group) == "bridge_home" ? "home" : Type(group), owner, GroupView(group));
                break;
            case "scene":
                yield return new StateChange("scene", id, SceneView(r));
                break;
        }
    }

    // MARK: Snapshots

    public IReadOnlyList<Light> Lights() => Views("light", LightView);
    public IReadOnlyList<Group> Rooms() => Views("room", GroupView);
    public IReadOnlyList<Group> Zones() => Views("zone", GroupView);
    public IReadOnlyList<Scene> Scenes() => Views("scene", SceneView);

    public Group? Home()
    {
        lock (gate) return byId.Values.Where(r => Type(r) == "bridge_home").Select(GroupView).FirstOrDefault();
    }

    public Light? LightById(string id)
    {
        lock (gate) return byId.TryGetValue(id, out var r) && Type(r) == "light" ? LightView(r) : null;
    }

    IReadOnlyList<T> Views<T>(string type, Func<JsonObject, T> view) where T : class
    {
        lock (gate)
        {
            return byId.Values.Where(r => Type(r) == type).Select(view)
                .OrderBy(v => v switch { Light l => l.Name, Group g => g.Name, Scene s => s.Name, _ => "" }, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    // MARK: Finding things by name

    public Light FindLight(string target) => Resolve(Lights(), target, "light", l => l.Id, l => l.Name);

    /// kind is "room", "zone" or "home". The home group is every light.
    public Group FindGroup(string kind, string target)
    {
        if (kind == "home") return Home() ?? throw new NotFoundException("The bridge has no home group");
        return Resolve(kind == "room" ? Rooms() : Zones(), target, kind, g => g.Id, g => g.Name);
    }

    /// Scene names repeat between rooms ("Relax" is in every room), so `group` can narrow it down.
    public Scene FindScene(string target, string? group = null)
    {
        var scenes = Scenes().Where(s => group == null || s.GroupId == group
                                         || string.Equals(s.Group, group, StringComparison.OrdinalIgnoreCase));
        return Resolve(scenes.ToList(), target, "scene", s => s.Id, s => s.Name, s => s.Group);
    }

    static T Resolve<T>(IReadOnlyList<T> items, string target, string what, Func<T, string> id, Func<T, string> name,
                        Func<T, string?>? where = null)
    {
        var byIdMatch = items.FirstOrDefault(i => id(i) == target);
        if (byIdMatch != null) return byIdMatch;
        var matches = items.Where(i => string.Equals(name(i), target, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new NotFoundException($"No {what} called \"{target}\""),
            _ => throw new AmbiguousException(where == null
                ? $"{matches.Count} {what}s are called \"{target}\"; use its id"
                : $"\"{target}\" is in {string.Join(", ", matches.Select(where).Select(w => w ?? "?"))}; say which"),
        };
    }

    // MARK: Building snapshots from the bridge's JSON (must hold `gate`)

    Light LightView(JsonObject r)
    {
        var id = Id(r);
        var ct = r["color_temperature"];
        int? mirek = ct?["mirek"] is JsonValue m ? m.GetValue<int>() : null;
        return new Light(
            id,
            Name(r),
            r["on"]?["on"]?.GetValue<bool>() ?? false,
            r["dimming"]?["brightness"]?.GetValue<double>(),
            mirek is > 0 ? (int)Math.Round(1_000_000.0 / mirek.Value) : null,
            r["color"] != null,
            ct != null,
            byId.Values.Where(g => Type(g) == "room").FirstOrDefault(g => LightIdsOf(g).Contains(id)) is { } room ? Name(room) : null)
        {
            MirekMin = ct?["mirek_schema"]?["mirek_minimum"]?.GetValue<int>() ?? 153,
            MirekMax = ct?["mirek_schema"]?["mirek_maximum"]?.GetValue<int>() ?? 500,
        };
    }

    Group GroupView(JsonObject r)
    {
        var groupedId = Refs(r["services"]).FirstOrDefault(s => s.Type == "grouped_light").Id;
        var grouped = groupedId != null && byId.TryGetValue(groupedId, out var g) ? g : null;
        var kind = Type(r) == "bridge_home" ? "home" : Type(r);
        return new Group(
            Id(r),
            kind,
            kind == "home" ? "All lights" : Name(r),
            grouped?["on"]?["on"]?.GetValue<bool>() ?? false,
            grouped?["dimming"]?["brightness"]?.GetValue<double>(),
            LightIdsOf(r))
        { GroupedLightId = groupedId };
    }

    Scene SceneView(JsonObject r)
    {
        var groupId = r["group"]?["rid"]?.GetValue<string>();
        var group = groupId != null && byId.TryGetValue(groupId, out var g) ? Name(g) : null;
        var active = r["status"]?["active"]?.GetValue<string>() is { } a && a != "inactive";
        return new Scene(Id(r), Name(r), groupId, group, active);
    }

    /// A room lists devices, which own lights. A zone lists lights. The home lists rooms and devices.
    List<string> LightIdsOf(JsonObject group)
    {
        var lights = new List<string>();
        foreach (var (id, type) in Refs(group["children"]))
        {
            if (type == "light") lights.Add(id);
            else if (byId.TryGetValue(id, out var child))
            {
                if (type == "device") lights.AddRange(Refs(child["services"]).Where(s => s.Type == "light").Select(s => s.Id));
                else if (type == "room") lights.AddRange(LightIdsOf(child));
            }
        }
        return lights.Distinct().ToList();
    }

    static IEnumerable<(string Id, string Type)> Refs(JsonNode? list) =>
        (list as JsonArray ?? new()).Select(n => (n?["rid"]?.GetValue<string>(), n?["rtype"]?.GetValue<string>()))
            .Where(p => p.Item1 != null && p.Item2 != null).Select(p => (p.Item1!, p.Item2!));

    static string Id(JsonObject r) => r["id"]!.GetValue<string>();
    static string Type(JsonObject r) => r["type"]?.GetValue<string>() ?? "";
    static string Name(JsonObject r) => r["metadata"]?["name"]?.GetValue<string>() ?? Id(r);
    static string? Owner(JsonObject r) => r["owner"]?["rid"]?.GetValue<string>();

    static void Merge(JsonObject target, JsonObject update)
    {
        foreach (var (key, value) in update.ToList())
        {
            if (value is JsonObject child && target[key] is JsonObject existing) Merge(existing, child);
            else target[key] = value?.DeepClone();
        }
    }
}
