namespace F1Hue.Core;

public sealed record StartupState(bool Initializing = true, string Phase = "initializing", string? Error = null, DateTimeOffset? RetryAt = null);

/// <summary>A manual action cancels the startup intention for this process.</summary>
public sealed class AutomaticStartup(TimeProvider? time = null) : IDisposable
{
    private readonly CancellationTokenSource _manual = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private StartupState _state = new();
    public StartupState State => Volatile.Read(ref _state);
    public event Action? Changed;
    private void Set(StartupState state)
    {
        Volatile.Write(ref _state, state);
        Changed?.Invoke();
    }
    public void Cancel() => _manual.Cancel();
    public async Task RunAsync(Func<CancellationToken, Task> attempt, Func<bool> retryWanted, CancellationToken stopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, _manual.Token);
        var delay = 2d;
        try
        {
            while (true)
            {
                try
                {
                    await attempt(linked.Token);
                    Set(new(false, "ready"));
                    return;
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    if (!retryWanted())
                    {
                        Set(new(false, "needs_attention", e.Message));
                        return;
                    }
                    Set(new(false, "waiting", e.Message, _time.GetUtcNow().AddSeconds(delay)));
                }
                await Task.Delay(TimeSpan.FromSeconds(delay), _time, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                if (!retryWanted())
                {
                    Set(new(false, "idle"));
                    return;
                }
                delay = Math.Min(delay * 2, 60);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { Set(new(false, "idle")); }
    }
    public void Dispose()
    {
        _manual.Cancel();
        _manual.Dispose();
    }
}
