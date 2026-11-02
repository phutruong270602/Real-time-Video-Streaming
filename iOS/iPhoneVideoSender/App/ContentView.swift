import SwiftUI
import UIKit

struct ContentView: View {
    @StateObject private var camera = CameraManager()
    @StateObject private var discovery = BonjourDiscoveryClient()
    @StateObject private var signaling = SignalingClient()
    @StateObject private var webRTC = WebRTCManager()
    @State private var manualHost = ""
    @State private var manualPort = "8080"
    @State private var connectionError: String?
    @State private var bitrate = StreamingConfiguration.defaultBitrate
    @State private var streamPreparationTask: Task<Void, Error>?
    @State private var isLogsExpanded = false
    @State private var logLines: [String] = []
    @State private var lastPerformanceLogDate = Date.distantPast

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: 20) {
                    HStack {
                        Text("Version \(appVersion)")
                        Spacer()
                    }
                    .font(.caption)
                    .foregroundStyle(.secondary)

                    preview
                    cameraStatus
                    cameraControls
                    captureStatistics
                    senderStatistics
                    connectionControls
                    streamingControls
                    logsPanel
                }
                .padding()
            }
            .background(Color(uiColor: .systemGroupedBackground))
            .navigationTitle("Video Sender")
            .task {
                AppLogger.info("Main screen appeared", category: "App")
                await camera.start()
                discovery.startSearching()
                signaling.onMessage = handleSignalingMessage
                camera.onConfigurationApplied = {
                    webRTC.updateEncoding(bitrate: bitrate, frameRate: camera.selectedFPS)
                    logControlSettings(event: "camera_configuration_applied")
                    if signaling.state == .connected {
                        sendConfiguration()
                    }
                }
                logControlSettings(event: "camera_ready")
                camera.onConfigurationError = { message in
                    sendError(code: "CAMERA_CONFIGURATION_FAILED", message: message)
                }
                webRTC.onLocalCandidate = { candidate in
                    signaling.send(SignalingMessage(
                        type: .iceCandidate,
                        candidate: candidate.sdp,
                        sdpMid: candidate.sdpMid,
                        sdpMLineIndex: Int32(candidate.sdpMLineIndex)
                    ))
                }
                webRTC.onStatus = { state in
                    signaling.send(SignalingMessage(type: .status, state: state))
                }
                webRTC.onStatistics = { stats in
                    guard signaling.state == .connected else { return }
                    signaling.send(SignalingMessage(
                        type: .stats,
                        captureFPS: camera.captureFPS,
                        encodeFPS: stats.encodeFPS,
                        bitrate: stats.bitrate,
                        packetsLost: stats.packetsLost,
                        rtt: stats.roundTripTime
                    ))
                    let now = Date()
                    guard now.timeIntervalSince(lastPerformanceLogDate) >= 5 else { return }
                    lastPerformanceLogDate = now
                    AppLogger.info(
                        """
                        PERFORMANCE camera=\(camera.selectedCamera.rawValue) resolution=\(camera.selectedResolution.rawValue) \
                        targetFPS=\(camera.selectedFPS) targetBitrateBps=\(bitrate) \
                        captureFPS=\(String(format: "%.1f", camera.captureFPS)) \
                        encodeFPS=\(String(format: "%.1f", stats.encodeFPS)) \
                        sentBitrateBps=\(stats.bitrate) packetsLost=\(stats.packetsLost) \
                        rttMs=\(String(format: "%.1f", stats.roundTripTime * 1000))
                        """,
                        category: "Streaming"
                    )
                }
            }
            .onChange(of: signaling.state) { _, state in
                if state == .connected {
                    signaling.sendCapabilities(
                        cameraCapabilities: camera.capabilitiesByCamera,
                        cameras: camera.availableCameras
                    )
                    sendConfiguration()
                }
            }
            .onDisappear {
                discovery.stopSearching()
                signaling.disconnect()
                webRTC.close()
                camera.stop()
            }
            .alert("Camera access is off", isPresented: Binding(
                get: { camera.permissionDenied },
                set: { isPresented in
                    if !isPresented {
                        camera.dismissPermissionAlert()
                    }
                }
            )) {
                Button("Cancel", role: .cancel) {}
                Button("Open Settings") {
                    guard let settingsURL = URL(string: UIApplication.openSettingsURLString) else { return }
                    UIApplication.shared.open(settingsURL)
                }
            } message: {
                Text("Allow camera access in Settings to preview and stream video.")
            }
        }
    }

    private var appVersion: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0.2"
    }

    private var logsPanel: some View {
        DisclosureGroup(isExpanded: $isLogsExpanded) {
            ScrollView {
                Text(logLines.isEmpty ? "No logs recorded yet." : logLines.reversed().joined(separator: "\n"))
                    .font(.system(.caption2, design: .monospaced))
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(10)
            }
            .frame(maxHeight: 240)
            .background(Color(uiColor: .secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 10))
        } label: {
            Label("Application logs (\(logLines.count))", systemImage: "text.alignleft")
                .font(.subheadline.weight(.semibold))
        }
        .padding(14)
        .background(.background, in: RoundedRectangle(cornerRadius: 14))
        .task(id: isLogsExpanded) {
            guard isLogsExpanded else { return }
            while !Task.isCancelled {
                logLines = AppLogger.recentLines()
                do {
                    try await Task.sleep(for: .seconds(1))
                } catch is CancellationError {
                    return
                } catch {
                    AppLogger.error("Could not refresh visible logs: \(error.localizedDescription)", category: "Logging")
                    return
                }
            }
        }
    }

    private var preview: some View {
        ZStack(alignment: .topTrailing) {
            CameraPreview(session: camera.session)
                .frame(maxWidth: .infinity)
                .aspectRatio(16 / 9, contentMode: .fit)
                .background(.black)
                .clipShape(RoundedRectangle(cornerRadius: 16))

            Label(camera.isRunning ? "LIVE" : "OFF", systemImage: "circle.fill")
                .font(.caption.weight(.bold))
                .foregroundStyle(.white)
                .padding(.horizontal, 10)
                .padding(.vertical, 6)
                .background(camera.isRunning ? .red : .gray, in: Capsule())
                .padding(12)
        }
    }

    private var cameraStatus: some View {
        HStack(spacing: 10) {
            Image(systemName: camera.isRunning ? "checkmark.circle.fill" : "info.circle.fill")
                .foregroundStyle(camera.isRunning ? .green : .secondary)
            Text(camera.status)
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .lineLimit(2)
            Spacer(minLength: 0)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var cameraControls: some View {
        VStack(spacing: 0) {
            Picker("Camera", selection: Binding(
                get: { camera.selectedCamera },
                set: { camera.setCamera($0) }
            )) {
                ForEach(camera.availableCameras) { position in
                    Text(position.rawValue.capitalized).tag(position)
                }
            }
            .disabled(!camera.isRunning)

            Divider()

            Picker("Resolution", selection: Binding(
                get: { camera.selectedResolution },
                set: { camera.setResolution($0) }
            )) {
                ForEach(camera.availableResolutions) { resolution in
                    Text(resolution.rawValue).tag(resolution)
                }
            }
            .disabled(!camera.isRunning || camera.availableResolutions.isEmpty)

            Divider()

            Picker("Frame rate", selection: Binding(
                get: { camera.selectedFPS },
                set: { camera.setFrameRate($0) }
            )) {
                ForEach(camera.availableFrameRates, id: \.self) { fps in
                    Text("\(fps) FPS").tag(fps)
                }
            }
            .disabled(!camera.isRunning || camera.availableFrameRates.isEmpty)
        }
        .pickerStyle(.menu)
        .padding(.horizontal, 14)
        .background(.background, in: RoundedRectangle(cornerRadius: 14))
    }

    private var captureStatistics: some View {
        HStack(spacing: 12) {
            statistic(title: "Capture", value: String(format: "%.0f FPS", camera.captureFPS))
            statistic(title: "Resolution", value: camera.selectedResolution.rawValue)
            statistic(title: "Last frame", value: camera.lastFrameTimestamp ?? "--")
        }
    }

    private var senderStatistics: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("WebRTC sender")
                .font(.caption.weight(.semibold))
                .foregroundStyle(.secondary)
            HStack(spacing: 12) {
                statistic(
                    title: "Encode",
                    value: String(format: "%.1f FPS", webRTC.statistics.encodeFPS)
                )
                statistic(
                    title: "Sent bitrate",
                    value: String(format: "%.1f Mbps", Double(webRTC.statistics.bitrate) / 1_000_000)
                )
                statistic(title: "Packets lost", value: "\(webRTC.statistics.packetsLost)")
            }
        }
    }

    private var connectionControls: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Text("Windows receiver")
                    .font(.headline)
                Spacer()
                Button {
                    discovery.reloadSearching()
                } label: {
                    Label("Reload", systemImage: "arrow.clockwise")
                }
                .buttonStyle(.bordered)
            }

            HStack {
                Text("Connection: \(signaling.state.title)")
                    .font(.caption.weight(.medium))
                    .foregroundStyle(signaling.state == .connected ? .green : .secondary)
                    .lineLimit(1)
                Spacer()
            }

            if !discovery.receivers.isEmpty {
                ForEach(discovery.receivers) { receiver in
                    Button {
                        guard let url = receiver.webSocketURL else {
                            connectionError = "Could not create a WebSocket URL for \(receiver.name)."
                            return
                        }
                        signaling.connect(to: url)
                    } label: {
                        Label(
                            "\(receiver.name)  ·  \(receiver.host):\(receiver.port)",
                            systemImage: "desktopcomputer"
                        )
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .buttonStyle(.bordered)
                }
            }

            Text(discovery.status)
                .font(.caption)
                .foregroundStyle(.secondary)

            HStack(spacing: 10) {
                TextField("Windows IP or host", text: $manualHost)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                    .keyboardType(.URL)
                    .textFieldStyle(.roundedBorder)

                TextField("Port", text: $manualPort)
                    .keyboardType(.numberPad)
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 76)
            }

            HStack {
                Button("Connect manually", action: connectManually)
                    .buttonStyle(.borderedProminent)
                    .disabled(signaling.state == .connecting || signaling.state == .connected)
                Spacer()
                if signaling.state == .connected {
                    Button("Disconnect", role: .destructive) {
                        signaling.disconnect()
                    }
                    .buttonStyle(.bordered)
                }
            }
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.background, in: RoundedRectangle(cornerRadius: 14))
        .alert("Connection error", isPresented: Binding(
            get: { connectionError != nil },
            set: { if !$0 { connectionError = nil } }
        )) {
            Button("OK", role: .cancel) { connectionError = nil }
        } message: {
            Text(connectionError ?? "")
        }
    }

    private var streamingControls: some View {
        HStack(spacing: 12) {
            Button {
                startStreaming()
            } label: {
                Label("Start streaming", systemImage: "play.fill")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .disabled(signaling.state != .connected || webRTC.isPrepared)

            Button(role: .destructive) {
                stopStreaming()
            } label: {
                Label("Stop", systemImage: "stop.fill")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.bordered)
            .disabled(!webRTC.isPrepared)
        }
    }

    private func connectManually() {
        guard let port = Int(manualPort), (1...65535).contains(port) else {
            connectionError = "Enter a valid TCP port between 1 and 65535."
            return
        }

        var components = URLComponents()
        components.scheme = "ws"
        components.host = manualHost.trimmingCharacters(in: .whitespacesAndNewlines)
        components.port = port
        guard let url = components.url else {
            connectionError = "Enter a valid Windows receiver IP address or host name."
            return
        }
        AppLogger.info("Connecting manually to signaling server", category: "App")
        signaling.connect(to: url)
    }

    private func handleSignalingMessage(_ message: SignalingMessage) {
        switch message.type {
        case .stats, .iceCandidate:
            break
        default:
            AppLogger.info("Received signaling message: \(message.type)", category: "Signaling")
        }

        switch message.type {
        case .start:
            startStreaming()
        case .stop:
            stopStreaming()
        case .setBitrate:
            guard let value = message.value?.integerValue else {
                sendError(code: "INVALID_BITRATE", message: "SET_BITRATE requires a value in bps.")
                return
            }
            bitrate = min(
                max(value, StreamingConfiguration.minimumBitrate),
                StreamingConfiguration.maximumBitrate
            )
            logControlSettings(event: "bitrate_updated_from_receiver")
            webRTC.updateEncoding(bitrate: bitrate, frameRate: camera.selectedFPS)
            sendConfiguration()
        case .setFPS:
            guard let fps = message.value?.integerValue, camera.availableFrameRates.contains(fps) else {
                sendError(code: "UNSUPPORTED_FPS", message: "The requested frame rate is not supported.")
                return
            }
            camera.setFrameRate(fps)
        case .setResolution:
            guard let width = message.width, let height = message.height,
                  let resolution = VideoResolution.allCases.first(where: {
                      Int($0.width) == width && Int($0.height) == height
                  }),
                  camera.availableResolutions.contains(resolution) else {
                sendError(code: "UNSUPPORTED_RESOLUTION", message: "The requested resolution is not supported.")
                return
            }
            camera.setResolution(resolution)
        case .setCamera:
            guard let name = message.value?.stringValue ?? message.camera,
                  let position = CameraPosition(rawValue: name.lowercased()),
                  camera.availableCameras.contains(position) else {
                sendError(code: "UNSUPPORTED_CAMERA", message: "The requested camera is not available.")
                return
            }
            camera.setCamera(position)
        case .offer:
            acceptOffer(message)
        case .iceCandidate:
            guard let candidate = message.candidate,
                  let lineIndex = message.sdpMLineIndex else {
                sendError(code: "INVALID_ICE_CANDIDATE", message: "ICE_CANDIDATE requires candidate and sdpMLineIndex.")
                return
            }
            webRTC.addRemoteCandidate(
                sdp: candidate,
                sdpMid: message.sdpMid,
                sdpMLineIndex: lineIndex
            )
        case .answer, .status, .capabilities, .configApplied, .stats, .error:
            break
        }
    }

    private func startStreaming() {
        guard signaling.state == .connected else {
            AppLogger.warning("Stream start requested while signaling is disconnected", category: "App")
            return
        }
        AppLogger.info("Starting video stream", category: "App")
        Task {
            do {
                try await prepareStreaming()
                signaling.send(SignalingMessage(type: .status, state: "connecting"))
            } catch {
                AppLogger.error("Could not start video stream: \(error.localizedDescription)", category: "App")
                sendError(code: "WEBRTC_START_FAILED", message: error.localizedDescription)
            }
        }
    }

    private func stopStreaming() {
        AppLogger.info("Stopping video stream", category: "App")
        streamPreparationTask?.cancel()
        streamPreparationTask = nil
        webRTC.close()
        camera.stop()
        signaling.send(SignalingMessage(type: .status, state: "closed"))
    }

    private func acceptOffer(_ message: SignalingMessage) {
        guard let sdp = message.sdp else {
            sendError(code: "INVALID_OFFER", message: "OFFER requires an SDP payload.")
            return
        }

        Task {
            do {
                try await prepareStreaming()
                webRTC.handleOffer(sdp) { result in
                    switch result {
                    case .success(let answer):
                        AppLogger.info("Sending WebRTC answer", category: "Signaling")
                        signaling.send(SignalingMessage(type: .answer, sdp: answer))
                    case .failure(let error):
                        AppLogger.error("WebRTC offer handling failed: \(error.localizedDescription)", category: "App")
                        sendError(code: "WEBRTC_ANSWER_FAILED", message: error.localizedDescription)
                    }
                }
            } catch {
                AppLogger.error("Could not prepare WebRTC stream: \(error.localizedDescription)", category: "App")
                sendError(code: "WEBRTC_START_FAILED", message: error.localizedDescription)
            }
        }
    }

    private func prepareStreaming() async throws {
        if webRTC.isPrepared { return }
        if let streamPreparationTask {
            try await streamPreparationTask.value
            return
        }

        let task = Task {
            await camera.start()
            guard camera.isRunning else {
                throw ContentViewError.cameraUnavailable(camera.status)
            }
            try webRTC.prepare(
                cameraManager: camera,
                bitrate: bitrate,
                frameRate: camera.selectedFPS
            )
        }
        streamPreparationTask = task
        defer { streamPreparationTask = nil }
        try await task.value
    }

    private func sendConfiguration() {
        signaling.send(SignalingMessage(
            type: .configApplied,
            fps: camera.selectedFPS,
            width: Int(camera.selectedResolution.width),
            height: Int(camera.selectedResolution.height),
            camera: camera.selectedCamera.rawValue,
            bitrate: bitrate
        ))
    }

    private func logControlSettings(event: String) {
        AppLogger.info(
            "CONTROL event=\(event) camera=\(camera.selectedCamera.rawValue) " +
                "resolution=\(camera.selectedResolution.rawValue) " +
                "targetFPS=\(camera.selectedFPS) targetBitrateBps=\(bitrate)",
            category: "Streaming"
        )
    }

    private func sendError(code: String, message: String) {
        AppLogger.error("\(code): \(message)", category: "Signaling")
        signaling.send(SignalingMessage(type: .error, code: code, message: message))
    }

    private func statistic(title: String, value: String) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title)
                .font(.caption)
                .foregroundStyle(.secondary)
            Text(value)
                .font(.subheadline.weight(.semibold))
                .lineLimit(1)
                .minimumScaleFactor(0.75)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(12)
        .background(.background, in: RoundedRectangle(cornerRadius: 12))
    }
}

private enum ContentViewError: LocalizedError {
    case cameraUnavailable(String)

    var errorDescription: String? {
        switch self {
        case .cameraUnavailable(let status):
            "Cannot start streaming because the camera is unavailable: \(status)"
        }
    }
}
