import Cocoa

final class LocalRequestDelegate: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}

// This menu application owns exactly one local service. Closing the browser does not stop it.
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var item: NSStatusItem!
    private var process: Process?
    private var logHandle: FileHandle?
    private var loginItem: NSMenuItem!
    private var lastError: String?
    private var stopping = false
    private var restartTimes: [Date] = []
    private var restartWork: DispatchWorkItem?
    private lazy var localSession: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.connectionProxyDictionary = [:]
        return URLSession(configuration: configuration, delegate: LocalRequestDelegate(), delegateQueue: nil)
    }()
    private let port = 8081
    private let appPath = Bundle.main.bundlePath
    private var dataPath: URL { FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/F1Hue") }
    private var agentPath: URL { FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/LaunchAgents/local.f1hue.desktop.plist") }
    private var serverURL: URL { URL(string: "http://127.0.0.1:\(port)")! }

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        if NSRunningApplication.runningApplications(withBundleIdentifier: "local.f1hue.desktop").count > 1 { openAuthenticatedInterface(terminateAfter: true); return }
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.image = NSImage(systemSymbolName: "flag.checkered", accessibilityDescription: "F1 Hue Sync")
        let menu = NSMenu()
        menu.addItem(withTitle: "Ouvrir F1 Hue Sync", action: #selector(openInterface), keyEquivalent: "o").target = self
        menu.addItem(withTitle: "Ouvrir les journaux", action: #selector(openLogs), keyEquivalent: "").target = self
        menu.addItem(withTitle: "Vérifier les mises à jour", action: #selector(updates), keyEquivalent: "").target = self
        menu.addItem(NSMenuItem.separator())
        loginItem = menu.addItem(withTitle: "Lancer à l’ouverture de session", action: #selector(toggleLogin), keyEquivalent: "")
        loginItem.target = self; loginItem.state = FileManager.default.fileExists(atPath: agentPath.path) ? .on : .off
        menu.addItem(withTitle: "Importer un config.yml…", action: #selector(importConfig), keyEquivalent: "").target = self
        menu.addItem(NSMenuItem.separator())
        menu.addItem(withTitle: "Arrêter F1 Hue Sync et quitter", action: #selector(quit), keyEquivalent: "q").target = self
        item.menu = menu
        startService(importPath: nil)
        if !CommandLine.arguments.contains("--background") { waitAndOpen(attempt: 0) }
    }
    private func startService(importPath: String?) {
        guard process?.isRunning != true else { return }
        stopping = false
        lastError = nil
        do {
            try? logHandle?.close()
            try FileManager.default.createDirectory(at: dataPath, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            let log = dataPath.appendingPathComponent("service.log")
            if let size = try? log.resourceValues(forKeys: [.fileSizeKey]).fileSize, size > 2_000_000 { try? FileManager.default.removeItem(at: log) }
            if !FileManager.default.fileExists(atPath: log.path) { FileManager.default.createFile(atPath: log.path, contents: nil, attributes: [.posixPermissions: 0o600]) }
            logHandle = try FileHandle(forWritingTo: log); try logHandle?.seekToEnd()
            let child = Process()
            child.executableURL = Bundle.main.resourceURL!.appendingPathComponent("service/f1-hue")
            child.arguments = ["--desktop", "--listen", "127.0.0.1", "--data", dataPath.path, "--port", String(port)]
            let oldConfig = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("f1_flags_hue/config.yml").path
            if let path = importPath ?? (FileManager.default.fileExists(atPath: oldConfig) ? oldConfig : nil) { child.arguments! += ["--import", path] }
            child.standardOutput = logHandle; child.standardError = logHandle
            child.terminationHandler = { [weak self] p in
                DispatchQueue.main.async {
                    guard let self = self, self.process === p, !self.stopping else { return }
                    if p.terminationStatus == 0 { return }
                    self.lastError = "Le service s’est arrêté. Consulte les journaux depuis le menu F1 Hue."
                    // Exit 2 is an occupied profile: restarting cannot resolve it.
                    if p.terminationStatus == 2 { return }
                    self.restartTimes.removeAll { Date().timeIntervalSince($0) > 60 }
                    guard self.restartTimes.count < 3 else { return }
                    self.restartTimes.append(Date())
                    let work = DispatchWorkItem { [weak self] in
                        guard let self = self, !self.stopping else { return }
                        self.startService(importPath: nil)
                    }
                    self.restartWork = work
                    DispatchQueue.main.asyncAfter(deadline: .now() + 3, execute: work)
                }
            }
            try child.run(); process = child
        } catch { lastError = error.localizedDescription; showError(lastError!) }
    }
    private func waitAndOpen(attempt: Int) {
        var request = URLRequest(url: serverURL.appendingPathComponent("health")); request.timeoutInterval = 1
        URLSession.shared.dataTask(with: request) { [weak self] data, _, _ in
            DispatchQueue.main.async {
                guard let self = self else { return }
                if let data = data, let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any], json["status"] as? String == "ok" { self.openInterface(); return }
                if attempt < 20 && self.lastError == nil { DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { self.waitAndOpen(attempt: attempt + 1) } }
                else { self.showError(self.lastError ?? "Le service met trop de temps à démarrer. Consulte les journaux.") }
            }
        }.resume()
    }
    @objc private func openInterface() {
        openAuthenticatedInterface()
    }
    private func openAuthenticatedInterface(terminateAfter: Bool = false) {
        guard let secret = try? String(contentsOf: dataPath.appendingPathComponent("desktop-launch.key"), encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines), secret.count == 64 else {
            showError("Le service n’est pas prêt. Réessaie d’ouvrir l’interface depuis le menu F1 Hue Sync.")
            if terminateAfter { NSApp.terminate(nil) }
            return
        }
        var request = URLRequest(url: serverURL.appendingPathComponent("api/auth/desktop/ticket"))
        request.httpMethod = "POST"; request.timeoutInterval = 5
        request.setValue("1", forHTTPHeaderField: "X-F1Hue-Request")
        request.setValue(secret, forHTTPHeaderField: "X-F1Hue-Launcher")
        localSession.dataTask(with: request) { [weak self] data, response, _ in
            DispatchQueue.main.async {
                guard let self = self else { return }
                defer { if terminateAfter { NSApp.terminate(nil) } }
                guard (response as? HTTPURLResponse)?.statusCode == 200, let data = data,
                      let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                      let ticket = json["ticket"] as? String, ticket.count == 64 else {
                    self.showError("Impossible d’ouvrir l’interface. Vérifie que le service F1 Hue Sync est lancé puis réessaie.")
                    return
                }
                var components = URLComponents(url: self.serverURL, resolvingAgainstBaseURL: false)!
                components.fragment = "desktop=" + ticket
                if let url = components.url { NSWorkspace.shared.open(url) }
            }
        }.resume()
    }
    @objc private func openLogs() { NSWorkspace.shared.open(dataPath.appendingPathComponent("service.log")) }
    @objc private func updates() {
        if let urlString = Bundle.main.object(forInfoDictionaryKey: "F1HueReleasesURL") as? String, let url = URL(string: urlString), url.scheme == "https" { NSWorkspace.shared.open(url) }
        else { showError("Les mises à jour sont disponibles dans la section Releases du dépôt du projet.") }
    }
    @objc private func toggleLogin() {
        do {
            if FileManager.default.fileExists(atPath: agentPath.path) { try FileManager.default.removeItem(at: agentPath); loginItem.state = .off }
            else {
                try FileManager.default.createDirectory(at: agentPath.deletingLastPathComponent(), withIntermediateDirectories: true)
                let executable = Bundle.main.executablePath!
                let plist: [String: Any] = ["Label": "local.f1hue.desktop", "ProgramArguments": [executable, "--background"], "RunAtLoad": true, "ProcessType": "Interactive"]
                try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0).write(to: agentPath, options: .atomic)
                loginItem.state = .on
            }
        } catch { showError(error.localizedDescription) }
    }
    @objc private func importConfig() {
        let picker = NSOpenPanel(); picker.canChooseDirectories = false; picker.allowsMultipleSelection = false; picker.title = "Importer les réglages de la version Python"
        NSApp.activate(ignoringOtherApps: true)
        if picker.runModal() == .OK, let file = picker.url, stopService() { startService(importPath: file.path); waitAndOpen(attempt: 0) }
    }
    @discardableResult private func stopService() -> Bool {
        stopping = true
        restartWork?.cancel(); restartWork = nil
        if let child = process, child.isRunning {
            child.terminate()
            let deadline = Date().addingTimeInterval(45)
            while child.isRunning && Date() < deadline { Thread.sleep(forTimeInterval: 0.05) }
            if child.isRunning {
                showError("Le service termine encore l’arrêt des lampes. Réessaie de quitter dans quelques instants. Aucun autre service ne sera lancé entre-temps.")
                return false
            }
            child.waitUntilExit()
        }
        process = nil; try? logHandle?.close(); logHandle = nil
        return true
    }
    @objc private func quit() { if stopService() { NSApp.terminate(nil) } }
    func applicationWillTerminate(_ notification: Notification) { stopService() }
    private func showError(_ message: String) { let alert = NSAlert(); alert.messageText = "F1 Hue Sync"; alert.informativeText = message; alert.runModal() }
}
let application = NSApplication.shared
let delegate = AppDelegate()
application.delegate = delegate
application.run()
