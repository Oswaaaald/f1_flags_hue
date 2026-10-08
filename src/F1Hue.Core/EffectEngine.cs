namespace F1Hue.Core;

// Every bridge operation goes through this gate. An expired timer cannot restore
// the baseline after a more recent flag has acquired ownership of the lights.
public sealed class EffectEngine(IEffectOutput output, TimeProvider? time = null, OperationTimeline? timeline = null) : IAsyncDisposable
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _timerGate = new();
    private CancellationTokenSource? _timer;
    private long _generation;
    private Task _completion = Task.CompletedTask;
    public event Action<RaceFlag?>? Changed;
    public event Action<Exception>? Failed;
    public RaceFlag? Active
    {
        get; private set;
    }
    public Task Completion => _completion;

    public async Task PlayAsync(RaceFlag flag, AppSettings settings, bool preview = false, CancellationToken ct = default, string? eventId = null)
    {
        ct.ThrowIfCancellationRequested();
        CancelTimer(false); // Interrupt a settled-state check from the expired flag before waiting for its gate.
        await _gate.WaitAsync(ct);
        try
        {
            CancelTimer(true);
            var generation = ++_generation;
            if (Active is not null)
                timeline?.Add("preempted", eventId, Active.ToString());
            await output.EndAnimationAsync(ct);
            var effect = settings.Effects[flag];
            if (!effect.Enabled && !preview)
            {
                await output.RestoreAsync(ct);
                SetActive(null);
                _completion = Task.CompletedTask;
                return;
            }
            await output.ApplyAsync(flag, effect, settings, ct);
            SetActive(flag);
            var duration = effect.DurationSeconds ?? (effect.Mode == "blink" ? settings.AlertWatchdogSeconds : (double?)null);
            lock (_timerGate)
            {
                _timer = new CancellationTokenSource();
                _completion = duration is double seconds ? ExpireAsync(generation, seconds, _timer.Token, eventId, flag) : Task.CompletedTask;
            }
        }
        finally { _gate.Release(); }
    }
    private async Task ExpireAsync(long generation, double seconds, CancellationToken ct, string? eventId, RaceFlag flag)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), _time, ct);
            await _gate.WaitAsync(ct);
            try
            {
                if (generation != _generation)
                    return;
                timeline?.Add("duration_elapsed", eventId, flag.ToString());
                await output.EndAnimationAsync(ct);
                await output.RestoreAsync(ct);
                timeline?.Add("restored", eventId, flag.ToString());
                SetActive(null);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Failed?.Invoke(ex); }
    }
    public async Task StopAsync(bool restore, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        ct = deadline.Token;
        CancelTimer(false);
        await _gate.WaitAsync(ct);
        try
        {
            ++_generation;
            CancelTimer(false);
            var stopped = false;
            try
            {
                await output.EndAnimationAsync(ct);
                stopped = true;
            }
            finally
            {
                try
                {
                    if (restore)
                        await output.RestoreAsync(ct);
                    else if (stopped)
                        await output.ForgetAsync(ct);
                }
                finally { SetActive(null); }
            }
        }
        finally { _gate.Release(); }
    }
    private void CancelTimer(bool dispose)
    {
        lock (_timerGate)
        {
            _timer?.Cancel();
            if (dispose)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }
    }
    private void SetActive(RaceFlag? flag)
    {
        Active = flag;
        Changed?.Invoke(flag);
    }
    public async ValueTask DisposeAsync()
    {
        if (Active is not null)
            await StopAsync(false);
        CancelTimer(true);
    }
}
