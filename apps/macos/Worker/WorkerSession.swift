import Darwin
import Foundation

@MainActor
final class WorkerSession {
    private static let maximumPendingRequests = 64
    private static let maximumRequestBytes = 16_000
    private static let maximumResponseBytes = 2_000_000

    private let writeQueue = DispatchQueue(label: "dev.praxis.worker.stdin")
    private let decoder = JSONDecoder()
    private var process: Process?
    private var input: FileHandle?
    private var pending: [String: (WorkerResponse) -> Void] = [:]
    private var buffer = Data()
    private var failure: String?
    private var onDiagnostic: ((String) -> Void)?
    private var onExit: ((String?) -> Void)?

    func start(
        executable: URL,
        arguments: [String],
        onDiagnostic: @escaping (String) -> Void,
        onExit: @escaping (String?) -> Void
    ) throws {
        guard process == nil else { throw SessionError.alreadyRunning }

        let child = Process()
        let stdin = Pipe()
        let stdout = Pipe()
        let stderr = Pipe()
        let writer = stdin.fileHandleForWriting
        guard fcntl(writer.fileDescriptor, F_SETNOSIGPIPE, 1) != -1 else {
            throw SessionError.pipeSetup
        }

        child.executableURL = executable
        child.arguments = arguments
        child.standardInput = stdin
        child.standardOutput = stdout
        child.standardError = stderr

        stdout.fileHandleForReading.readabilityHandler = { [weak self, weak child] handle in
            let bytes = handle.availableData
            if bytes.isEmpty {
                handle.readabilityHandler = nil
                return
            }
            DispatchQueue.main.async {
                guard let self, let child, self.process === child else { return }
                self.receive(bytes, from: child)
            }
        }
        stderr.fileHandleForReading.readabilityHandler = { [weak self, weak child] handle in
            let bytes = handle.availableData
            if bytes.isEmpty {
                handle.readabilityHandler = nil
                return
            }
            let diagnostic = String(decoding: bytes.prefix(2048), as: UTF8.self)
            DispatchQueue.main.async {
                guard let self, let child, self.process === child else { return }
                self.onDiagnostic?(diagnostic)
            }
        }
        child.terminationHandler = { [weak self] child in
            let status = child.terminationStatus
            DispatchQueue.main.async {
                let message = status == 0 ? nil
                    : "The worker exited (\(status)). Check the private catalog."
                self?.finish(child, error: message)
            }
        }

        do {
            try child.run()
        } catch {
            stdout.fileHandleForReading.readabilityHandler = nil
            stderr.fileHandleForReading.readabilityHandler = nil
            child.terminationHandler = nil
            throw error
        }
        process = child
        input = writer
        self.onDiagnostic = onDiagnostic
        self.onExit = onExit
    }

    func send(
        _ action: WorkerAction, fields: [String: Any], receive: @escaping (WorkerResponse) -> Void
    ) throws {
        guard let process, let input else { throw SessionError.notRunning }
        guard pending.count < Self.maximumPendingRequests else { throw SessionError.busy }

        let id = UUID().uuidString
        var request = fields
        request["requestId"] = id
        request["action"] = action.rawValue
        guard var frame = try? JSONSerialization.data(withJSONObject: request),
              frame.count < Self.maximumRequestBytes else {
            throw SessionError.requestTooLarge
        }
        frame.append(0x0A)
        pending[id] = receive

        writeQueue.async { [weak self, weak process, frame] in
            do {
                try input.write(contentsOf: frame)
            } catch {
                DispatchQueue.main.async {
                    guard let self, let process else { return }
                    self.fail(process, message: "The worker is no longer accepting requests.")
                }
            }
        }
    }

    func stop() {
        guard let input else { return }
        self.input = nil
        writeQueue.async { try? input.close() }
    }

    private func receive(_ bytes: Data, from child: Process) {
        guard failure == nil else { return }
        buffer.append(bytes)
        while let end = buffer.firstIndex(of: 0x0A) {
            guard buffer.distance(from: buffer.startIndex, to: end) <= Self.maximumResponseBytes else {
                fail(child, message: "A worker response exceeded the size limit.")
                return
            }

            let line = Data(buffer[..<end])
            buffer.removeSubrange(...end)
            guard let response = try? decoder.decode(WorkerResponse.self, from: line),
                  let id = response.requestId,
                  let complete = pending.removeValue(forKey: id) else {
                fail(child, message: "The worker returned an invalid response.")
                return
            }
            complete(response)
        }
        if buffer.count > Self.maximumResponseBytes {
            fail(child, message: "A worker response exceeded the size limit.")
        }
    }

    private func fail(_ child: Process, message: String) {
        guard process === child, failure == nil else { return }
        failure = message
        stop()
        if child.isRunning { child.terminate() }
    }

    private func finish(_ child: Process, error: String?) {
        guard process === child else { return }
        stop()
        process = nil
        pending.removeAll()
        buffer.removeAll()
        let notify = onExit
        let exitError = failure ?? error
        failure = nil
        onDiagnostic = nil
        onExit = nil
        notify?(exitError)
    }

    private enum SessionError: LocalizedError {
        case alreadyRunning, notRunning, pipeSetup, busy, requestTooLarge

        var errorDescription: String? {
            switch self {
            case .alreadyRunning: "Praxis is still closing the previous worker."
            case .notRunning: "The worker is no longer accepting requests."
            case .pipeSetup: "Praxis could not prepare the worker connection."
            case .busy: "The worker is busy. Retry after pending requests complete."
            case .requestTooLarge: "The request exceeds the private protocol limit."
            }
        }
    }
}
