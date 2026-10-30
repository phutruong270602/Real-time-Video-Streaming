import Foundation
import OSLog

enum AppLogger {
    private static let subsystem = Bundle.main.bundleIdentifier ?? "iPhoneVideoSender"
    private static let fileName = "iPhoneVideoSender.log"
    private static let fileLock = NSLock()

    static func info(_ message: String, category: String) {
        write(message, category: category, level: .info)
    }

    static func warning(_ message: String, category: String) {
        write(message, category: category, level: .warning)
    }

    static func error(_ message: String, category: String) {
        write(message, category: category, level: .error)
    }

    static func debug(_ message: String, category: String) {
        write(message, category: category, level: .debug)
    }

    static func recentLines(limit: Int = 60) -> [String] {
        guard limit > 0 else { return [] }

        fileLock.lock()
        defer { fileLock.unlock() }

        do {
            let fileURL = try FileManager.default.url(
                for: .documentDirectory,
                in: .userDomainMask,
                appropriateFor: nil,
                create: true
            ).appendingPathComponent(fileName)
            guard FileManager.default.fileExists(atPath: fileURL.path) else { return [] }

            let file = try FileHandle(forReadingFrom: fileURL)
            defer { try? file.close() }
            let fileSize = try file.seekToEnd()
            try file.seek(toOffset: fileSize > 16_384 ? fileSize - 16_384 : 0)
            let data = try file.readToEnd() ?? Data()
            return String(decoding: data, as: UTF8.self)
                .split(whereSeparator: \.isNewline)
                .suffix(limit)
                .map(String.init)
        } catch {
            Logger(subsystem: subsystem, category: "Logging")
                .error("Could not read log file: \(error.localizedDescription, privacy: .public)")
            return []
        }
    }

    private static func write(_ message: String, category: String, level: LogLevel) {
        let logger = Logger(subsystem: subsystem, category: category)
        switch level {
        case .debug:
            logger.debug("\(message, privacy: .public)")
        case .info:
            logger.info("\(message, privacy: .public)")
        case .warning:
            logger.warning("\(message, privacy: .public)")
        case .error:
            logger.error("\(message, privacy: .public)")
        }

        let timestamp = ISO8601DateFormatter().string(from: Date())
        let line = "\(timestamp) [\(level.label)] [\(category)] \(message)\n"
        appendToFile(line, logger: logger)
    }

    private static func appendToFile(_ line: String, logger: Logger) {
        fileLock.lock()
        defer { fileLock.unlock() }

        do {
            let fileURL = try FileManager.default.url(
                for: .documentDirectory,
                in: .userDomainMask,
                appropriateFor: nil,
                create: true
            ).appendingPathComponent(fileName)
            if !FileManager.default.fileExists(atPath: fileURL.path) {
                FileManager.default.createFile(atPath: fileURL.path, contents: nil)
            }

            let file = try FileHandle(forWritingTo: fileURL)
            defer { try? file.close() }
            try file.seekToEnd()
            try file.write(contentsOf: Data(line.utf8))
        } catch {
            logger.error("Could not write log file: \(error.localizedDescription, privacy: .public)")
        }
    }
}

private enum LogLevel {
    case debug
    case info
    case warning
    case error

    var label: String {
        switch self {
        case .debug: "DEBUG"
        case .info: "INFO"
        case .warning: "WARNING"
        case .error: "ERROR"
        }
    }
}
