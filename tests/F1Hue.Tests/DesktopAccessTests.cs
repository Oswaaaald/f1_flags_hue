using F1Hue.Infrastructure;

internal static class DesktopAccessTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static bool Rejected(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException) { return true; }
    }
    public static async Task Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "f1hue-desktop-" + Guid.NewGuid());
        var file = Path.Combine(directory, DesktopAccess.FileName);
        var clock = new Clock();
        string secret;
        string oldTicket;
        try
        {
            using (var access = new DesktopAccess(directory, clock))
            {
                secret = File.ReadAllText(file);
                check(secret.Length == 64 && (OperatingSystem.IsWindows() || File.GetUnixFileMode(file) == (UnixFileMode.UserRead | UnixFileMode.UserWrite)), "Launcher proof is stored privately, outside browser state");
                check(Rejected(() => access.CreateTicket(null)) && Rejected(() => access.CreateTicket(new string('0', 64))), "Desktop tickets require the current launcher's secret");
                var ticket = access.CreateTicket(secret);
                access.ConsumeTicket(ticket);
                check(Rejected(() => access.ConsumeTicket(ticket)), "A browser ticket can be consumed only once");
                var expired = access.CreateTicket(secret);
                clock.Now = clock.Now.AddMinutes(1);
                check(Rejected(() => access.ConsumeTicket(expired)), "Browser tickets expire after one minute, including the boundary");
                var shared = access.CreateTicket(secret);
                var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => !Rejected(() => access.ConsumeTicket(shared)))));
                check(outcomes.Count(accepted => accepted) == 1, "Concurrent exchanges cannot replay the same browser ticket");
                oldTicket = access.CreateTicket(secret);
                for (var i = 1; i < 20; i++)
                    access.CreateTicket(secret);
                check(Rejected(() => access.CreateTicket(secret)), "Outstanding desktop tickets are bounded");
                clock.Now = clock.Now.AddMinutes(1);
                check(access.CreateTicket(secret).Length == 64, "Expired tickets release their capacity");
            }
            check(!File.Exists(file), "Stopping the desktop service removes its launcher proof");
            using (var restarted = new DesktopAccess(directory, clock))
                check(File.ReadAllText(file) != secret && Rejected(() => restarted.CreateTicket(secret)) && Rejected(() => restarted.ConsumeTicket(oldTicket)), "Restart rotates the launcher secret and invalidates previous tickets");
        }
        finally { Directory.Delete(directory, true); }
    }
}
