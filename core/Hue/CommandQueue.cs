using System.Text.Json.Nodes;

namespace HueTray.Hue;

/// Sends changes to the bridge no faster than it accepts them: about 10 a second to single lights
/// and 1 a second to groups. While a change waits its turn, newer changes to the same target are
/// merged into it, latest value winning, so a fast jog spin becomes a few sends of the final value.
public sealed class CommandQueue : IAsyncDisposable
{
    public delegate Task Sender(string type, string id, JsonObject change, CancellationToken ct);

    sealed class Pending(string type, string id, JsonObject change)
    {
        public readonly string Type = type, Id = id;
        public readonly JsonObject Change = change;
    }

    readonly Sender send;
    readonly TimeSpan lightInterval, groupInterval;
    readonly object gate = new();
    readonly List<Pending> queue = new();            // oldest first
    readonly SemaphoreSlim wake = new(0);
    readonly CancellationTokenSource stop = new();
    readonly Task worker;
    DateTime nextLight = DateTime.MinValue, nextGroup = DateTime.MinValue;
    TaskCompletionSource idle = Completed();
    int sending;

    public CommandQueue(Sender send, double lightsPerSecond, double groupsPerSecond)
    {
        this.send = send;
        lightInterval = TimeSpan.FromSeconds(1 / Math.Max(0.1, lightsPerSecond));
        groupInterval = TimeSpan.FromSeconds(1 / Math.Max(0.1, groupsPerSecond));
        worker = Task.Run(RunAsync);
    }

    /// Scene recalls and grouped_light changes count against the group limit.
    static bool IsGroup(string type) => type != "light";

    public void Enqueue(string type, string id, JsonObject change)
    {
        lock (gate)
        {
            var existing = queue.FirstOrDefault(p => p.Type == type && p.Id == id);
            if (existing != null) Merge(existing.Change, change);
            else queue.Add(new Pending(type, id, (JsonObject)change.DeepClone()));
            if (idle.Task.IsCompleted) idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        wake.Release();
    }

    /// Completes once everything queued so far has been sent.
    public Task WhenIdle()
    {
        lock (gate) return idle.Task;
    }

    async Task RunAsync()
    {
        var ct = stop.Token;
        while (!ct.IsCancellationRequested)
        {
            Pending? next = null;
            TimeSpan wait = Timeout.InfiniteTimeSpan;
            lock (gate)
            {
                var now = DateTime.UtcNow;
                next = queue.FirstOrDefault(p => (IsGroup(p.Type) ? nextGroup : nextLight) <= now);
                if (next != null)
                {
                    queue.Remove(next);
                    if (IsGroup(next.Type)) nextGroup = now + groupInterval;
                    else nextLight = now + lightInterval;
                    sending++;
                }
                else if (queue.Count > 0)
                {
                    var soonest = queue.Min(p => IsGroup(p.Type) ? nextGroup : nextLight);
                    wait = soonest - now;
                    if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                }
                else if (sending == 0)
                {
                    idle.TrySetResult();
                }
            }

            if (next == null)
            {
                try { await wake.WaitAsync(wait, ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try { await send(next.Type, next.Id, next.Change, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { Log.Write($"Couldn't change {next.Type} {next.Id}: {e.Message}"); }
            finally { lock (gate) sending--; }
        }
        lock (gate) idle.TrySetResult();
    }

    static void Merge(JsonObject target, JsonObject change)
    {
        foreach (var (key, value) in change.ToList())
        {
            if (value is JsonObject child && target[key] is JsonObject existing) Merge(existing, child);
            else target[key] = value?.DeepClone();
        }
    }

    static TaskCompletionSource Completed()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        await worker;
        stop.Dispose();
    }
}
