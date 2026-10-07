namespace F1Hue.Core;

// Every bridge operation goes through this gate. An expired timer cannot restore
// the baseline after a more recent flag has acquired ownership of the lights.
public sealed class EffectEngine(IEffectOutput output, TimeProvider? time = null) : IAsyncDisposable
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _timer;
    private long _generation;
    private Task _completion = Task.CompletedTask;
    public event Action<RaceFlag?>? Changed;
    public event Action<Exception>? Failed;
    public RaceFlag? Active { get; private set; }
    public Task Completion => _completion;

    public async Task PlayAsync(RaceFlag flag, AppSettings settings, bool preview = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _timer?.Cancel(); _timer?.Dispose(); _timer = null;
            var generation = ++_generation;
            await output.EndAnimationAsync(ct);
            var effect = settings.Effects[flag];
            if (!effect.Enabled && !preview)
            {
                await output.RestoreAsync(ct);
                SetActive(null); _completion = Task.CompletedTask; return;
            }
            await output.ApplyAsync(flag, effect, settings, ct);
            SetActive(flag);
            var duration = effect.DurationSeconds ?? (effect.Mode == "blink" ? settings.AlertWatchdogSeconds : (double?)null);
            _timer = new CancellationTokenSource();
            _completion = duration is double seconds ? ExpireAsync(generation, seconds, _timer.Token) : Task.CompletedTask;
        }
        finally { _gate.Release(); }
    }
    private async Task ExpireAsync(long generation, double seconds, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), _time, ct);
            await _gate.WaitAsync(ct);
            try
            {
                if (generation != _generation) return;
                await output.EndAnimationAsync(ct);
                await output.RestoreAsync(ct);
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
        deadline.CancelAfter(TimeSpan.FromSeconds(25)); ct = deadline.Token;
        await _gate.WaitAsync(ct);
        try
        {
            ++_generation; _timer?.Cancel();
            var stopped = false;
            try { await output.EndAnimationAsync(ct); stopped = true; }
            finally
            {
                try { if (restore) await output.RestoreAsync(ct); else if (stopped) await output.ForgetAsync(ct); }
                finally { SetActive(null); }
            }
        }
        finally { _gate.Release(); }
    }
    private void SetActive(RaceFlag? flag) { Active = flag; Changed?.Invoke(flag); }
    public async ValueTask DisposeAsync() { if (Active is not null) await StopAsync(false); _timer?.Cancel(); _timer?.Dispose(); }
}
