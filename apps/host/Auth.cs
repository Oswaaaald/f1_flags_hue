using System.Security.Cryptography;
using System.Text;
using F1Hue.Infrastructure;

namespace F1Hue.Host;

public sealed record PasswordRecord(string Salt, string Hash);
public sealed record SessionRecord(string Hash, DateTimeOffset Expires);
public sealed class Auth(Store store)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _attempts = [];
    public bool SetupRequired => store.Get<PasswordRecord>("password") is null;
    public string SetupFile => Path.Combine(store.DirectoryPath, "setup-code.txt");
    public void EnsureSetupCode()
    {
        if (SetupRequired && !File.Exists(SetupFile)) PrivateFiles.Write(SetupFile, Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
    }
    private static bool Equal(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    public void Limit(string ip)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in _attempts.Where(p => p.Value.All(at => now - at > TimeSpan.FromMinutes(1))).Select(p => p.Key).ToArray()) _attempts.Remove(key);
            if (_attempts.Count >= 1000) throw new InvalidOperationException("Trop de tentatives. Attends une minute.");
            if (!_attempts.TryGetValue(ip, out var attempts)) _attempts[ip] = attempts = new();
            while (attempts.TryPeek(out var at) && now - at > TimeSpan.FromMinutes(1)) attempts.Dequeue();
            if (attempts.Count >= 5) throw new InvalidOperationException("Trop de tentatives. Attends une minute.");
            attempts.Enqueue(now);
        }
    }
    private static byte[] Hash(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(password, salt, 600000, HashAlgorithmName.SHA256, 32);
    public void Setup(string code, string password)
    {
        lock (_gate)
        {
        if (!SetupRequired || !File.Exists(SetupFile) || !Equal(code, File.ReadAllText(SetupFile).Trim())) throw new UnauthorizedAccessException("Code de configuration invalide.");
        if (password.Length is < 12 or > 256) throw new ArgumentException("Choisis un mot de passe de 12 à 256 caractères.");
        var salt = RandomNumberGenerator.GetBytes(16);
        store.Put("password", new PasswordRecord(Convert.ToBase64String(salt), Convert.ToBase64String(Hash(password, salt)))); File.Delete(SetupFile);
        }
    }
    public void Login(string password)
    {
        var record = store.Get<PasswordRecord>("password");
        if (record is null || password.Length > 256 || !CryptographicOperations.FixedTimeEquals(Hash(password, Convert.FromBase64String(record.Salt)), Convert.FromBase64String(record.Hash)))
            throw new UnauthorizedAccessException("Mot de passe incorrect.");
    }
    private static string TokenHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public string CreateSession()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        lock (_gate)
        {
            var sessions = (store.Get<SessionRecord[]>("sessions") ?? []).Where(s => s.Expires > DateTimeOffset.UtcNow).TakeLast(19).ToList();
            sessions.Add(new(TokenHash(token), DateTimeOffset.UtcNow.AddDays(30))); store.Put("sessions", sessions);
        }
        return token;
    }
    public bool Valid(string? token) => token is { Length: 64 } && (store.Get<SessionRecord[]>("sessions") ?? []).Any(s => s.Expires > DateTimeOffset.UtcNow && Equal(s.Hash, TokenHash(token)));
    public void Logout(string? token) { if (token is null) return; lock (_gate) store.Put("sessions", (store.Get<SessionRecord[]>("sessions") ?? []).Where(s => !Equal(s.Hash, TokenHash(token))).ToArray()); }
    public void Reset() { store.Delete("password"); store.Delete("sessions"); EnsureSetupCode(); }
}
