using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HueTray;

/// A brightness that's either a level (50) or a step from the current level (+5, -5).
public readonly record struct Level(double Value, bool Relative)
{
    public static Level Parse(string s)
    {
        s = s.Trim().TrimEnd('%');
        bool relative = s.StartsWith('+') || s.StartsWith('-');
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            throw new FormatException($"\"{s}\" isn't a brightness: use 0-100, or +n / -n to step");
        return new Level(v, relative);
    }

    public double ApplyTo(double current) => Math.Clamp(Relative ? current + Value : Value, 0, 100);
}

/// What to change about a light, room or zone. Only the fields that are set change.
public sealed record LightChange
{
    public bool? On { get; init; }
    public Level? Brightness { get; init; }   // percent; reaching 0 turns the light off
    public int? Kelvin { get; init; }         // colour temperature, roughly 2000-6500
    public string? Color { get; init; }       // "#rrggbb"
    public int? TransitionMs { get; init; }

    /// Reads the HTTP API's JSON: {"on", "brightness" (number, or "+5"), "colorTemp", "color", "transitionMs"}.
    public static LightChange Parse(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object) throw new FormatException("Expected a JSON object");
        var change = new LightChange();
        foreach (var p in json.EnumerateObject())
        {
            change = p.Name.ToLowerInvariant() switch
            {
                "on" => change with { On = p.Value.GetBoolean() },
                "brightness" => change with
                {
                    Brightness = p.Value.ValueKind == JsonValueKind.Number ? new Level(p.Value.GetDouble(), false) : Level.Parse(p.Value.GetString() ?? ""),
                },
                "colortemp" => change with { Kelvin = p.Value.GetInt32() },
                "color" => change with { Color = ColorMath.Normalize(p.Value.GetString() ?? "") },
                "transitionms" => change with { TransitionMs = p.Value.GetInt32() },
                _ => throw new FormatException($"Unknown field \"{p.Name}\""),
            };
        }
        return change;
    }

    /// The CLIP v2 body for this change. `current` is the target's brightness now (null if unknown)
    /// and `isOn` whether it's on, for working out steps.
    public JsonObject ToHue(double? current, bool isOn, int mirekMin = 153, int mirekMax = 500)
    {
        var body = new JsonObject();
        bool turnOn = On == true;
        if (Brightness is { } level)
        {
            double from = isOn ? current ?? 0 : 0;
            double to = level.ApplyTo(from);
            if (to <= 0)
            {
                if (isOn || On == true) body["on"] = new JsonObject { ["on"] = false };
                turnOn = false;
            }
            else
            {
                body["dimming"] = new JsonObject { ["brightness"] = Math.Round(to, 2) };
                turnOn = On != false;
            }
        }
        if (Kelvin is { } k)
        {
            int mirek = Math.Clamp((int)Math.Round(1_000_000.0 / Math.Max(1, k)), mirekMin, mirekMax);
            body["color_temperature"] = new JsonObject { ["mirek"] = mirek };
            turnOn |= On != false;
        }
        if (Color is { } hex)
        {
            var (x, y) = ColorMath.HexToXy(hex);
            body["color"] = new JsonObject { ["xy"] = new JsonObject { ["x"] = x, ["y"] = y } };
            turnOn |= On != false;
        }
        if (On == false) body["on"] = new JsonObject { ["on"] = false };
        else if (turnOn && !body.ContainsKey("on")) body["on"] = new JsonObject { ["on"] = true };
        if (TransitionMs is { } ms) body["dynamics"] = new JsonObject { ["duration"] = Math.Max(0, ms) };
        return body;
    }
}

public static class ColorMath
{
    public static string Normalize(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            throw new FormatException($"\"{hex}\" isn't a colour: use #rrggbb");
        return "#" + hex.ToLowerInvariant();
    }

    /// sRGB to CIE xy, using the conversion in Philips' Hue developer docs. The bridge moves
    /// colours a light can't show to the nearest one it can.
    public static (double X, double Y) HexToXy(string hex)
    {
        int v = int.Parse(Normalize(hex)[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        double r = Linear((v >> 16) & 0xFF), g = Linear((v >> 8) & 0xFF), b = Linear(v & 0xFF);
        double X = r * 0.664511 + g * 0.154324 + b * 0.162028;
        double Y = r * 0.283881 + g * 0.668433 + b * 0.047685;
        double Z = r * 0.000088 + g * 0.072310 + b * 0.986039;
        double sum = X + Y + Z;
        if (sum == 0) return (0.3127, 0.3290);   // black: use the white point; brightness does the rest
        return (Math.Round(X / sum, 4), Math.Round(Y / sum, 4));
    }

    static double Linear(int c)
    {
        double s = c / 255.0;
        return s > 0.04045 ? Math.Pow((s + 0.055) / 1.055, 2.4) : s / 12.92;
    }
}
