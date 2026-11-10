import SwiftUI
import UIKit

struct RTSPSenderView: View {
    @StateObject private var camera = CameraManager()
    @StateObject private var server = RTSPServer()
    @State private var bitrate = 12_000_000
    @State private var logLines: [String] = []
    @State private var isLogsExpanded = false
    @State private var isStarting = false
    @State private var selectedTab = 0

    var body: some View {
        TabView(selection: $selectedTab) {
            NavigationStack {
                ScrollView {
                    VStack(spacing: 18) {
                        HStack {
                            Text("Version \(appVersion)")
                            Spacer()
                        }
                        .font(.caption)
                        .foregroundStyle(.secondary)

                        VStack(alignment: .leading, spacing: 8) {
                            Label(
                                camera.status,
                                systemImage: camera.isRunning ? "camera.fill" : "camera"
                            )
                            .font(.subheadline)
                            Label(server.status, systemImage: "dot.radiowaves.left.and.right")
                                .font(.subheadline)
                            Label(
                                server.isClientConnected
                                    ? "Client: Connected" : "Client: Disconnected",
                                systemImage: server.isClientConnected
                                    ? "checkmark.circle.fill" : "circle"
                            )
                            .font(.subheadline)
                            .foregroundStyle(server.isClientConnected ? .green : .secondary)
                            if server.isRunning {
                                Text(server.streamURL)
                                    .font(.system(.footnote, design: .monospaced))
                                    .textSelection(.enabled)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(14)
                        .background(.background, in: RoundedRectangle(cornerRadius: 14))

                        cameraControls

                        VStack(alignment: .leading, spacing: 8) {
                            HStack {
                                Text("Video bitrate")
                                Spacer()
                                Text("\(bitrate / 1_000_000) Mbps")
                                    .foregroundStyle(.secondary)
                            }
                            Slider(
                                value: Binding(
                                    get: { Double(bitrate) },
                                    set: { bitrate = Int($0) }
                                ),
                                in: 2_000_000...20_000_000,
                                step: 1_000_000
                            )
                        }
                        .padding(14)
                        .background(.background, in: RoundedRectangle(cornerRadius: 14))
                        .onChange(of: bitrate) { _, value in
                            server.updateVideoSettings(
                                frameRate: camera.selectedFPS,
                                bitrate: value,
                                resolution: camera.selectedResolution
                            )
                        }

                        HStack(spacing: 12) {
                            Button {
                                Task { await startServer() }
                            } label: {
                                Label(
                                    isStarting ? "Starting..." : "Start",
                                    systemImage: "play.fill"
                                )
                                .frame(maxWidth: .infinity)
                            }
                            .buttonStyle(.borderedProminent)
                            .disabled(server.isRunning || isStarting)

                            Button {
                                server.stop(camera: camera)
                            } label: {
                                Label("Stop", systemImage: "stop.fill")
                                    .frame(maxWidth: .infinity)
                            }
                            .buttonStyle(.bordered)
                            .disabled(!server.isRunning)
                        }

                        statistics
                        logsPanel
                    }
                    .padding()
                }
                .background(Color(uiColor: .systemGroupedBackground))
                .navigationTitle("RTSP Video Sender")
            }
            .tabItem {
                Label("Config", systemImage: "slider.horizontal.3")
            }
            .tag(0)

            GeometryReader { geometry in
                ZStack(alignment: .topTrailing) {
                    CameraPreview(session: camera.session)
                        .clipped()

                    CenteringGuide()
                        .allowsHitTesting(false)
                        .accessibilityHidden(true)

                    if server.isRunning {
                        Label("LIVE", systemImage: "circle.fill")
                            .font(.caption.weight(.bold))
                            .foregroundStyle(.white)
                            .padding(.horizontal, 12)
                            .padding(.vertical, 8)
                            .background(.red, in: Capsule())
                            .frame(maxWidth: .infinity, alignment: .top)
                            .padding(16)
                            .accessibilityLabel("Live streaming")
                    }
                }
                .frame(width: geometry.size.width, height: geometry.size.height)
                .background(.black)
            }
            .tabItem {
                Label("Live Video", systemImage: "video.fill")
            }
            .tag(1)
        }
        .background(Color(uiColor: .systemGroupedBackground))
        .task {
            AppLogger.info("RTSP sender screen appeared", category: "RTSP")
            await startServer()
        }
        .onChange(of: camera.selectedFPS) { _, fps in
            server.updateVideoSettings(
                frameRate: fps,
                bitrate: bitrate,
                resolution: camera.selectedResolution
            )
        }
        .onChange(of: camera.selectedResolution) { _, resolution in
            server.updateVideoSettings(
                frameRate: camera.selectedFPS,
                bitrate: bitrate,
                resolution: resolution
            )
        }
        .onDisappear {
            server.stop(camera: camera)
            camera.stop()
        }
        .task(id: isLogsExpanded) {
            guard isLogsExpanded else { return }
            while !Task.isCancelled {
                logLines = AppLogger.recentLines()
                do {
                    try await Task.sleep(for: .seconds(1))
                } catch is CancellationError {
                    return
                } catch {
                    AppLogger.error(
                        "Could not refresh logs: \(error.localizedDescription)",
                        category: "Logging")
                    return
                }
            }
        }
        .alert(
            "Camera access is off",
            isPresented: Binding(
                get: { camera.permissionDenied },
                set: { presented in
                    if !presented {
                        camera.dismissPermissionAlert()
                    }
                }
            )
        ) {
            Button("Open Settings") {
                guard let url = URL(string: UIApplication.openSettingsURLString) else { return }
                UIApplication.shared.open(url)
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Allow camera access in Settings to publish an RTSP video stream.")
        }
    }

    private var appVersion: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0.2"
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

    private var statistics: some View {
        HStack(spacing: 12) {
            statistic("Capture", String(format: "%.0f FPS", camera.captureFPS))
            statistic("Resolution", camera.selectedResolution.rawValue)
            statistic("Port", "\(server.port)")
        }
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
    }

    private func statistic(_ title: String, _ value: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title)
                .font(.caption)
                .foregroundStyle(.secondary)
            Text(value)
                .font(.subheadline.weight(.semibold))
                .lineLimit(1)
                .minimumScaleFactor(0.7)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(12)
        .background(.background, in: RoundedRectangle(cornerRadius: 12))
    }

    @MainActor
    private func startServer() async {
        guard !isStarting else { return }
        isStarting = true
        defer { isStarting = false }
        if !camera.isRunning {
            await camera.start()
        }
        guard camera.isRunning else {
            AppLogger.error("Cannot start RTSP server because the camera is unavailable", category: "RTSP")
            return
        }
        server.start(camera: camera)
    }
}

private struct CenteringGuide: View {
    var body: some View {
        ZStack {
            CornerBrackets()
                .stroke(.white, style: StrokeStyle(lineWidth: 3, lineCap: .round))
                .shadow(color: .black.opacity(0.7), radius: 2)

            Circle()
                .stroke(.white, lineWidth: 2)
                .frame(width: 24, height: 24)
                .shadow(color: .black.opacity(0.7), radius: 2)

            Circle()
                .fill(.white)
                .frame(width: 4, height: 4)
                .shadow(color: .black.opacity(0.7), radius: 2)
        }
    }
}

private struct CornerBrackets: Shape {
    func path(in rect: CGRect) -> Path {
        let inset: CGFloat = 28
        let length: CGFloat = 34
        let minX = rect.minX + inset
        let maxX = rect.maxX - inset
        let minY = rect.minY + inset
        let maxY = rect.maxY - inset

        var path = Path()
        path.move(to: CGPoint(x: minX + length, y: minY))
        path.addLine(to: CGPoint(x: minX, y: minY))
        path.addLine(to: CGPoint(x: minX, y: minY + length))

        path.move(to: CGPoint(x: maxX - length, y: minY))
        path.addLine(to: CGPoint(x: maxX, y: minY))
        path.addLine(to: CGPoint(x: maxX, y: minY + length))

        path.move(to: CGPoint(x: minX, y: maxY - length))
        path.addLine(to: CGPoint(x: minX, y: maxY))
        path.addLine(to: CGPoint(x: minX + length, y: maxY))

        path.move(to: CGPoint(x: maxX - length, y: maxY))
        path.addLine(to: CGPoint(x: maxX, y: maxY))
        path.addLine(to: CGPoint(x: maxX, y: maxY - length))
        return path
    }
}
