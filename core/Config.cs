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

    /// Loads the settings, creating the API token (and saving it) the first time.
    public static Config Load()
    {
        Config cfg;
        try { cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(Path), options) ?? new Config(); }
        catch { cfg = new Config(); }
        if (string.IsNullOrEmpty(cfg.ApiToken))
        {
            cfg.ApiToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            Save(cfg);
        }
        return cfg;
    }

    public static void Save(Config cfg)
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(cfg, options)); }
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
