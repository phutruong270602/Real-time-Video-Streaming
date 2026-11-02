import Foundation
import Combine

final class WebSocketManager: NSObject, ObservableObject, @unchecked Sendable {
    @Published private(set) var state: SignalingState = .disconnected

    var onMessage: ((SignalingMessage) -> Void)?

    private lazy var urlSession = URLSession(
        configuration: .default,
        delegate: self,
        delegateQueue: OperationQueue.main
    )
    private var webSocketTask: URLSessionWebSocketTask?

    func connect(to url: URL) {
        AppLogger.info("Connecting to signaling server", category: "WebSocket")
        disconnect()
        state = .connecting
        let task = urlSession.webSocketTask(with: url)
        webSocketTask = task
        task.resume()
    }

    func disconnect() {
        if webSocketTask != nil {
            AppLogger.info("Disconnecting from signaling server", category: "WebSocket")
        }
        webSocketTask?.cancel(with: .normalClosure, reason: nil)
        webSocketTask = nil
        state = .disconnected
    }

    func send(_ message: SignalingMessage) {
        guard let webSocketTask, state == .connected else {
            fail("Cannot send signaling message: WebSocket is not connected.")
            return
        }

        do {
            let data = try JSONEncoder().encode(message)
            guard let text = String(data: data, encoding: .utf8) else {
                fail("Could not encode the signaling message as UTF-8.")
                return
            }
            webSocketTask.send(.string(text)) { [weak self] error in
                guard let error else { return }
                DispatchQueue.main.async {
                    self?.fail("WebSocket send failed: \(error.localizedDescription)")
                }
            }
        } catch {
            fail("Could not encode signaling message: \(error.localizedDescription)")
        }
    }

    private func receiveNextMessage(from task: URLSessionWebSocketTask) {
        task.receive { [weak self, weak task] result in
            guard let self, let task, self.webSocketTask === task else { return }

            switch result {
            case .success(let message):
                do {
                    let data: Data
                    switch message {
                    case .string(let text):
                        data = Data(text.utf8)
                    case .data(let binary):
                        data = binary
                    @unknown default:
                        throw WebSocketError.unsupportedMessage
                    }
                    let decoded = try JSONDecoder().decode(SignalingMessage.self, from: data)
                    self.onMessage?(decoded)
                    self.receiveNextMessage(from: task)
                } catch {
                    self.fail("Invalid signaling message: \(error.localizedDescription)")
                }
            case .failure(let error):
                self.fail("WebSocket receive failed: \(error.localizedDescription)")
            }
        }
    }

    private func fail(_ reason: String) {
        AppLogger.error(reason, category: "WebSocket")
        webSocketTask?.cancel(with: .goingAway, reason: nil)
        webSocketTask = nil
        state = .failed(reason)
    }
}

extension WebSocketManager: URLSessionWebSocketDelegate {
    func urlSession(
        _ session: URLSession,
        webSocketTask: URLSessionWebSocketTask,
        didOpenWithProtocol protocol: String?
    ) {
        guard self.webSocketTask === webSocketTask else { return }
        AppLogger.info("Signaling WebSocket connected", category: "WebSocket")
        state = .connected
        receiveNextMessage(from: webSocketTask)
    }

    func urlSession(
        _ session: URLSession,
        webSocketTask: URLSessionWebSocketTask,
        didCloseWith closeCode: URLSessionWebSocketTask.CloseCode,
        reason: Data?
    ) {
        guard self.webSocketTask === webSocketTask else { return }
        AppLogger.info("Signaling WebSocket closed", category: "WebSocket")
        self.webSocketTask = nil
        state = .disconnected
    }
}

private enum WebSocketError: LocalizedError {
    case unsupportedMessage

    var errorDescription: String? {
        "The receiver sent an unsupported WebSocket message."
    }
}
