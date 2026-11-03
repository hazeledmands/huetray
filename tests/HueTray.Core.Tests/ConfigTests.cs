using System.Net;
using System.Net.Http.Headers;
using HueTray.Api;

namespace HueTray.Tests;

public class ConfigTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("hue-tray-test").FullName;
    string Path => System.IO.Path.Combine(dir, "config.json");

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public void CreatesAndSavesATokenTheFirstTime()
    {
        var cfg = ConfigFile.Load(Path);
        Assert.Null(ConfigFile.LoadError);
        Assert.Equal(32, cfg.ApiToken.Length);
        Assert.Equal(cfg.ApiToken, ConfigFile.Load(Path).ApiToken);
    }

    [Fact]
    public void NeverWritesOverAFileItCantRead()
    {
        const string broken = """{ "bridgeIp": "192.168.1.72", "appKey": "secret", oops }""";
        File.WriteAllText(Path, broken);

        var cfg = ConfigFile.Load(Path);
        Assert.NotNull(ConfigFile.LoadError);
        Assert.False(cfg.IsPaired);
        Assert.Equal("", cfg.ApiToken);   // no new token, which would mean saving

        cfg.BridgeIp = "10.0.0.1";
        ConfigFile.Save(cfg, Path);
        Assert.Equal(broken, File.ReadAllText(Path));

        File.WriteAllText(Path, """{ "bridgeIp": "192.168.1.72", "appKey": "secret", "apiToken": "abc" }""");
        cfg = ConfigFile.Load(Path);
        Assert.Null(ConfigFile.LoadError);
        Assert.True(cfg.IsPaired);
    }

    [Fact]
    public async Task TheApiTurnsEveryoneAwayWithoutAToken()
    {
        await using var hue = new HueService(new Config(), new FakeBridge().Client());
        await using var api = new ApiServer(hue, "");
        await api.StartAsync(0);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{api.Port}/api/v1/") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("status")).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("status")).StatusCode);
    }
}
