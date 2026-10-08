using System.Text.Json;

namespace F1Hue.Infrastructure;

/// <summary>A subscription keyframe precedes deltas already received in transit.</summary>
public sealed class F1SubscriptionBuffer(Action<JsonElement> snapshot, Action<string, JsonElement> delta)
{
    private readonly object _gate = new();
    private readonly Queue<(string Topic, JsonElement Data)> _pending = new();
    private bool _ready;
    public void Receive(string topic, JsonElement data)
    {
        lock (_gate)
        {
            if (_ready)
            {
                delta(topic, data);
                return;
            }
            if (_pending.Count >= 2048)
                throw new IOException("Trop de messages pendant la connexion F1.");
            _pending.Enqueue((topic, data.Clone()));
        }
    }
    public void Complete(JsonElement data)
    {
        lock (_gate)
        {
            if (_ready)
                throw new InvalidOperationException("Abonnement F1 déjà initialisé.");
            snapshot(data);
            while (_pending.TryDequeue(out var item))
                delta(item.Topic, item.Data);
            _ready = true;
        }
    }
}
