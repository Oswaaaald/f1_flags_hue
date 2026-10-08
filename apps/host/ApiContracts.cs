using System.Text.Json;
using F1Hue.Core;
using F1Hue.Infrastructure;

namespace F1Hue.Host;

public sealed record SelectionRequest(string[] LightIds, string[] GroupIds, string? EntertainmentAreaId = null);
public sealed record SceneRecallRequest(string Id, bool Dynamic = false);
public sealed record OffsetAdjustmentRequest(double DeltaSeconds);
public sealed record SetupRequest(string Code, string Password);
public sealed record LoginRequest(string Password);
public sealed record DesktopLoginRequest(string Ticket);
public sealed record PreviewRequest(string Flag);
public sealed record ReplayRequest(string Id, string Kind, double Speed = 1);
public sealed record PairRequest(string Ip);
public sealed record CalibrationArmRequest(string Mode);
public sealed record CalibrationClockRequest(string Remaining);
public sealed record SimulationRequest(string Topic, JsonElement Payload);
public sealed record RecoveryAbandonRequest(string Confirmation);
public sealed record ApiError(string Code, string Error, string TraceId);
public sealed record ApiState(FeedState Feed, RunnerState Runner, bool Initializing, StartupState Startup, RecoveryState? Recovery,
    CalibrationState Calibration, AppSettings Settings, HueStatus Hue, RaceEvent[] Journal, RecordedSession[] Sessions,
    bool Simulation, DateTimeOffset ServerUtc, string Version, string? UiBuild);
public sealed record Scenario(string Id, string Name, string Description);
public sealed class RateLimitException(string message) : InvalidOperationException(message);

public static class ApiInput
{
    public static long? Revision(HttpContext context)
    {
        var header = context.Request.Headers.IfMatch.ToString();
        if (header.Length == 0)
            return null;
        if (!long.TryParse(header.Trim('"'), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var revision))
            throw new ArgumentException("Révision de réglages invalide.");
        return revision;
    }
}
