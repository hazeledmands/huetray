using System.Text.Json.Nodes;
using HueTray.Hue;

namespace HueTray.Tests;

public class StateTests
{
    static HueState Loaded()
    {
        var state = new HueState();
        state.Load(FakeBridge.Resources().OfType<JsonObject>().Where(r => HueClient.Types.Contains(r["type"]!.GetValue<string>())));
        return state;
    }

    [Fact]
    public void BuildsSnapshots()
    {
        var state = Loaded();
        Assert.Equal(new[] { "Bedside", "Ceiling", "Sofa lamp" }, state.Lights().Select(l => l.Name));

        var sofa = state.FindLight("sofa LAMP");
        Assert.True(sofa.On);
        Assert.Equal(40, sofa.Brightness);
        Assert.Equal(2703, sofa.Kelvin);
        Assert.Equal("Living room", sofa.Room);
        Assert.Equal(454, sofa.MirekMax);
        Assert.False(state.FindLight("Ceiling").SupportsColor);

        var living = state.FindGroup("room", "living room");
        Assert.Equal("gl-living", living.GroupedLightId);
        Assert.Equal(new[] { "light-sofa", "light-ceiling" }, living.LightIds);
        Assert.Equal(new[] { "light-sofa", "light-bed" }, state.FindGroup("zone", "Reading").LightIds);
        Assert.Equal(3, state.Home()!.LightIds.Count);

        Assert.True(state.FindScene("Relax", "Living room").Active);
        Assert.False(state.FindScene("relax", "room-bed").Active);
    }

    [Fact]
    public void ReportsMissingAndAmbiguousNames()
    {
        var state = Loaded();
        Assert.Throws<NotFoundException>(() => state.FindLight("Garage"));
        var e = Assert.Throws<AmbiguousException>(() => state.FindScene("Relax"));
        Assert.Contains("Bedroom", e.Message);
        Assert.Equal("scene-energize", state.FindScene("Energize").Id);
    }

    [Fact]
    public void MergesEventsAndHoldsLocalChanges()
    {
        var state = Loaded();
        var changes = new List<StateChange>();
        state.Changed += c => changes.AddRange(c);

        state.ApplyEvents(new[] { ("update", Obj("""{"id":"gl-bed","type":"grouped_light","dimming":{"brightness":55}}""")) });
        Assert.Equal(55, state.FindGroup("room", "Bedroom").Brightness);
        Assert.Contains(changes, c => c.Type == "room" && c.Id == "room-bed");

        // Right after a local change, the bridge's (older) reports for that light are ignored.
        state.ApplyLocal("light-sofa", Obj("""{"dimming":{"brightness":90}}"""));
        state.ApplyEvents(new[] { ("update", Obj("""{"id":"light-sofa","type":"light","dimming":{"brightness":45},"metadata":{"name":"Couch"}}""")) });
        var sofa = state.LightById("light-sofa")!;
        Assert.Equal(90, sofa.Brightness);
        Assert.Equal("Couch", sofa.Name);

        state.ApplyEvents(new[] { ("delete", Obj("""{"id":"scene-energize","type":"scene"}""")) });
        Assert.Throws<NotFoundException>(() => state.FindScene("Energize"));
    }

    static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;
}
