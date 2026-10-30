using System.Diagnostics;
using System.Text.Json.Nodes;
using HueTray.Hue;

namespace HueTray.Tests;

public class CommandQueueTests
{
    [Fact]
    public async Task MergesChangesWaitingToBeSent()
    {
        var sent = new List<string>();
        var release = new TaskCompletionSource();
        await using var queue = new CommandQueue(async (type, id, change, ct) =>
        {
            lock (sent) sent.Add($"{type}/{id} {change.ToJsonString()}");
            await release.Task;
        }, 1000, 1000);

        queue.Enqueue("light", "a", Bri(1));       // goes straight out and blocks
        await Task.Delay(50);
        for (int i = 2; i <= 100; i++) queue.Enqueue("light", "a", Bri(i));
        queue.Enqueue("light", "a", new JsonObject { ["on"] = new JsonObject { ["on"] = true } });
        release.SetResult();
        await queue.WhenIdle();

        Assert.Equal(new[]
        {
            """light/a {"dimming":{"brightness":1}}""",
            """light/a {"dimming":{"brightness":100},"on":{"on":true}}""",
        }, sent);
    }

    [Fact]
    public async Task SpacesOutSendsAndKeepsLightsMovingWhileGroupsWait()
    {
        var sent = new List<(string Key, long Ms)>();
        var clock = Stopwatch.StartNew();
        await using var queue = new CommandQueue((type, id, _, _) =>
        {
            lock (sent) sent.Add(($"{type}/{id}", clock.ElapsedMilliseconds));
            return Task.CompletedTask;
        }, lightsPerSecond: 20, groupsPerSecond: 4);

        queue.Enqueue("grouped_light", "g1", Bri(1));
        queue.Enqueue("grouped_light", "g2", Bri(1));
        queue.Enqueue("light", "a", Bri(1));
        queue.Enqueue("light", "b", Bri(1));
        await queue.WhenIdle();

        Assert.Equal(new[] { "grouped_light/g1", "light/a", "light/b", "grouped_light/g2" }, sent.Select(s => s.Key));
        long Gap(string a, string b) => sent.Single(s => s.Key == b).Ms - sent.Single(s => s.Key == a).Ms;
        Assert.InRange(Gap("light/a", "light/b"), 40, 200);
        Assert.InRange(Gap("grouped_light/g1", "grouped_light/g2"), 230, 600);
    }

    [Fact]
    public async Task KeepsGoingAfterAFailedSend()
    {
        int calls = 0;
        await using var queue = new CommandQueue((_, id, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return id == "bad" ? throw new HueException("nope") : Task.CompletedTask;
        }, 1000, 1000);
        queue.Enqueue("light", "bad", Bri(1));
        queue.Enqueue("light", "good", Bri(1));
        await queue.WhenIdle();
        Assert.Equal(2, calls);
    }

    static JsonObject Bri(double b) => new() { ["dimming"] = new JsonObject { ["brightness"] = b } };
}
