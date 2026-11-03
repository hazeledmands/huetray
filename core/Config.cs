using System.Security.Cryptography;
using System.Text.Json;

namespace HueTray;

// Missing keys keep these defaults, so adding a setting never discards a pairing.
public sealed class Config
{
    public string? BridgeIp { get; set; }
    public string? BridgeId { get; set; }
    public string? BridgeName { get; set; }
    public string? AppKey { get; set; }                       // the bridge's "username" for this app
    public string? CertFingerprint { get; set; }              // SHA-256 of the bridge's certificate, pinned when pairing
    public int ApiPort { get; set; } = 8765;                  // the local HTTP API, on 127.0.0.1 only
    public string ApiToken { get; set; } = "";                // generated on first run
    public MenuConfig Menu { get; set; } = new();
    public List<ScopeConfig> Scopes { get; set; } = new();   // sets of lights that change together; see Scopes.cs
    public double BrightnessStep { get; set; } = 5;           // percent, for relative changes without a number
    public double LightCommandsPerSecond { get; set; } = 10;  // the bridge's documented limits
    public double GroupCommandsPerSecond { get; set; } = 1;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPaired => !string.IsNullOrEmpty(BridgeIp) && !string.IsNullOrEmpty(AppKey);
}

/// Which rooms, zones, lights and scenes the tray menu shows, by name or id. Empty lists show
/// every room, zone and scene, and no lights on their own (they're inside their rooms).
public sealed class MenuConfig
{
    public List<string> Rooms { get; set; } = new();
    public List<string> Lights { get; set; } = new();
    public List<string> Scenes { get; set; } = new();
}

public static class ConfigFile
{
    public static readonly string Path = MakePath();

    static readonly JsonSerializerOptions options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    static string MakePath()
    {
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hue-tray");
        Directory.CreateDirectory(dir);
        return System.IO.Path.Combine(dir, "config.json");
    }

    /// Why the settings file couldn't be read the last time it was loaded, or null.
    public static string? LoadError { get; private set; }

    /// Loads the settings, creating the API token (and saving it) the first time. If there's a settings file
    /// that can't be read, it returns the defaults with no token (so the API turns every request away), sets
    /// LoadError, and doesn't save: Save won't write over it either.
    public static Config Load() => Load(Path);

    internal static Config Load(string path)
    {
        LoadError = null;
        string? error = null;
        var cfg = File.Exists(path) ? Read(path, out error) : new Config();
        if (cfg == null)
        {
            LoadError = error;
            Log.Write($"Can't read {path}: {error}");
            return new Config();
        }
        if (string.IsNullOrEmpty(cfg.ApiToken))
        {
            cfg.ApiToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            Save(cfg, path);
        }
        return cfg;
    }

    static Config? Read(string path, out string? error)
    {
        error = null;
        try { return JsonSerializer.Deserialize<Config>(File.ReadAllText(path), options) ?? new Config(); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            error = e.Message;
            return null;
        }
    }

    /// Saves the settings, unless the file there can't be read. That's usually a hand edit with a typo, and
    /// writing over it would lose the pairing and everything else: everywhere, if the file is synced.
    public static void Save(Config cfg) => Save(cfg, Path);

    internal static void Save(Config cfg, string path)
    {
        if (File.Exists(path) && Read(path, out _) == null) return;
        try { File.WriteAllText(path, JsonSerializer.Serialize(cfg, options)); }
        catch (IOException) { }
    }

    public static DateTime? Modified() => File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : null;
}

public static class Log
{
    /// Lets a shell show log lines somewhere other than the console.
    public static event Action<string>? Written;

    public static void Write(string s)
    {
        var line = $"[{DateTime.Now:T}] {s}";
        Console.WriteLine(line);
        Written?.Invoke(line);
    }
}
