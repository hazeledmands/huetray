using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace HueTray.Tests;

/// Stands in for a bridge: answers GET /clip/v2/resource with a small home, and records every PUT.
sealed class FakeBridge : HttpMessageHandler
{
    public readonly List<(string Path, JsonObject Body)> Puts = new();

    public HttpClient Client() => new(this) { BaseAddress = new Uri("https://bridge.test/") };

    public List<JsonObject> PutsTo(string path)
    {
        lock (Puts) return Puts.Where(p => p.Path == path).Select(p => p.Body).ToList();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        if (request.Method == HttpMethod.Get && path == "clip/v2/resource")
            return Json(new JsonObject { ["errors"] = new JsonArray(), ["data"] = Resources() });
        if (request.Method == HttpMethod.Put)
        {
            var body = (JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            lock (Puts) Puts.Add((path, body));
            return Json(new JsonObject { ["errors"] = new JsonArray(), ["data"] = new JsonArray() });
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    static HttpResponseMessage Json(JsonNode body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    static JsonObject Ref(string rid, string rtype) => new() { ["rid"] = rid, ["rtype"] = rtype };

    static JsonObject LightRes(string id, string device, string name, bool on, double bri, bool color = true) => new()
    {
        ["id"] = id, ["type"] = "light",
        ["owner"] = Ref(device, "device"),
        ["metadata"] = new JsonObject { ["name"] = name },
        ["on"] = new JsonObject { ["on"] = on },
        ["dimming"] = new JsonObject { ["brightness"] = bri },
        ["color_temperature"] = new JsonObject
        {
            ["mirek"] = 370, ["mirek_valid"] = true,
            ["mirek_schema"] = new JsonObject { ["mirek_minimum"] = 153, ["mirek_maximum"] = 454 },
        },
        ["color"] = color ? new JsonObject { ["xy"] = new JsonObject { ["x"] = 0.4, ["y"] = 0.4 } } : null,
    };

    static JsonObject Device(string id, string light, string name) => new()
    {
        ["id"] = id, ["type"] = "device",
        ["metadata"] = new JsonObject { ["name"] = name },
        ["services"] = new JsonArray(Ref(light, "light")),
    };

    static JsonObject Grouped(string id, string owner, string ownerType, bool on, double bri) => new()
    {
        ["id"] = id, ["type"] = "grouped_light",
        ["owner"] = Ref(owner, ownerType),
        ["on"] = new JsonObject { ["on"] = on },
        ["dimming"] = new JsonObject { ["brightness"] = bri },
    };

    static JsonObject Group(string id, string type, string name, string grouped, params JsonObject[] children) => new()
    {
        ["id"] = id, ["type"] = type,
        ["metadata"] = new JsonObject { ["name"] = name },
        ["children"] = new JsonArray(children.Select(c => (JsonNode)c).ToArray()),
        ["services"] = new JsonArray(Ref(grouped, "grouped_light")),
    };

    static JsonObject SceneRes(string id, string name, string group, string groupType, string active = "inactive") => new()
    {
        ["id"] = id, ["type"] = "scene",
        ["metadata"] = new JsonObject { ["name"] = name },
        ["group"] = Ref(group, groupType),
        ["status"] = new JsonObject { ["active"] = active },
    };

    public static JsonArray Resources() => new(
        LightRes("light-sofa", "dev-sofa", "Sofa lamp", true, 40),
        LightRes("light-ceiling", "dev-ceiling", "Ceiling", false, 80, color: false),
        LightRes("light-bed", "dev-bed", "Bedside", true, 20),
        Device("dev-sofa", "light-sofa", "Sofa lamp"),
        Device("dev-ceiling", "light-ceiling", "Ceiling"),
        Device("dev-bed", "light-bed", "Bedside"),
        Group("room-living", "room", "Living room", "gl-living", Ref("dev-sofa", "device"), Ref("dev-ceiling", "device")),
        Group("room-bed", "room", "Bedroom", "gl-bed", Ref("dev-bed", "device")),
        Group("zone-reading", "zone", "Reading", "gl-reading", Ref("light-sofa", "light"), Ref("light-bed", "light")),
        Group("home", "bridge_home", "Home", "gl-home", Ref("room-living", "room"), Ref("room-bed", "room")),
        Grouped("gl-living", "room-living", "room", true, 40),
        Grouped("gl-bed", "room-bed", "room", true, 20),
        Grouped("gl-reading", "zone-reading", "zone", true, 30),
        Grouped("gl-home", "home", "bridge_home", true, 30),
        SceneRes("scene-relax-living", "Relax", "room-living", "room", "static"),
        SceneRes("scene-relax-bed", "Relax", "room-bed", "room"),
        SceneRes("scene-energize", "Energize", "room-living", "room"),
        new JsonObject { ["id"] = "motion-1", ["type"] = "motion" });
}
