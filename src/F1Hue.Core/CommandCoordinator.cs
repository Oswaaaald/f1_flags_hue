namespace F1Hue.Core;

/// <summary>Serializes ownership changes after request binding. Stop cancels both
/// active work and commands received before it, including unfinished downloads.</summary>
public sealed class CommandCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource _epoch = new();

    public CancellationTokenSource Request(CancellationToken ct = default)
    {
        lock (_sync)
            return CancellationTokenSource.CreateLinkedTokenSource(ct, _epoch.Token);
    }

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            return await action(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(Func<Task> stop)
    {
        Task turn;
        CancellationTokenSource previous;
        lock (_sync)
        {
            // Reserve Stop's position before accepting work in the new epoch.
            turn = _gate.WaitAsync();
            previous = _epoch;
            _epoch = new();
        }
        previous.Cancel();
        previous.Dispose();
        await turn;
        try
        {
            await stop();
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _epoch.Cancel();
        _epoch.Dispose();
        _gate.Dispose();
    }
}
