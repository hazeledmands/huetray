using System.Text.Json;
using HueTray.Hue;

namespace HueTray;

/// A named set of lights, rooms and zones that change together, from the "scopes" setting. A DJ
/// controller's knob can choose the scope while its faders change whatever's chosen.
public sealed class ScopeConfig
{
    public string Name { get; set; } = "";
    public List<string> Targets { get; set; } = new();   // "lights/NAME", "rooms/NAME", "zones/NAME" or "home"
}

/// The chosen scope, as the API shows it. Index counts from 1, and is 0 when there are no scopes.
/// Names lists every scope, in order.
public sealed record ScopeView(int Index, int Count, string? Name, IReadOnlyList<string> Targets, IReadOnlyList<string> Names);

/// How to choose a scope: by number (Index), by a step from the current one (Step), by name, or by a
/// position from 0 to 1 (Position), which shares a knob's travel equally between the scopes.
public sealed record ScopeChoice
{
    public int? Index { get; init; }
    public int? Step { get; init; }
    public double? Position { get; init; }
    public string? Name { get; init; }

    /// Reads the HTTP API's JSON: one of {"index": 3}, {"index": "+1"}, {"position": 0.6} or {"name": "Desk"}.
    public static ScopeChoice Parse(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object) throw new FormatException("Expected a JSON object");
        var choices = json.EnumerateObject().ToList();
        if (choices.Count != 1) throw new FormatException("Give one of index, position or name");
        var p = choices[0];
        return p.Name.ToLowerInvariant() switch
        {
            "index" when p.Value.ValueKind == JsonValueKind.Number => new ScopeChoice { Index = (int)Math.Round(p.Value.GetDouble()) },
            "index" => ParseIndex(p.Value.GetString() ?? ""),
            "position" => new ScopeChoice { Position = p.Value.GetDouble() },
            "name" => new ScopeChoice { Name = p.Value.GetString() },
            _ => throw new FormatException($"Unknown field \"{p.Name}\": use index, position or name"),
        };
    }

    /// "3" is a number, "+1" and "-1" are steps.
    public static ScopeChoice ParseIndex(string s)
    {
        s = s.Trim();
        if (!int.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n))
            throw new FormatException($"\"{s}\" isn't a scope number: use 1, 2, …, or +1 / -1 to step");
        return s.StartsWith('+') || s.StartsWith('-') ? new ScopeChoice { Step = n } : new ScopeChoice { Index = n };
    }
}

/// The scopes from the settings, and which one is chosen. It starts at the first. Safe to use from any thread.
public sealed class Scopes
{
    readonly object gate = new();
    readonly List<ScopeConfig> list;
    int index;

    /// Raised when the chosen scope changes, on whichever thread changed it.
    public event Action<ScopeView>? Changed;

    public Scopes(IEnumerable<ScopeConfig> scopes)
    {
        list = scopes.Where(s => s != null).ToList();
        index = list.Count > 0 ? 1 : 0;
    }

    public ScopeView Current
    {
        get { lock (gate) return View(); }
    }

    /// Chooses a scope. Numbers and steps past either end stop at the first or last scope.
    public ScopeView Select(ScopeChoice choice)
    {
        ScopeView view;
        bool changed;
        lock (gate)
        {
            if (list.Count == 0) throw new NotFoundException("There are no scopes yet. Add them to \"scopes\" in Hue Tray's settings file");
            int chosen;
            if (choice.Name is { } name)
            {
                chosen = list.FindIndex(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) + 1;
                if (chosen == 0) throw new NotFoundException($"No scope called \"{name}\"");
            }
            else if (choice.Position is { } position)
            {
                if (double.IsNaN(position)) throw new FormatException("position must be a number from 0 to 1");
                chosen = (int)Math.Floor(Math.Clamp(position, 0, 1) * list.Count) + 1;
            }
            else if (choice.Step is { } step) chosen = index + step;
            else if (choice.Index is { } i) chosen = i;
            else throw new FormatException("Give one of index, position or name");

            chosen = Math.Clamp(chosen, 1, list.Count);
            changed = chosen != index;
            index = chosen;
            view = View();
        }
        if (changed) Changed?.Invoke(view);
        return view;
    }

    /// Chooses the scope with this name again, if there still is one. For keeping the choice when the settings are reloaded.
    public void Restore(string? name)
    {
        if (name == null) return;
        lock (gate)
        {
            int found = list.FindIndex(s => s.Name == name);
            if (found >= 0) index = found + 1;
        }
    }

    // Must hold `gate`.
    ScopeView View()
    {
        var chosen = index > 0 ? list[index - 1] : null;
        return new ScopeView(index, list.Count, chosen?.Name, chosen?.Targets.ToList() ?? new List<string>(), list.Select(s => s.Name).ToList());
    }
}
