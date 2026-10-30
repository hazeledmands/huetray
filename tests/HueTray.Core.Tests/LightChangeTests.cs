using System.Text.Json;

namespace HueTray.Tests;

public class LightChangeTests
{
    static LightChange Parse(string json) => LightChange.Parse(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void StepsFromTheCurrentBrightness()
    {
        Assert.Equal("""{"dimming":{"brightness":45},"on":{"on":true}}""",
                     Parse("""{"brightness":"+5"}""").ToHue(40, true).ToJsonString());
        Assert.Equal("""{"dimming":{"brightness":100},"on":{"on":true}}""",
                     Parse("""{"brightness":"+20"}""").ToHue(90, true).ToJsonString());
    }

    [Fact]
    public void ReachingZeroTurnsOffAndStepsUpFromOff()
    {
        Assert.Equal("""{"on":{"on":false}}""", Parse("""{"brightness":"-10"}""").ToHue(5, true).ToJsonString());
        Assert.Equal("{}", Parse("""{"brightness":"-10"}""").ToHue(50, false).ToJsonString());
        Assert.Equal("""{"dimming":{"brightness":3},"on":{"on":true}}""",
                     Parse("""{"brightness":"+3"}""").ToHue(50, false).ToJsonString());
    }

    [Fact]
    public void ConvertsColourAndTemperature()
    {
        var body = Parse("""{"colorTemp":2700,"color":"#f00","transitionMs":400}""").ToHue(50, true, 153, 454);
        Assert.Equal(370, body["color_temperature"]!["mirek"]!.GetValue<int>());
        Assert.Equal(0.7006, body["color"]!["xy"]!["x"]!.GetValue<double>(), 3);
        Assert.Equal(0.2993, body["color"]!["xy"]!["y"]!.GetValue<double>(), 3);
        Assert.Equal(400, body["dynamics"]!["duration"]!.GetValue<int>());
        Assert.Equal(454, Parse("""{"colorTemp":1000}""").ToHue(50, true, 153, 454)["color_temperature"]!["mirek"]!.GetValue<int>());
    }

    [Fact]
    public void RejectsBadInput()
    {
        Assert.Throws<FormatException>(() => Parse("""{"brightness":"loud"}"""));
        Assert.Throws<FormatException>(() => Parse("""{"color":"#12"}"""));
        Assert.Throws<FormatException>(() => Parse("""{"hue":3}"""));
    }
}
