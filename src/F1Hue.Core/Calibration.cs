using System.Globalization;
using System.Text.RegularExpressions;

namespace F1Hue.Core;

public static partial class Calibration
{
    [GeneratedRegex(@"^\d{1,3}:[0-5]\d(?::[0-5]\d)?$")]
    private static partial Regex ClockFormat();
    public static double ParseRemaining(string? value)
    {
        if (value is null || !ClockFormat().IsMatch(value.Trim())) throw new ArgumentException("Utilisez MM:SS ou H:MM:SS.");
        var parts = value.Trim().Split(':').Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        var seconds = parts.Length == 2 ? parts[0] * 60 + parts[1] : parts[0] * 3600 + parts[1] * 60 + parts[2];
        if (seconds > 21600) throw new ArgumentException("Le chrono dépasse six heures.");
        return seconds;
    }
    public static double Remaining(SessionClock? clock, DateTimeOffset at)
    {
        if (clock is not { Extrapolating: true, Utc: not null }) throw new ArgumentException("L’horloge F1 est arrêtée ou indisponible.");
        var elapsed = (at - clock.Utc.Value).TotalSeconds;
        if (elapsed is < -30 or > 21600) throw new ArgumentException("Horloge F1 périmée ou horloge de l’ordinateur désynchronisée.");
        var seconds = ParseRemaining(clock.Remaining) - elapsed;
        if (seconds <= 0) throw new ArgumentException("Le chrono F1 est terminé.");
        return seconds;
    }
    public static double Compare(SessionClock? clock, string tv, DateTimeOffset at) => Math.Round(ParseRemaining(tv) + .5 - Remaining(clock, at), 1);
}
