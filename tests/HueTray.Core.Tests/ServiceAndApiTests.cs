using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using HueTray.Api;

namespace HueTray.Tests;

public class ServiceAndApiTests : IAsyncLifetime
{
    const string Token = "test-token";
    readonly FakeBridge bridge = new();
    HueService hue = null!;
    ApiServer api = null!;
    HttpClient http = null!;

    public async Task InitializeAsync()
    {
        var cfg = new Config { LightCommandsPerSecond = 1000, GroupCommandsPerSecond = 1000 };
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
    public async Task StepsAddUpWithoutWaitingForTheBridge()
    {
        for (int i = 0; i < 10; i++) hue.SetGroup("room", "Living room", new LightChange { Brightness = new Level(2, true) });
        await hue.WhenIdle();
        Assert.Equal(60, bridge.PutsTo("clip/v2/resource/grouped_light/gl-living").Last()["dimming"]!["brightness"]!.GetValue<double>());
        Assert.Equal(60, hue.State.FindGroup("room", "Living room").Brightness);
    }

    [Fact]
    public async Task RejectsRequestsWithoutTheToken()
    {
        using var anonymous = new HttpClient { BaseAddress = http.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("lights")).StatusCode);
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("off", null)).StatusCode);
        Assert.Empty(bridge.Puts);
    }

    [Fact]
    public async Task ListsAndChangesLights()
    {
        var lights = await http.GetFromJsonAsync<JsonArray>("lights");
        Assert.Equal("Bedside", lights![0]!["name"]!.GetValue<string>());
        Assert.Null(lights[0]!["mirekMin"]);

        var response = await http.PatchAsync("lights/Sofa%20lamp", Json("""{"brightness":75,"colorTemp":4000}"""));
        response.EnsureSuccessStatusCode();
        var light = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(75, light!["brightness"]!.GetValue<double>());

        await hue.WhenIdle();
        var put = bridge.PutsTo("clip/v2/resource/light/light-sofa").Single();
        Assert.Equal("""{"dimming":{"brightness":75},"color_temperature":{"mirek":250},"on":{"on":true}}""", put.ToJsonString());
    }

    [Fact]
    public async Task ActivatesScenes()
    {
        var ambiguous = await http.PostAsync("scenes/Relax/activate", null);
        Assert.Equal(HttpStatusCode.Conflict, ambiguous.StatusCode);

        (await http.PostAsync("scenes/Relax/activate", Json("""{"room":"Bedroom"}"""))).EnsureSuccessStatusCode();
        await hue.WhenIdle();
        Assert.Equal("""{"recall":{"action":"active"}}""", bridge.PutsTo("clip/v2/resource/scene/scene-relax-bed").Single().ToJsonString());
    }

    [Fact]
    public async Task ReportsBadRequests()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await http.PatchAsync("rooms/Garage", Json("""{"on":true}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PatchAsync("rooms/Bedroom", Json("""{"brightness":"lots"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PatchAsync("rooms/Bedroom", Json("not json"))).StatusCode);
    }

    [Fact]
    public async Task AllOffUsesTheHomeGroupByDefault()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync("off", null)).StatusCode);
        await hue.WhenIdle();
        Assert.Equal("""{"on":{"on":false}}""", bridge.PutsTo("clip/v2/resource/grouped_light/gl-home").Single().ToJsonString());
    }

    [Fact]
    public async Task StreamsChangesOverAWebSocket()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{api.Port}/api/v1/events?token={Token}"), default);
        Assert.Equal("status", (await Receive(socket))["type"]!.GetValue<string>());

        hue.ApplyEventBatch("""[{"type":"update","data":[{"id":"gl-bed","type":"grouped_light","on":{"on":false}}]}]""");
        var message = await Receive(socket);
        Assert.Equal("room", message["type"]!.GetValue<string>());
        Assert.Equal("Bedroom", message["item"]!["name"]!.GetValue<string>());
        Assert.False(message["item"]!["on"]!.GetValue<bool>());
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
