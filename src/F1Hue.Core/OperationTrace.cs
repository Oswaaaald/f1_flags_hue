namespace F1Hue.Core;

public sealed record OperationTrace(DateTimeOffset At, string Stage, string? EventId = null,
    string? Flag = null, double? ElapsedMs = null, string? Detail = null);

/// <summary>Bounded, local diagnostic timeline. Reporting must never block lamp control.</summary>
public sealed class OperationTimeline(TimeProvider? time = null)
{
    private readonly object _gate = new();
    private readonly Queue<OperationTrace> _items = new();
    public void Add(string stage, string? eventId = null, string? flag = null, double? elapsedMs = null, string? detail = null)
    {
        lock (_gate)
        {
            _items.Enqueue(new((time ?? TimeProvider.System).GetUtcNow(), stage, eventId, flag, elapsedMs, detail));
            while (_items.Count > 200)
                _items.Dequeue();
        }
    }
    public OperationTrace[] Read()
    {
        lock (_gate)
            return _items.Reverse().ToArray();
    }
}

public sealed class SettingsConflictException() : InvalidOperationException("Les réglages ont changé dans une autre page. Recharge les valeurs enregistrées avant de recommencer.");
