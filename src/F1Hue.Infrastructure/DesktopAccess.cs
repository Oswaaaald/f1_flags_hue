using System.Security.Cryptography;
using System.Text;

namespace F1Hue.Infrastructure;

/// <summary>The launcher proves local ownership, then receives a short-lived, single-use browser ticket.</summary>
public sealed class DesktopAccess : IDisposable
{
    public const string FileName = "desktop-launch.key";
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, DateTimeOffset> _tickets = [];
    public DesktopAccess(string directory, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _path = Path.Combine(directory, FileName);
        PrivateFiles.Write(_path, _secret);
    }
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public string CreateTicket(string? launcherSecret)
    {
        if (launcherSecret is not { Length: 64 } || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(launcherSecret), Encoding.UTF8.GetBytes(_secret)))
            throw new UnauthorizedAccessException("Ouvre l’interface depuis le menu de l’application F1 Hue Sync.");
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            foreach (var token in _tickets.Where(t => t.Value <= now).Select(t => t.Key).ToArray()) _tickets.Remove(token);
            if (_tickets.Count >= 20) throw new InvalidOperationException("Trop d’ouvertures en attente. Réessaie dans une minute.");
            var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _tickets.Add(Hash(ticket), now.AddMinutes(1));
            return ticket;
        }
    }
    public void ConsumeTicket(string ticket)
    {
        lock (_gate)
        {
            if (ticket.Length != 64 || !_tickets.Remove(Hash(ticket), out var expires) || expires <= _clock.GetUtcNow())
                throw new UnauthorizedAccessException("Cette ouverture a expiré. Rouvre l’interface depuis le menu F1 Hue Sync.");
        }
    }
    public void Dispose()
    {
        lock (_gate) _tickets.Clear();
        if (File.Exists(_path) && File.ReadAllText(_path) == _secret) File.Delete(_path);
    }
}
