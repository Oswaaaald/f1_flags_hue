using System.Text.Json;
using System.Globalization;

namespace F1Hue.Core;

public sealed class F1Parser(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _sync = new();
    private FeedState _state = new();
    private string? _trackFlag;
    private string? _neutralisation;
    private readonly HashSet<string> _yellowSectors = [];
    public FeedState State { get { lock (_sync) return _state; } }
    public void Connection(bool connected, string? error = null)
    { lock (_sync) _state = _state with { Connected = connected, LastError = error }; }
    private static string? Text(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v.ToString() : null;
    private static JsonElement Child(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? v : default;
    private static int? Integer(JsonElement obj, string key) => int.TryParse(Text(obj, key), out var n) ? n : null;
    private static string? Track(string? value) => value switch { "1" or "3" => "GREEN", "2" => "YELLOW", "4" => "SC", "5" => "RED", "6" => "VSC", "7" => "VSC_ENDING", _ => null };

    public IReadOnlyList<RaceEvent> Snapshot(JsonElement snapshot)
    {
        lock (_sync)
        {
            var events = new List<RaceEvent>();
            SessionInfo(Child(snapshot, "SessionInfo"));
            Status(Text(Child(snapshot, "SessionStatus"), "Status") ?? Text(Child(snapshot, "SessionInfo"), "SessionStatus"), true, events);
            Lap(Child(snapshot, "LapCount"), true, events);
            Clock(Child(snapshot, "ExtrapolatedClock"));
            var track = Text(Child(snapshot, "TrackStatus"), "Status");
            TrackFlag(Track(track), true, events);
            _state = _state with { LastDataAt = _time.GetUtcNow() };
            return events;
        }
    }
    public IReadOnlyList<RaceEvent> Update(string topic, JsonElement payload)
    {
        lock (_sync)
        {
            var events = new List<RaceEvent>();
            if (payload.ValueKind != JsonValueKind.Object) return events;
            switch (topic)
            {
                case "SessionInfo":
                    SessionInfo(payload);
                    if (Text(payload, "SessionStatus") is string status) Status(status, false, events);
                    break;
                case "SessionStatus": Status(Text(payload, "Status"), false, events); break;
                case "LapCount": Lap(payload, false, events); break;
                case "ExtrapolatedClock": Clock(payload); break;
                case "TrackStatus":
                    var track = Text(payload, "Status");
                    TrackFlag(Track(track), false, events);
                    break;
                case "RaceControlMessages":
                    if (_state.SessionStatus is not ("Started" or "Aborted" or "Finished") || Text(payload, "_kf")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true) break;
                    var messages = Child(payload, "Messages");
                    var rows = messages.ValueKind switch
                    {
                        JsonValueKind.Array => messages.EnumerateArray().ToArray(),
                        JsonValueKind.Object => messages.EnumerateObject().OrderBy(p => int.TryParse(p.Name, out var n) ? n : int.MaxValue).Select(p => p.Value).ToArray(),
                        _ => []
                    };
                    foreach (var row in rows)
                    {
                        var flag = Text(row, "Flag")?.ToUpperInvariant();
                        var category = Text(row, "Category")?.ToLowerInvariant();
                        var message = Text(row, "Message")?.ToUpperInvariant() ?? "";
                        if (_state.SessionStatus == "Finished" && flag != "CHEQUERED") continue;
                        if (category == "flag")
                        {
                            if (flag == "DOUBLE YELLOW") flag = "YELLOW";
                            if (flag is not ("GREEN" or "YELLOW" or "RED" or "BLUE" or "CHEQUERED")) continue;
                        }
                        else if (category == "safetycar")
                        {
                            if (message.Contains("VSC") || message.Contains("VIRTUAL SAFETY CAR")) flag = message.Contains("ENDING") ? "VSC_ENDING" : "VSC";
                            else if (message.Contains("SAFETY CAR")) flag = message.Contains("ENDING") || message.Contains("IN THIS LAP") ? "SC_ENDING" : "SC";
                            else continue;
                        }
                        else continue;
                        ControlFlag(flag, row, events);
                    }
                    break;
            }
            _state = _state with { LastDataAt = _time.GetUtcNow() };
            return events;
        }
    }
    private void SessionInfo(JsonElement info)
    {
        if (info.ValueKind != JsonValueKind.Object) return;
        var key = Text(info, "Key");
        if (key is not null && key != _state.SessionKey)
        {
            _state = _state with { LastFlag = null, CurrentLap = null, TotalLaps = null, Clock = null, SessionStatus = null };
            _trackFlag = null; _neutralisation = null; _yellowSectors.Clear();
        }
        var name = Text(info, "Name"); var meeting = Text(Child(info, "Meeting"), "Name");
        _state = _state with { SessionKey = key ?? _state.SessionKey, SessionName = name is null ? _state.SessionName : meeting is null ? name : meeting + " · " + name, SessionType = Text(info, "Type") ?? _state.SessionType };
    }
    private void Status(string? status, bool initial, List<RaceEvent> events)
    {
        if (status is null) return;
        if (status != _state.SessionStatus || initial) events.Add(Event("session_status", status, initial));
        _state = _state with { SessionStatus = status };
    }
    private void Lap(JsonElement data, bool initial, List<RaceEvent> events)
    {
        if (data.ValueKind != JsonValueKind.Object) return;
        _state = _state with { TotalLaps = Integer(data, "TotalLaps") ?? _state.TotalLaps };
        var lap = Integer(data, "CurrentLap");
        if (lap is > 0 and <= 500 && lap != _state.CurrentLap)
        { _state = _state with { CurrentLap = lap }; events.Add(Event("lap", lap.ToString(), initial)); }
    }
    private void Clock(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return;
        var previous = _state.Clock;
        var utc = SourceUtc(Text(data, "Utc")) ?? previous?.Utc;
        var extrapolating = bool.TryParse(Text(data, "Extrapolating"), out var running) ? running : previous?.Extrapolating ?? false;
        _state = _state with { Clock = new(utc, Text(data, "Remaining") ?? previous?.Remaining, extrapolating) };
    }
    private static DateTimeOffset? SourceUtc(string? text) => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc) ? utc : null;
    private static bool Neutralised(string? flag) => flag is "RED" or "SC" or "SC_ENDING" or "VSC" or "VSC_ENDING";
    private void TrackFlag(string? flag, bool initial, List<RaceEvent> events)
    {
        if (flag is null || !(_state.SessionStatus == "Started" || _state.SessionStatus == "Aborted" && flag == "RED")) return;
        _trackFlag = flag;
        // TrackStatus often repeats SC while race control has announced its end.
        if (flag == "SC" && _neutralisation == "SC_ENDING" || flag == "VSC" && _neutralisation == "VSC_ENDING") flag = _neutralisation;
        _neutralisation = Neutralised(flag) ? flag : null;
        if (flag == "GREEN") _yellowSectors.Clear();
        Flag(flag, initial, null, events);
    }
    private void ControlFlag(string? flag, JsonElement row, List<RaceEvent> events)
    {
        var scope = Text(row, "Scope");
        var sector = Text(row, "Sector");
        var local = sector is not null || Text(row, "RacingNumber") is not null
            || string.Equals(scope, "Sector", StringComparison.OrdinalIgnoreCase)
            || string.Equals(scope, "Driver", StringComparison.OrdinalIgnoreCase);
        if (sector is not null)
        {
            if (flag == "YELLOW") _yellowSectors.Add(sector);
            else if (flag == "GREEN") _yellowSectors.Remove(sector);
        }
        if (flag == "CHEQUERED") { _neutralisation = null; Flag(flag, false, SourceUtc(Text(row, "Utc")), events); return; }
        // Aborted may arrive immediately before RED. Nothing local can release it.
        if (_state.SessionStatus == "Aborted" && flag != "RED") return;
        if (Neutralised(flag))
        {
            if (local || _neutralisation == "RED" && flag != "RED") return;
            _neutralisation = flag;
        }
        else
        {
            if (Neutralised(_neutralisation))
            {
                if (local || flag != "GREEN" || !string.Equals(scope, "Track", StringComparison.OrdinalIgnoreCase)) return;
                _neutralisation = null; _trackFlag = "GREEN"; _yellowSectors.Clear();
            }
            if (local && flag == "GREEN" && (_yellowSectors.Count > 0 || _trackFlag == "YELLOW")) return;
            if (local && flag == "BLUE" && (_yellowSectors.Count > 0 || _trackFlag == "YELLOW")) return;
        }
        Flag(flag, false, SourceUtc(Text(row, "Utc")), events);
    }
    private void Flag(string? flag, bool initial, DateTimeOffset? utc, List<RaceEvent> events)
    {
        if (flag is null || flag == _state.LastFlag) return;
        _state = _state with { LastFlag = flag };
        events.Add(Event("flag", flag, initial) with { SourceUtc = utc });
    }
    private RaceEvent Event(string kind, string? value, bool initial) => new(kind, value, _state.SessionKey, _state.SessionName, _state.SessionType, _time.GetUtcNow(), _time.GetTimestamp(), Initial: initial, TotalLaps: _state.TotalLaps);
}
