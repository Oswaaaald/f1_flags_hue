using System.Text.Json;
using System.Text.Json.Serialization;

namespace F1Hue.Core;

[JsonConverter(typeof(JsonStringEnumConverter<RaceFlag>))]
public enum RaceFlag
{
    GREEN, YELLOW, RED, SC, SC_ENDING, VSC, VSC_ENDING, BLUE, CHEQUERED
}
public sealed record EffectSpec(bool Enabled, double? DurationSeconds, string Mode, double X, double Y);
public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public long Revision
    {
        get; init;
    }
    public int Brightness { get; init; } = 254;
    public double TransitionSeconds { get; init; } = .5;
    public double OffsetSeconds
    {
        get; init;
    }
    public double AlertWatchdogSeconds { get; init; } = 600;
    public bool RestoreOnExit { get; init; } = true;
    public bool ExitOnChequered
    {
        get; init;
    }
    public bool AutoLive
    {
        get; init;
    }
    public string[] LightIds { get; init; } = [];
    public string? EntertainmentAreaId
    {
        get; init;
    }
    public Dictionary<RaceFlag, EffectSpec> Effects { get; init; } = Defaults();
    public static Dictionary<RaceFlag, EffectSpec> Defaults() => new()
    {
        [RaceFlag.GREEN] = new(true, 8, "solid", .17, .7),
        [RaceFlag.YELLOW] = new(true, null, "solid", .545, .455),
        [RaceFlag.RED] = new(true, null, "solid", .675, .322),
        [RaceFlag.SC] = new(true, null, "blink", .545, .455),
        [RaceFlag.SC_ENDING] = new(true, 20, "solid", .545, .455),
        [RaceFlag.VSC] = new(true, null, "blink", .545, .455),
        [RaceFlag.VSC_ENDING] = new(true, 15, "solid", .545, .455),
        [RaceFlag.BLUE] = new(false, 2.1, "blink", .15, .06),
        [RaceFlag.CHEQUERED] = new(true, 10, "blink", .3227, .329),
    };
    public AppSettings Validate()
    {
        if (SchemaVersion != 1)
            throw new InvalidOperationException("Cette configuration nécessite une autre version de F1 Hue Sync. Restaure une sauvegarde compatible ou mets l’application à jour.");
        static bool Valid(double n, double min, double max) => double.IsFinite(n) && n >= min && n <= max;
        if (Brightness is < 1 or > 254 || !Valid(TransitionSeconds, 0, 10) || !Valid(OffsetSeconds, 0, 3600)
            || !Valid(AlertWatchdogSeconds, 30, 3600))
            throw new ArgumentException("Réglages numériques invalides.");
        if (LightIds is null || LightIds.Length > 100 || LightIds.Any(id => !Guid.TryParse(id, out _)) || LightIds.Distinct().Count() != LightIds.Length)
            throw new ArgumentException("Sélection de lampes invalide.");
        if (EntertainmentAreaId is not null && !Guid.TryParse(EntertainmentAreaId, out _))
            throw new ArgumentException("Zone Entertainment invalide.");
        if (Effects is null || Effects.Count != 9 || Enum.GetValues<RaceFlag>().Any(f => !Effects.ContainsKey(f)))
            throw new ArgumentException("Les neuf drapeaux sont requis.");
        foreach (var effect in Effects.Values)
            if (effect is null || effect.Mode is not ("solid" or "blink") || effect.DurationSeconds is double d && !Valid(d, .1, 3600)
                || !Valid(effect.X, 0, 1) || !Valid(effect.Y, .001, 1) || effect.X + effect.Y > 1.001)
                throw new ArgumentException("Durée, couleur ou mode d’effet invalide.");
        return this;
    }
}
public sealed record RaceEvent(string Kind, string? Value, string? SessionKey, string? SessionName,
    string? SessionType, DateTimeOffset ReceivedAt, long ReceivedTicks, DateTimeOffset? SourceUtc = null,
    bool Initial = false, int? TotalLaps = null, string? EventId = null);
public sealed record SessionClock(DateTimeOffset? Utc, string? Remaining, bool Extrapolating);
public sealed record FeedState(bool Connected = false, string? SessionKey = null, string? SessionName = null,
    string? SessionType = null, string? SessionStatus = null, int? CurrentLap = null, int? TotalLaps = null,
    SessionClock? Clock = null, string? LastFlag = null, DateTimeOffset? LastDataAt = null, string? LastError = null,
    string? JournalError = null, string? ProcessingError = null);
public sealed record RunnerState(bool Running = false, string? Mode = null, string? LastFlag = null,
    string? ActiveEffect = null, string? Error = null, DateTimeOffset? StartedAt = null,
    bool Stopping = false, bool CleanupPending = false, int QueuedEvents = 0, DateTimeOffset? NextEffectAt = null);
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };
}
public interface ISettingsStore
{
    AppSettings Read(); void Save(AppSettings settings);
}
public interface IEffectOutput
{
    bool RecoveryPending => false;
    Task PrepareAsync(AppSettings settings, CancellationToken cancellationToken) => CaptureAsync(settings, cancellationToken);
    Task CaptureAsync(AppSettings settings, CancellationToken cancellationToken);
    Task ApplyAsync(RaceFlag flag, EffectSpec effect, AppSettings settings, CancellationToken cancellationToken);
    Task EndAnimationAsync(CancellationToken cancellationToken);
    Task RestoreAsync(CancellationToken cancellationToken);
    Task ForgetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task ReleaseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
public interface IEffectFailureSource
{
    event Action<Exception>? Failed;
}
