using System.Threading.Channels;

namespace F1Hue.Core;

public interface ILiveFeed
{
    FeedState State { get; }
    event Action<RaceEvent>? Event;
}
public sealed record ReplayItem(double AtSeconds, RaceFlag Flag);

/// <summary>One mode owns the selected lamps. All events, including replays, cross this engine.</summary>
public sealed class Runner(ISettingsStore store, ILiveFeed feed, IEffectOutput output, TimeProvider? time = null) : IAsyncDisposable
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _mode = new(1);
    private readonly EffectEngine _engine = new(output, time);
    private CancellationTokenSource? _cancel;
    private Task _task = Task.CompletedTask;
    private RunnerState _state = new();
    public RunnerState State => Volatile.Read(ref _state);
    public event Action? Changed;
    private void Set(RunnerState state) { Volatile.Write(ref _state, state); Changed?.Invoke(); }
    private AppSettings Rules(AppSettings targets) => store.Read() with { LightIds = targets.LightIds, EntertainmentAreaId = targets.EntertainmentAreaId };

    public async Task StartAsync(string mode, RaceFlag? preview = null, ReplayItem[]? replay = null, double speed = 1, CancellationToken ct = default)
    {
        if (mode is not ("live" or "preview" or "sequence" or "replay")) throw new ArgumentException("Mode inconnu.");
        if (mode == "preview" && preview is null) throw new ArgumentException("Choisis un drapeau.");
        if (mode == "replay" && (replay is null || replay.Length == 0 || !double.IsFinite(speed) || speed < .25 || speed > 100)) throw new ArgumentException("Replay ou vitesse invalide.");
        await _mode.WaitAsync(ct);
        try
        {
            if (State.Running) throw new InvalidOperationException("Arrête le mode actif avant de changer de mode.");
            if (State.CleanupPending) throw new InvalidOperationException("L’arrêt des lampes est incomplet. Clique sur Réessayer l’arrêt avant de lancer un autre effet.");
            var targets = store.Read();
            if (targets.LightIds.Length == 0) throw new InvalidOperationException("Choisis au moins une lampe dans Hue.");
            try
            {
                await output.CaptureAsync(targets, ct);
                ct.ThrowIfCancellationRequested();
            }
            catch (Exception e) { RecoveryFailed(e); throw; }
            _cancel?.Dispose(); _cancel = new CancellationTokenSource();
            Set(new(true, mode, StartedAt: _time.GetUtcNow()));
            _engine.Changed -= OnEffect; _engine.Changed += OnEffect;
            _engine.Failed -= OnFailure; _engine.Failed += OnFailure;
            if (output is IEffectFailureSource monitored) { monitored.Failed -= OnFailure; monitored.Failed += OnFailure; }
            _task = RunAsync(mode, targets, preview, replay, speed, _cancel.Token);
        }
        finally { _mode.Release(); }
    }
    private void OnEffect(RaceFlag? effect) => Set(State with { ActiveEffect = effect?.ToString() });
    private void OnFailure(Exception exception) { Set(State with { Error = exception.Message }); _cancel?.Cancel(); }
    private async Task Play(RaceFlag flag, AppSettings targets, bool preview, CancellationToken ct)
    {
        var rules = Rules(targets); // Read only when the event is played, after the television delay.
        Set(State with { LastFlag = flag.ToString() });
        await _engine.PlayAsync(flag, rules, preview, ct);
    }
    private async Task RunAsync(string mode, AppSettings targets, RaceFlag? preview, ReplayItem[]? replay, double speed, CancellationToken ct)
    {
        try
        {
            if (mode == "live") await LiveAsync(targets, ct);
            else if (mode == "preview")
            {
                await Play(preview!.Value, targets, true, ct);
                if (Rules(targets).Effects[preview.Value].DurationSeconds is null && Rules(targets).Effects[preview.Value].Mode == "solid")
                    await Task.Delay(Timeout.InfiniteTimeSpan, _time, ct);
                else await _engine.Completion.WaitAsync(ct);
            }
            else if (mode == "sequence")
            {
                foreach (var flag in Enum.GetValues<RaceFlag>())
                {
                    if (!store.Read().Effects[flag].Enabled) continue;
                    await Play(flag, targets, false, ct);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(store.Read().Effects[flag].DurationSeconds ?? 4, 4)), _time, ct);
                }
            }
            else
            {
                var start = _time.GetTimestamp();
                foreach (var item in replay!)
                {
                    var remaining = item.AtSeconds / speed - _time.GetElapsedTime(start).TotalSeconds;
                    if (remaining > 0) await Task.Delay(TimeSpan.FromSeconds(remaining), _time, ct);
                    await Play(item.Flag, targets, false, ct);
                }
                if (!_engine.Completion.IsCompleted) await _engine.Completion.WaitAsync(ct);
                else await Task.Delay(TimeSpan.FromSeconds(3), _time, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { Set(State with { Error = e.Message }); }
        finally
        {
            await CleanupAsync();
        }
    }
    public void RecoveryFailed(Exception error) => Set(State with { CleanupPending = true, Error = "Arrêt incomplet : " + error.Message });
    private async Task CleanupAsync()
    {
        Set(State with { Stopping = true });
        try
        {
            await _engine.StopAsync(store.Read().RestoreOnExit);
            Set(State with { Running = false, Stopping = false, CleanupPending = false, ActiveEffect = null,
                Error = State.CleanupPending ? null : State.Error });
        }
        catch (Exception e)
        {
            Set(State with { Running = false, Stopping = false, CleanupPending = true, ActiveEffect = null,
                Error = "Arrêt incomplet : " + e.Message + " Vérifie le pont puis réessaie Stop." });
        }
    }
    private async Task LiveAsync(AppSettings targets, CancellationToken ct)
    {
        var queue = Channel.CreateBounded<(RaceEvent Event, double Offset)>(new BoundedChannelOptions(2048) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var enqueueGate = new object();
        void Receive(RaceEvent item)
        {
            if (item.Kind != "flag") return;
            lock (enqueueGate)
                if (!queue.Writer.TryWrite((item, store.Read().OffsetSeconds)))
                    queue.Writer.TryComplete(new IOException("Trop d’événements en attente : relance le direct."));
        }
        feed.Event += Receive;
        try
        {
            // Starting mid-session also synchronizes the current flag. Finished snapshots never play.
            lock (enqueueGate)
            {
                var current = feed.State;
                if (current.Connected && (current.SessionStatus == "Started" || current.SessionStatus == "Aborted" && current.LastFlag == "RED") && current.LastFlag is not null)
                    Receive(new("flag", current.LastFlag, current.SessionKey, current.SessionName, current.SessionType, _time.GetUtcNow(), _time.GetTimestamp(), Initial: true));
            }
            RaceFlag? lastPlayed = null;
            string? lastSession = null;
            await foreach (var queued in queue.Reader.ReadAllAsync(ct))
            {
                if (!Enum.TryParse<RaceFlag>(queued.Event.Value, out var flag)) continue;
                if (flag == lastPlayed && queued.Event.SessionKey == lastSession) continue;
                var wait = queued.Offset - _time.GetElapsedTime(queued.Event.ReceivedTicks).TotalSeconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), _time, ct);
                await Play(flag, targets, false, ct);
                lastPlayed = flag;
                lastSession = queued.Event.SessionKey;
                if (flag == RaceFlag.CHEQUERED && store.Read().ExitOnChequered)
                {
                    await _engine.Completion.WaitAsync(ct);
                    break;
                }
            }
        }
        finally { feed.Event -= Receive; }
    }
    public async Task StopAsync()
    {
        await _mode.WaitAsync();
        try
        {
            var retry = State.CleanupPending;
            _cancel?.Cancel(); await _task;
            if (retry) await CleanupAsync();
            if (State.CleanupPending) throw new InvalidOperationException(State.Error);
        }
        finally { _mode.Release(); }
    }
    public async ValueTask DisposeAsync() { await StopAsync(); await _engine.DisposeAsync(); _cancel?.Dispose(); }
}
