import Foundation

// A pipe decouples the service descriptor from the current log file, so
// rotation also works during a long-running service, not only at startup.
final class RotatingLog: @unchecked Sendable {
    private let queue = DispatchQueue(label: "local.f1hue.logs")
    private let path: URL
    private var handle: FileHandle?
    private var size: Int = 0
    init(directory: URL) throws {
        path = directory.appendingPathComponent("service.log")
        try open()
    }
    private func open() throws {
        if !FileManager.default.fileExists(atPath: path.path) {
            FileManager.default.createFile(atPath: path.path, contents: nil, attributes: [.posixPermissions: 0o600])
        }
        handle = try FileHandle(forWritingTo: path)
        size = Int(try handle!.seekToEnd())
    }
    func append(_ data: Data) {
        queue.async { [self] in
            do {
                guard handle != nil else { return }
                if size + data.count > 2_000_000 {
                    try handle?.close(); handle = nil
                    try? FileManager.default.removeItem(atPath: path.path + ".3")
                    for index in [2, 1] where FileManager.default.fileExists(atPath: path.path + ".\(index)") {
                        try FileManager.default.moveItem(atPath: path.path + ".\(index)", toPath: path.path + ".\(index + 1)")
                    }
                    try FileManager.default.moveItem(atPath: path.path, toPath: path.path + ".1")
                    try open()
                }
                try handle?.write(contentsOf: data); size += data.count
            } catch {
                // Logging failures must not terminate the owner of the lamps.
                try? open()
            }
        }
    }
    func close() { queue.sync { try? handle?.close(); handle = nil } }
}
