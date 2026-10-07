using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

ApplicationConfiguration.Initialize();
using var mutex = new Mutex(true, "Local\\F1HueDesktop", out var first);
if (!first) { Process.Start(new ProcessStartInfo("http://127.0.0.1:8081") { UseShellExecute = true }); return; }
Application.Run(new TrayApplication(args.Contains("--background")));

sealed class TrayApplication : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly string _data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "F1Hue");
    private readonly string _url = "http://127.0.0.1:8081";
    private readonly ToolStripMenuItem _login;
    private Process? _service;
    private StreamWriter? _log;
    private readonly object _logGate = new();
    private readonly System.Windows.Forms.Timer _supervisor = new() { Interval = 1000 };
    private readonly Queue<DateTimeOffset> _restarts = new();
    private DateTimeOffset? _restartAt;
    private bool _stopping;
    public TrayApplication(bool background)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Ouvrir F1 Hue Sync", null, (_, _) => Open());
        menu.Items.Add("Ouvrir les journaux", null, (_, _) => Process.Start(new ProcessStartInfo("notepad.exe", Path.Combine(_data, "service.log")) { UseShellExecute = true }));
        menu.Items.Add("Vérifier les mises à jour", null, (_, _) => Updates());
        menu.Items.Add(new ToolStripSeparator());
        _login = new ToolStripMenuItem("Lancer à l’ouverture de session") { Checked = IsAutoStart() };
        _login.Click += (_, _) => ToggleLogin(); menu.Items.Add(_login);
        menu.Items.Add("Importer un config.yml…", null, (_, _) => Import());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Arrêter F1 Hue Sync et quitter", null, (_, _) => ExitThread());
        _tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "F1 Hue Sync", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => Open();
        try { Start(); if (!background) _ = WaitAndOpen(); }
        catch (Exception e) { MessageBox.Show(e.Message, "F1 Hue Sync"); }
        _supervisor.Tick += (_, _) => Supervise();
        _supervisor.Start();
    }
    private void Start(string? import = null)
    {
        if (_service is { HasExited: false }) throw new InvalidOperationException("Le service précédent n’est pas encore arrêté.");
        _stopping = false;
        Directory.CreateDirectory(_data);
        var logFile = Path.Combine(_data, "service.log");
        if (File.Exists(logFile) && new FileInfo(logFile).Length > 2_000_000) File.Delete(logFile);
        _log = new StreamWriter(new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "service", "f1-hue.exe")) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "--data", _data, "--port", "8081" }) info.ArgumentList.Add(argument);
        if (import is not null) { info.ArgumentList.Add("--import"); info.ArgumentList.Add(import); }
        _service = new Process { StartInfo = info };
        _service.OutputDataReceived += (_, e) => Log(e.Data); _service.ErrorDataReceived += (_, e) => Log(e.Data);
        _service.Start(); _service.BeginOutputReadLine(); _service.BeginErrorReadLine();
    }
    private void Log(string? line) { if (line is not null) lock (_logGate) _log?.WriteLine(line); }
    private void Supervise()
    {
        if (_stopping || _service is not { HasExited: true } child || child.ExitCode is 0 or 2) return;
        var now = DateTimeOffset.UtcNow;
        if (_restartAt is null)
        {
            while (_restarts.TryPeek(out var at) && now - at > TimeSpan.FromMinutes(1)) _restarts.Dequeue();
            if (_restarts.Count >= 3) { _stopping = true; _tray.ShowBalloonTip(5000, "F1 Hue Sync", "Le service s’arrête à répétition. Consulte les journaux avant de relancer l’application.", ToolTipIcon.Error); return; }
            _restarts.Enqueue(now); _restartAt = now.AddSeconds(3); return;
        }
        if (now < _restartAt) return;
        _restartAt = null; child.Dispose(); _service = null;
        lock (_logGate) { _log?.Dispose(); _log = null; }
        try { Start(); }
        catch (Exception e) { _stopping = true; MessageBox.Show(e.Message, "F1 Hue Sync"); }
    }
    private async Task WaitAndOpen()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        for (var i = 0; i < 20; i++)
        {
            try { var response = await http.GetStringAsync(_url + "/health"); if (JsonDocument.Parse(response).RootElement.GetProperty("status").GetString() == "ok") { Open(); return; } }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
            await Task.Delay(500);
        }
        MessageBox.Show("Le service n’a pas démarré. Consulte les journaux depuis le menu F1 Hue.", "F1 Hue Sync");
    }
    private void Open()
    {
        var setup = Path.Combine(_data, "setup-code.txt");
        Process.Start(new ProcessStartInfo(_url + (File.Exists(setup) ? "/#setup=" + Uri.EscapeDataString(File.ReadAllText(setup).Trim()) : "")) { UseShellExecute = true });
    }
    private static bool IsAutoStart() { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("F1Hue") is not null; }
    private void ToggleLogin()
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (IsAutoStart()) key.DeleteValue("F1Hue", false); else key.SetValue("F1Hue", "\"" + Application.ExecutablePath + "\" --background");
        _login.Checked = IsAutoStart();
    }
    private void Updates()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "releases-url.txt");
        if (File.Exists(path) && Uri.TryCreate(File.ReadAllText(path).Trim(), UriKind.Absolute, out var uri) && uri.Scheme == "https") Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        else MessageBox.Show("Consulte la section Releases du dépôt du projet.", "F1 Hue Sync");
    }
    private bool Stop()
    {
        _stopping = true; _restartAt = null;
        if (_service is { HasExited: false })
        {
            // Private marker: the host performs normal cancellation and lamp restoration.
            File.WriteAllText(Path.Combine(_data, "stop.request"), "stop");
            if (!_service.WaitForExit(45000))
            {
                MessageBox.Show("Le service termine encore l’arrêt des lampes. Réessaie de quitter dans quelques instants. Aucun autre service ne sera lancé entre-temps.", "F1 Hue Sync");
                return false;
            }
        }
        _service?.Dispose(); _service = null; lock (_logGate) { _log?.Dispose(); _log = null; }
        return true;
    }
    private void Import()
    {
        using var picker = new OpenFileDialog { Filter = "Configuration YAML|*.yml;*.yaml", Title = "Importer les réglages de la version Python" };
        if (picker.ShowDialog() == DialogResult.OK && Stop()) { Start(picker.FileName); _ = WaitAndOpen(); }
    }
    protected override void ExitThreadCore() { if (!Stop()) return; _supervisor.Dispose(); _tray.Visible = false; _tray.Dispose(); base.ExitThreadCore(); }
}
