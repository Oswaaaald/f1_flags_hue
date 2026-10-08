using System.Threading.Channels;

namespace F1Hue.Core;

public interface ILiveFeed
{
    FeedState State
    {
        get;
    }
    event Action<RaceEvent>? Event;
}
public sealed record ReplayItem(double AtSeconds, RaceFlag Flag);

/// <summary>One mode owns the selected lamps. All events, including replays, cross this engine.</summary>
public sealed class Runner(ISettingsStore store, ILiveFeed feed, IEffectOutput output, TimeProvider? time = null, OperationTimeline? timeline = null) : IAsyncDisposable
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _mode = new(1);
    private readonly EffectEngine _engine = new(output, time, timeline);
    private CancellationTokenSource? _cancel;
    private Task _task = Task.CompletedTask;
    private RunnerState _state = new();
    private readonly object _stateGate = new();
    public RunnerState State => Volatile.Read(ref _state);
    public event Action? Changed;
    private void Set(RunnerState state)
    {
        Volatile.Write(ref _state, state);
        Changed?.Invoke();
    }
    private void Update(Func<RunnerState, RunnerState> update)
    {
        lock (_stateGate)
            Volatile.Write(ref _state, update(_state));
        Changed?.Invoke();
    }
    private AppSettings Rules(AppSettings targets) => store.Read() with { LightIds = targets.LightIds, EntertainmentAreaId = targets.EntertainmentAreaId };

    public async Task StartAsync(string mode, RaceFlag? preview = null, ReplayItem[]? replay = null, double speed = 1, CancellationToken ct = default)
    {
        if (mode is not ("live" or "preview" or "sequence" or "replay"))
            throw new ArgumentException("Mode inconnu.");
        if (mode == "preview" && preview is null)
            throw new ArgumentException("Choisis un drapeau.");
        if (mode == "replay" && (replay is null || replay.Length == 0 || !double.IsFinite(speed) || speed < .25 || speed > 100))
            throw new ArgumentException("Replay ou vitesse invalide.");
        await _mode.WaitAsync(ct);
        try
        {
            if (State.Running)
                throw new InvalidOperationException("Arrête le mode actif avant de changer de mode.");
            if (State.CleanupPending)
                throw new InvalidOperationException("L’arrêt des lampes est incomplet. Clique sur Réessayer l’arrêt avant de lancer un autre effet.");
            var targets = store.Read();
            if (targets.LightIds.Length == 0)
                throw new InvalidOperationException("Choisis au moins une lampe dans Hue.");
            try
            {
                await output.PrepareAsync(targets, ct);
                ct.ThrowIfCancellationRequested();
            }
            catch (Exception e)
            {
                Update(s => s with { CleanupPending = output.RecoveryPending, Error = e is OperationCanceledException ? null : e.Message });
                throw;
            }
            _cancel?.Dispose();
            _cancel = new CancellationTokenSource();
            Set(new(true, mode, StartedAt: _time.GetUtcNow()));
            _engine.Changed -= OnEffect;
            _engine.Changed += OnEffect;
            _engine.Failed -= OnFailure;
            _engine.Failed += OnFailure;
            if (output is IEffectFailureSource monitored)
            {
                monitored.Failed -= OnFailure;
                monitored.Failed += OnFailure;
            }
            _task = RunAsync(mode, targets, preview, replay, speed, _cancel.Token);
        }
        finally { _mode.Release(); }
    }
    private void OnEffect(RaceFlag? effect) => Update(s => s with { ActiveEffect = effect?.ToString() });
    private void OnFailure(Exception exception)
    {
        Update(s => s with { Error = exception.Message });
        _cancel?.Cancel();
    }
    private async Task Play(RaceFlag flag, AppSettings targets, bool preview, CancellationToken ct, string? eventId = null)
    {
        var rules = Rules(targets); // Read only when the event is played, after the television delay.
        Update(s => s with { LastFlag = flag.ToString() });
        var began = _time.GetTimestamp();
        timeline?.Add("play_requested", eventId, flag.ToString());
        await _engine.PlayAsync(flag, rules, preview, ct, eventId);
        timeline?.Add(!rules.Effects[flag].Enabled && !preview ? "disabled" : "command_applied", eventId, flag.ToString(), _time.GetElapsedTime(began).TotalMilliseconds);
    }
    private async Task RunAsync(string mode, AppSettings targets, RaceFlag? preview, ReplayItem[]? replay, double speed, CancellationToken ct)
    {
        try
        {
            if (mode == "live")
                await LiveAsync(targets, ct);
            else if (mode == "preview")
            {
                await Play(preview!.Value, targets, true, ct);
                if (Rules(targets).Effects[preview.Value].DurationSeconds is null && Rules(targets).Effects[preview.Value].Mode == "solid")
                    await Task.Delay(Timeout.InfiniteTimeSpan, _time, ct);
                else
                    await _engine.Completion.WaitAsync(ct);
            }
            else if (mode == "sequence")
            {
                foreach (var flag in Enum.GetValues<RaceFlag>())
                {
                    if (!store.Read().Effects[flag].Enabled)
                        continue;
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
                    if (remaining > 0)
                        await Task.Delay(TimeSpan.FromSeconds(remaining), _time, ct);
                    await Play(item.Flag, targets, false, ct);
                }
                if (!_engine.Completion.IsCompleted)
                    await _engine.Completion.WaitAsync(ct);
                else
                    await Task.Delay(TimeSpan.FromSeconds(3), _time, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { Update(s => s with { Error = e.Message }); timeline?.Add("effect_error", detail: e.Message); }
        finally
        {
            await CleanupAsync();
        }
    }
    public void RecoveryFailed(Exception error) => Update(s => s with { CleanupPending = true, Error = "Arrêt incomplet : " + error.Message });
    public void RecoveryResolved()
    {
        if (State.Running || State.Stopping)
            throw new InvalidOperationException("Attends la fin du mode actif.");
        Update(s => s with { CleanupPending = false, Error = null, ActiveEffect = null });
    }
    private async Task CleanupAsync()
    {
        Update(s => s with { Stopping = true });
        timeline?.Add("stopping");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await _engine.StopAsync(store.Read().RestoreOnExit, deadline.Token);
            await output.ReleaseAsync(deadline.Token);
            Update(s => s with
            {
                Running = false,
                Stopping = false,
                CleanupPending = false,
                ActiveEffect = null,
                QueuedEvents = 0,
                NextEffectAt = null,
                Error = s.CleanupPending ? null : s.Error
            });
            timeline?.Add("stopped");
        }
        catch (Exception e)
        {
            Update(s => s with
            {
                Running = false,
                Stopping = false,
                CleanupPending = true,
                ActiveEffect = null,
                QueuedEvents = 0,
                NextEffectAt = null,
                Error = "Arrêt incomplet : " + e.Message + " Vérifie le pont puis réessaie Stop."
            });
            timeline?.Add("recovery_required", detail: e.Message);
        }
    }
    private async Task LiveAsync(AppSettings targets, CancellationToken ct)
    {
        var queue = Channel.CreateBounded<(RaceEvent Event, double Offset)>(new BoundedChannelOptions(2048) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var enqueueGate = new object();
        void Receive(RaceEvent item)
        {
            if (item.Kind != "flag")
                return;
            lock (enqueueGate)
            {
                if (!queue.Writer.TryWrite((item, store.Read().OffsetSeconds)))
                    queue.Writer.TryComplete(new IOException("Trop d’événements en attente : relance le direct."));
                else
                    Update(s => s with { QueuedEvents = s.QueuedEvents + 1 });
            }
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
                lock (enqueueGate)
                    Update(s => s with { QueuedEvents = Math.Max(0, s.QueuedEvents - 1) });
                if (!Enum.TryParse<RaceFlag>(queued.Event.Value, out var flag))
                    continue;
                if (flag == lastPlayed && queued.Event.SessionKey == lastSession)
                    continue;
                var wait = queued.Offset - _time.GetElapsedTime(queued.Event.ReceivedTicks).TotalSeconds;
                Update(s => s with { NextEffectAt = _time.GetUtcNow().AddSeconds(Math.Max(0, wait)) });
                timeline?.Add("scheduled", queued.Event.EventId, flag.ToString(), detail: queued.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture) + " s");
                if (wait > 0)
                    await Task.Delay(TimeSpan.FromSeconds(wait), _time, ct);
                await Play(flag, targets, false, ct, queued.Event.EventId);
                Update(s => s with { NextEffectAt = null });
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
            _cancel?.Cancel();
            await _task;
            if (retry)
                await CleanupAsync();
            if (State.CleanupPending)
                throw new InvalidOperationException(State.Error);
        }
        finally { _mode.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        // An explicitly failed shutdown has already spent its recovery budget.
        // Dispose must not silently start a second 25-second restoration attempt.
        try
        {
            if (State.Running || State.Stopping)
                await StopAsync();
        }
        finally { await _engine.DisposeAsync(); _cancel?.Dispose(); }
    }
}
