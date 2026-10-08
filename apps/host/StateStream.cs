using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using F1Hue.Core;

namespace F1Hue.Host;

/// <summary>One serialized projection per change, shared by all open pages.</summary>
public sealed class StateStream(Func<object> read)
{
    private readonly ConcurrentDictionary<Guid, Channel<bool>> _subscribers = new();
    private readonly SemaphoreSlim _slots = new(20, 20);
    private readonly object _cacheGate = new();
    private long _version;
    private long _serializedVersion = -1;
    private string _serialized = "";
    public void Changed()
    {
        Interlocked.Increment(ref _version);
        foreach (var subscriber in _subscribers.Values)
            subscriber.Writer.TryWrite(true);
    }
    private string Serialize(bool initial)
    {
        lock (_cacheGate)
        {
            var version = Interlocked.Read(ref _version);
            if (initial || version != _serializedVersion)
            {
                _serialized = "data: " + JsonSerializer.Serialize(read(), JsonDefaults.Options) + "\n\n";
                _serializedVersion = version;
            }
            return _serialized;
        }
    }
    public async Task ConnectAsync(HttpContext context, Auth auth, CancellationToken stopping)
    {
        if (!_slots.Wait(0))
            throw new RateLimitException("Trop de pages connectées. Ferme un autre onglet.");
        var id = Guid.NewGuid();
        var queue = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        _subscribers[id] = queue;
        queue.Writer.TryWrite(true);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        var initial = true;
        try
        {
            while (!lifetime.IsCancellationRequested && auth.Valid(context.Request.Cookies["f1hue_session"]))
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
                var changed = true;
                try
                {
                    await queue.Reader.ReadAsync(heartbeat.Token);
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { changed = false; }
                await context.Response.WriteAsync(changed ? Serialize(initial) : ": heartbeat\n\n", lifetime.Token);
                await context.Response.Body.FlushAsync(lifetime.Token);
                initial = false;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { _subscribers.TryRemove(id, out _); _slots.Release(); }
    }
}
