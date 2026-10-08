namespace F1Hue.Core;

public sealed record CalibrationState(string? Mode = null, bool Waiting = false, RaceEvent? Reference = null, double? ProposedOffset = null);
public sealed class CalibrationSession
{
    private readonly ILiveFeed _feed;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private CalibrationState _state = new();
    public CalibrationState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }
    public event Action? Changed;
    public CalibrationSession(ILiveFeed feed, TimeProvider? time = null)
    {
        _feed = feed;
        _time = time ?? TimeProvider.System;
        feed.Event += Observe;
    }
    public void Arm(string mode)
    {
        if (mode is not ("start" or "flag" or "lap"))
            throw new ArgumentException("Méthode de calibration inconnue.");
        if (!_feed.State.Connected)
            throw new InvalidOperationException("Attends la connexion au flux F1.");
        lock (_gate)
            _state = new(mode, true);
        Changed?.Invoke();
    }
    private void Observe(RaceEvent item)
    {
        lock (_gate)
        {
            if (!_state.Waiting || item.Initial)
                return;
            if (!((_state.Mode == "start" && item.Kind == "session_status" && item.Value == "Started" && item.SessionType == "Race") || (_state.Mode == "flag" && item.Kind == "flag") || (_state.Mode == "lap" && item.Kind == "lap")))
                return;
            _state = _state with
            {
                Waiting = false,
                Reference = item
            };
        }
        Changed?.Invoke();
    }
    public double Seen()
    {
        double offset;
        lock (_gate)
        {
            var reference = _state.Reference ?? throw new InvalidOperationException("Le flux F1 n’a pas encore envoyé l’événement attendu.");
            if (_feed.State.SessionKey != reference.SessionKey)
                throw new InvalidOperationException("La séance a changé. Recommence la mesure.");
            offset = Math.Round(_time.GetElapsedTime(reference.ReceivedTicks).TotalSeconds, 1);
            if (offset is < 0 or > 3600)
                throw new InvalidOperationException("Mesure expirée.");
            _state = _state with
            {
                ProposedOffset = offset
            };
        }
        Changed?.Invoke();
        return offset;
    }
    public double Compare(string remaining)
    {
        if (!_feed.State.Connected || _feed.State.SessionStatus != "Started")
            throw new InvalidOperationException("Le chrono de séance n’est pas actif.");
        var offset = Calibration.Compare(_feed.State.Clock, remaining, _time.GetUtcNow());
        if (offset is < 0 or > 3600)
            throw new ArgumentException("La mesure donne un décalage hors limites. Vérifie le chrono saisi.");
        lock (_gate)
            _state = new("clock", ProposedOffset: offset);
        Changed?.Invoke();
        return offset;
    }
    public void Cancel()
    {
        lock (_gate)
            _state = new();
        Changed?.Invoke();
    }
}
