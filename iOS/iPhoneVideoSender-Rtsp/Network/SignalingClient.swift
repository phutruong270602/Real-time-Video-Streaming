import Foundation
import Combine

final class SignalingClient: ObservableObject {
    @Published private(set) var state: SignalingState = .disconnected

    var onMessage: ((SignalingMessage) -> Void)?

    private let webSocket = WebSocketManager()
    private var stateObservation: AnyCancellable?

    init() {
        stateObservation = webSocket.$state
            .receive(on: DispatchQueue.main)
            .sink { [weak self] state in
                self?.state = state
            }
        webSocket.onMessage = { [weak self] message in
            self?.onMessage?(message)
        }
    }

    func connect(to url: URL) {
        webSocket.connect(to: url)
    }

    func disconnect() {
        webSocket.disconnect()
    }

    func send(_ message: SignalingMessage) {
        webSocket.send(message)
    }

    func sendCapabilities(
        cameraCapabilities: [CameraPosition: [CameraFormatCapability]],
        cameras: [CameraPosition]
    ) {
        var mergedModes: [String: (resolution: VideoResolution, camera: CameraPosition, fps: Set<Int>)] = [:]
        for (camera, capabilities) in cameraCapabilities {
            for capability in capabilities {
                for fps in StreamingConfiguration.supportedFrameRates
                    where capability.supports(frameRate: fps) {
                    let key = "\(capability.resolution.rawValue)|\(camera.rawValue)"
                    var mode = mergedModes[key] ?? (capability.resolution, camera, [])
                    mode.fps.insert(fps)
                    mergedModes[key] = mode
                }
            }
        }

        let resolutions = mergedModes.values
            .map { mode in
                ResolutionCapability(
                    width: Int(mode.resolution.width),
                    height: Int(mode.resolution.height),
                    fps: mode.fps.sorted(),
                    cameras: [mode.camera.rawValue]
                )
            }
            .sorted {
                if $0.width == $1.width {
                    return $0.cameras[0] < $1.cameras[0]
                }
                return $0.width < $1.width
            }

        send(SignalingMessage(
            type: .capabilities,
            resolutions: resolutions,
            cameras: cameras.map(\.rawValue)
        ))
    }
}
