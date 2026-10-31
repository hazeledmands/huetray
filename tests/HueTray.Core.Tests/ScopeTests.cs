using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using HueTray.Api;
using HueTray.Hue;

namespace HueTray.Tests;

public class ScopeTests : IAsyncLifetime
{
    const string Token = "test-token";
    readonly FakeBridge bridge = new();
    HueService hue = null!;
    ApiServer api = null!;
    HttpClient http = null!;

    static List<ScopeConfig> FourScopes() => new()
    {
        new() { Name = "Sofa", Targets = { "lights/Sofa lamp" } },
        new() { Name = "Reading", Targets = { "lights/Sofa lamp", "lights/Bedside" } },
        new() { Name = "Living room", Targets = { "rooms/Living room" } },
        new() { Name = "Everywhere", Targets = { "home" } },
    };

    public async Task InitializeAsync()
    {
        var cfg = new Config { LightCommandsPerSecond = 1000, GroupCommandsPerSecond = 1000, Scopes = FourScopes() };
        hue = new HueService(cfg, bridge.Client());
        await hue.LoadAsync();
        api = new ApiServer(hue, Token);
        await api.StartAsync(0);
        http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{api.Port}/api/v1/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public async Task DisposeAsync()
    {
        http.Dispose();
        await api.DisposeAsync();
        await hue.DisposeAsync();
    }

    [Fact]
    public async Task StartsAtTheFirstScope()
    {
        var scope = await http.GetFromJsonAsync<JsonObject>("scope");
        Assert.Equal(1, scope!["index"]!.GetValue<int>());
        Assert.Equal(4, scope["count"]!.GetValue<int>());
        Assert.Equal("Sofa", scope["name"]!.GetValue<string>());
        Assert.Equal("""["lights/Sofa lamp"]""", scope["targets"]!.ToJsonString());
        Assert.Equal("""["Sofa","Reading","Living room","Everywhere"]""", scope["names"]!.ToJsonString());
    }

    [Theory]
    [InlineData("""{"index":3}""", 3)]
    [InlineData("""{"index":9}""", 4)]            // past the end stops at the last
    [InlineData("""{"index":"+1"}""", 2)]
    [InlineData("""{"index":"-1"}""", 1)]         // before the start stops at the first
    [InlineData("""{"name":"living ROOM"}""", 3)]
    [InlineData("""{"position":0}""", 1)]
    [InlineData("""{"position":0.24}""", 1)]
    [InlineData("""{"position":0.25}""", 2)]      // four scopes share the knob's travel equally
    [InlineData("""{"position":0.74}""", 3)]
    [InlineData("""{"position":1}""", 4)]
    [InlineData("""{"position":1.5}""", 4)]
    public async Task ChoosesAScope(string body, int expected)
    {
        var response = await http.PutAsync("scope", Json(body));
        response.EnsureSuccessStatusCode();
        Assert.Equal(expected, (await response.Content.ReadFromJsonAsync<JsonObject>())!["index"]!.GetValue<int>());
        Assert.Equal(expected, hue.Scopes.Current.Index);
    }

    [Fact]
    public async Task StepsCountFromTheCurrentScope()
    {
        (await http.PutAsync("scope", Json("""{"index":3}"""))).EnsureSuccessStatusCode();
        (await http.PutAsync("scope", Json("""{"index":"+1"}"""))).EnsureSuccessStatusCode();
        (await http.PutAsync("scope", Json("""{"index":"+1"}"""))).EnsureSuccessStatusCode();
        Assert.Equal(4, hue.Scopes.Current.Index);
    }

    [Fact]
    public async Task ReportsBadChoices()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await http.PutAsync("scope", Json("""{"name":"Garage"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsync("scope", Json("""{"index":"lots"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsync("scope", Json("""{"index":1,"name":"Sofa"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsync("scope", Json("""{"colour":1}"""))).StatusCode);
        Assert.Equal(1, hue.Scopes.Current.Index);
    }

    [Fact]
    public async Task ChangesEveryLightInTheScope()
    {
        (await http.PutAsync("scope", Json("""{"name":"Reading"}"""))).EnsureSuccessStatusCode();
        var response = await http.PatchAsync("scope/lights", Json("""{"brightness":70}"""));
        response.EnsureSuccessStatusCode();
        var items = await response.Content.ReadFromJsonAsync<JsonArray>();
        Assert.Equal(new[] { "Sofa lamp", "Bedside" }, items!.Select(i => i!["name"]!.GetValue<string>()));

        await hue.WhenIdle();
        Assert.Equal(70, bridge.PutsTo("clip/v2/resource/light/light-sofa").Single()["dimming"]!["brightness"]!.GetValue<double>());
        Assert.Equal(70, bridge.PutsTo("clip/v2/resource/light/light-bed").Single()["dimming"]!["brightness"]!.GetValue<double>());
        Assert.Empty(bridge.PutsTo("clip/v2/resource/light/light-ceiling"));
    }

    [Fact]
    public async Task ChangesRoomsAndTheHome()
    {
        (await http.PutAsync("scope", Json("""{"index":3}"""))).EnsureSuccessStatusCode();
        (await http.PatchAsync("scope/lights", Json("""{"colorTemp":2700}"""))).EnsureSuccessStatusCode();
        (await http.PutAsync("scope", Json("""{"index":4}"""))).EnsureSuccessStatusCode();
        (await http.PatchAsync("scope/lights", Json("""{"on":false}"""))).EnsureSuccessStatusCode();
        await hue.WhenIdle();
        Assert.Single(bridge.PutsTo("clip/v2/resource/grouped_light/gl-living"));
        Assert.Equal("""{"on":{"on":false}}""", bridge.PutsTo("clip/v2/resource/grouped_light/gl-home").Single().ToJsonString());
    }

    [Fact]
    public async Task AMisspeltTargetChangesNothing()
    {
        var scopes = FourScopes();
        scopes[1].Targets.Add("lights/Desk lamp");
        await using var other = new HueService(new Config { Scopes = scopes }, bridge.Client());
        await other.LoadAsync();
        other.Scopes.Select(new ScopeChoice { Index = 2 });
        var error = Assert.Throws<NotFoundException>(() => other.SetScope(new LightChange { On = true }));
        Assert.Contains("Reading", error.Message);
        Assert.Contains("Desk lamp", error.Message);
        await other.WhenIdle();
        Assert.Empty(bridge.Puts);

        scopes[1].Targets[^1] = "garden/Fountain";
        Assert.Throws<FormatException>(() => other.SetScope(new LightChange { On = true }));
    }

    [Fact]
    public async Task WorksWithoutScopes()
    {
        await using var other = new HueService(new Config(), bridge.Client());
        var none = other.Scopes.Current;
        Assert.True(none is { Index: 0, Count: 0, Name: null, Targets.Count: 0, Names.Count: 0 });
        Assert.Throws<NotFoundException>(() => other.Scopes.Select(new ScopeChoice { Index = 1 }));
        Assert.Throws<NotFoundException>(() => other.SetScope(new LightChange { On = true }));
    }

    [Fact]
    public void KeepsTheChoiceWhenRestoredByName()
    {
        var scopes = new Scopes(FourScopes());
        scopes.Restore("Living room");
        Assert.Equal(3, scopes.Current.Index);
        scopes.Restore("Gone");
        Assert.Equal(3, scopes.Current.Index);
    }

    [Fact]
    public async Task StreamsScopeChanges()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{api.Port}/api/v1/events?token={Token}"), default);
        Assert.Equal("status", (await Receive(socket))["type"]!.GetValue<string>());

        (await http.PutAsync("scope", Json("""{"index":2}"""))).EnsureSuccessStatusCode();
        var message = await Receive(socket);
        Assert.Equal("scope", message["type"]!.GetValue<string>());
        Assert.Equal("Reading", message["item"]!["name"]!.GetValue<string>());

        // Choosing the scope that's already chosen sends nothing.
        (await http.PutAsync("scope", Json("""{"index":2}"""))).EnsureSuccessStatusCode();
        (await http.PutAsync("scope", Json("""{"index":1}"""))).EnsureSuccessStatusCode();
        Assert.Equal(1, (await Receive(socket))["item"]!["index"]!.GetValue<int>());
    }

    static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");

    static async Task<JsonObject> Receive(ClientWebSocket socket)
    {
        var buffer = new byte[4096];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        return (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count))!;
    }
}
