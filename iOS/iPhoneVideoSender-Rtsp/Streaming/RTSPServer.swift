import AVFoundation
import Combine
import CoreImage
import CoreMedia
import Darwin
import Foundation
import Network
import VideoToolbox

final class RTSPServer: ObservableObject, @unchecked Sendable {
    @Published private(set) var status = "RTSP server stopped"
    @Published private(set) var isRunning = false
    @Published private(set) var isClientConnected = false

    let port = StreamingConfiguration.rtspPort
    private let discoveryPort: UInt16 = 8555
    private let statisticsPort: UInt16 = 8556
    private static let statisticsRequest = Data("GET_STATS".utf8)

    private let networkQueue = DispatchQueue(label: "iphone.rtsp.server.network")
    private let encoderQueue = DispatchQueue(label: "iphone.rtsp.server.encoder")
    private let settingsLock = NSLock()
    private let statisticsLock = NSLock()
    private let imageContext = CIContext(options: [.useSoftwareRenderer: false])
    private let udpRTPPort: UInt16 = 5004
    private var listener: NWListener?
    private var statisticsListener: NWListener?
    private var discoverySocket: Int32 = -1
    private var discoveryTimer: DispatchSourceTimer?
    private var discoverySendFailed = false
    private var client: NWConnection?
    private var connections: [ObjectIdentifier: NWConnection] = [:]
    private var connectionBuffers: [ObjectIdentifier: Data] = [:]
    private var pendingHTTPResponses: Set<ObjectIdentifier> = []
    private var udpSender: NWConnection?
    private var udpRtcpConnection: NWConnection?
    private var senderReportTimer: DispatchSourceTimer?
    private var transport: RTPTransport?
    private var sessionID = UUID().uuidString.replacingOccurrences(of: "-", with: "")
    private var sps: Data?
    private var pps: Data?
    private var videoWidth = 0
    private var videoHeight = 0
    private var configuredFrameRate = 30
    private var configuredBitrate = 12_000_000
    private var compressionSession: VTCompressionSession?
    private var compressionSessionRefcon: UnsafeMutableRawPointer?
    private var rtpSequence = UInt16.random(in: .min ... .max)
    private let ssrc = UInt32.random(in: .min ... .max)
    private var rtpPacketCount: UInt64 = 0
    private var rtpOctetCount: UInt64 = 0
    private var lastRtpTimestamp: UInt32 = 0
    private var encodedFrameCount: UInt64 = 0
    private var capturedFramesForStatistics: UInt64 = 0
    private var encodedFramesForStatistics: UInt64 = 0
    private var sentBytesForStatistics: UInt64 = 0
    private var previousCapturedFramesForStatistics: UInt64 = 0
    private var previousEncodedFramesForStatistics: UInt64 = 0
    private var previousSentBytesForStatistics: UInt64 = 0
    private var statisticsSampleDate = Date()
    private var nalLengthFieldBytes = 4
    private var isPlaying = false
    private var isStreamingEnabled = true
    private weak var sourceCamera: CameraManager?
    private var configuredServerAddress = RTSPServer.localIPv4Address() ?? "0.0.0.0"

    var streamURL: String {
        "rtsp://\(currentServerAddress()):\(port)/"
    }

    func start(camera: CameraManager) {
        networkQueue.async { [weak self, weak camera] in
            guard let self, let camera else { return }
            guard self.listener == nil else {
                self.updateStatus("RTSP server is already running")
                return
            }

            self.settingsLock.lock()
            self.configuredServerAddress = Self.localIPv4Address() ?? "0.0.0.0"
            self.sourceCamera = camera
            self.settingsLock.unlock()
            self.settingsLock.lock()
            self.configuredFrameRate = camera.selectedFPS
            self.settingsLock.unlock()
            self.encoderQueue.sync {
                self.configureEncoder(
                    width: Int(camera.selectedResolution.width),
                    height: Int(camera.selectedResolution.height)
                )
            }
            camera.onFrame = { [weak self] sampleBuffer in
                self?.encode(sampleBuffer)
            }

            do {
                let port = NWEndpoint.Port(rawValue: self.port)!
                let listener = try NWListener(using: .tcp, on: port)
                let serviceName = "\(ProcessInfo.processInfo.hostName) RTSP"
                listener.stateUpdateHandler = { [weak self] state in
                    switch state {
                    case .ready:
                        self?.networkQueue.async {
                            self?.updateStatus("RTSP ready at \(self?.streamURL ?? "unknown URL")")
                            guard let self else { return }
                            let discoveryStarted = self.startDiscoveryBroadcast()
                            AppLogger.info(
                                "RTSP listener ready: url=\(self.streamURL) transport=TCP port=\(self.port); UDP broadcast discovery \(discoveryStarted ? "active" : "unavailable") on port \(self.discoveryPort)",
                                category: "RTSP"
                            )
                        }
                    case .waiting(let error):
                        AppLogger.warning(
                            "RTSP listener waiting: \(error.localizedDescription)",
                            category: "RTSP"
                        )
                    case .failed(let error):
                        self?.networkQueue.async {
                            self?.updateStatus("RTSP server failed: \(error.localizedDescription)")
                            AppLogger.error(
                                "RTSP listener failed: tcp_port=\(self?.port ?? 0) error=\(error.localizedDescription)",
                                category: "RTSP"
                            )
                            self?.stopOnNetworkQueue(camera: camera)
                        }
                    case .cancelled:
                        self?.networkQueue.async {
                            self?.setRunning(false, status: "RTSP server stopped")
                            AppLogger.info(
                                "RTSP listener stopped: tcp_port=\(self?.port ?? 0), UDP broadcast discovery stopped",
                                category: "RTSP"
                            )
                        }
                    default:
                        break
                    }
                }
                listener.newConnectionHandler = { [weak self] connection in
                    self?.accept(connection)
                }
                self.listener = listener
                listener.start(queue: self.networkQueue)
                self.startStatisticsListener()
                self.setRunning(true, status: "Starting RTSP server...")
                AppLogger.info(
                    "RTSP server starting: url=\(self.streamURL) transport=TCP rtsp_port=\(self.port) discovery=UDP_subnet_broadcast discovery_udp_port=\(self.discoveryPort) service_name=\"\(serviceName)\"",
                    category: "RTSP"
                )
            } catch {
                camera.onFrame = nil
                self.updateStatus("Could not start RTSP server: \(error.localizedDescription)")
                AppLogger.error("Could not start RTSP server: \(error.localizedDescription)", category: "RTSP")
            }
        }
    }

    func stop(camera: CameraManager) {
        camera.onFrame = nil
        networkQueue.async { [weak self, weak camera] in
            guard let self else { return }
            self.stopOnNetworkQueue(camera: camera)
        }
    }

    func updateVideoSettings(frameRate: Int, bitrate: Int, resolution: VideoResolution) {
        settingsLock.lock()
        configuredFrameRate = max(1, frameRate)
        configuredBitrate = max(250_000, bitrate)
        settingsLock.unlock()
        AppLogger.info(
            "RTSP video settings changed: \(frameRate) FPS, \(bitrate) bps",
            category: "RTSP"
        )

        encoderQueue.async { [weak self] in
            guard let self, self.compressionSession != nil else { return }
            let width = Int(resolution.width)
            let height = Int(resolution.height)
            if self.videoWidth != width || self.videoHeight != height {
                self.configureEncoder(width: width, height: height)
            }
            guard let compressionSession = self.compressionSession else { return }
            let settings = self.currentSettings()
            VTSessionSetProperty(
                compressionSession,
                key: kVTCompressionPropertyKey_AverageBitRate,
                value: NSNumber(value: settings.bitrate)
            )
            VTSessionSetProperty(
                compressionSession,
                key: kVTCompressionPropertyKey_ExpectedFrameRate,
                value: NSNumber(value: settings.frameRate)
            )
            VTSessionSetProperty(
                compressionSession,
                key: kVTCompressionPropertyKey_MaxKeyFrameInterval,
                value: NSNumber(value: settings.frameRate)
            )
        }
    }

    private func currentSettings() -> (frameRate: Int, bitrate: Int) {
        settingsLock.lock()
        defer { settingsLock.unlock() }
        return (configuredFrameRate, configuredBitrate)
    }

    private func currentServerAddress() -> String {
        settingsLock.lock()
        defer { settingsLock.unlock() }
        return configuredServerAddress
    }

    private func stopOnNetworkQueue(camera: CameraManager?) {
        camera?.onFrame = nil
        stopDiscoveryBroadcast()
        stopSenderReportTimer()
        statisticsListener?.newConnectionHandler = nil
        statisticsListener?.cancel()
        statisticsListener = nil
        listener?.newConnectionHandler = nil
        listener?.cancel()
        listener = nil
        connections.values.forEach {
            $0.stateUpdateHandler = nil
            $0.cancel()
        }
        connections.removeAll()
        client = nil
        connectionBuffers.removeAll()
        pendingHTTPResponses.removeAll()
        updateClientConnection(false)
        udpSender?.cancel()
        udpSender = nil
        udpRtcpConnection?.cancel()
        udpRtcpConnection = nil
        transport = nil
        isPlaying = false
        isStreamingEnabled = true
        sourceCamera = nil
        resetStatistics()
        sps = nil
        pps = nil
        encoderQueue.async { [weak self] in
            guard let self else { return }
            if let compressionSession = self.compressionSession {
                VTCompressionSessionCompleteFrames(compressionSession, untilPresentationTimeStamp: .invalid)
                VTCompressionSessionInvalidate(compressionSession)
                self.compressionSession = nil
                if let refcon = self.compressionSessionRefcon {
                    Unmanaged<RTSPServer>.fromOpaque(refcon).release()
                    self.compressionSessionRefcon = nil
                }
            }
        }
        setRunning(false, status: "RTSP server stopped")
    }

    @discardableResult
    private func startDiscoveryBroadcast() -> Bool {
        guard discoverySocket < 0 else { return true }

        guard let broadcastConfiguration = Self.wifiIPv4BroadcastConfiguration() else {
            AppLogger.error(
                "Could not start UDP discovery: Wi-Fi interface en0 has no active IPv4 address and netmask",
                category: "RTSP"
            )
            return false
        }

        let socketDescriptor = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard socketDescriptor >= 0 else {
            AppLogger.error("Could not create UDP discovery socket: errno=\(errno)", category: "RTSP")
            return false
        }

        var interfaceIndex = broadcastConfiguration.interfaceIndex
        guard setsockopt(
            socketDescriptor,
            IPPROTO_IP,
            IP_BOUND_IF,
            &interfaceIndex,
            socklen_t(MemoryLayout<Int32>.size)
        ) == 0 else {
            let socketError = errno
            close(socketDescriptor)
            AppLogger.error(
                "Could not bind UDP discovery to \(broadcastConfiguration.interfaceName): errno=\(socketError)",
                category: "RTSP"
            )
            return false
        }

        var allowBroadcast: Int32 = 1
        guard setsockopt(
            socketDescriptor,
            SOL_SOCKET,
            SO_BROADCAST,
            &allowBroadcast,
            socklen_t(MemoryLayout<Int32>.size)
        ) == 0 else {
            let socketError = errno
            close(socketDescriptor)
            AppLogger.error("Could not enable UDP broadcast: errno=\(socketError)", category: "RTSP")
            return false
        }

        var localEndpoint = sockaddr_in()
        localEndpoint.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        localEndpoint.sin_family = sa_family_t(AF_INET)
        localEndpoint.sin_addr = broadcastConfiguration.localAddress
        guard withUnsafePointer(to: &localEndpoint, {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(socketDescriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }) == 0 else {
            let socketError = errno
            close(socketDescriptor)
            AppLogger.error(
                "Could not bind UDP discovery to \(broadcastConfiguration.interfaceName) at \(broadcastConfiguration.localAddressText): errno=\(socketError)",
                category: "RTSP"
            )
            return false
        }

        discoverySocket = socketDescriptor
        discoveryBroadcastAddress = broadcastConfiguration.broadcastAddress
        AppLogger.info(
            "UDP discovery configured: interface=\(broadcastConfiguration.interfaceName) local_ip=\(broadcastConfiguration.localAddressText) broadcast_address=\(broadcastConfiguration.broadcastAddressText) udp_port=\(discoveryPort)",
            category: "RTSP"
        )
        let timer = DispatchSource.makeTimerSource(queue: networkQueue)
        timer.schedule(deadline: .now(), repeating: .seconds(1))
        timer.setEventHandler { [weak self] in
            self?.sendDiscoveryBroadcast()
        }
        discoveryTimer = timer
        timer.resume()
        return true
    }

    private var discoveryBroadcastAddress = in_addr(s_addr: INADDR_BROADCAST)
    private var discoveryBroadcastSent = false

    private func sendDiscoveryBroadcast() {
        guard discoverySocket >= 0 else { return }
        let payload = Data("IPHONE_RTSP|1|\(port)".utf8)
        var destination = sockaddr_in()
        destination.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        destination.sin_family = sa_family_t(AF_INET)
        destination.sin_port = in_port_t(discoveryPort).bigEndian
        destination.sin_addr = discoveryBroadcastAddress

        let bytesSent = payload.withUnsafeBytes { payloadBytes in
            withUnsafePointer(to: &destination) { addressPointer in
                addressPointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    sendto(
                        discoverySocket,
                        payloadBytes.baseAddress,
                        payloadBytes.count,
                        0,
                        $0,
                        socklen_t(MemoryLayout<sockaddr_in>.size)
                    )
                }
            }
        }

        if bytesSent < 0 {
            let socketError = errno
            if !discoverySendFailed {
                AppLogger.warning(
                    "UDP discovery broadcast failed: destination=\(Self.ipv4String(discoveryBroadcastAddress) ?? "unknown"):\(discoveryPort) errno=\(socketError)",
                    category: "RTSP"
                )
                discoverySendFailed = true
            }
        } else {
            if !discoveryBroadcastSent {
                AppLogger.info(
                    "UDP discovery broadcast sent: destination=\(Self.ipv4String(discoveryBroadcastAddress) ?? "unknown"):\(discoveryPort) bytes=\(bytesSent)",
                    category: "RTSP"
                )
                discoveryBroadcastSent = true
            }
            if discoverySendFailed {
                AppLogger.info("UDP discovery broadcast send recovered", category: "RTSP")
            }
            discoverySendFailed = false
        }
    }

    private func stopDiscoveryBroadcast() {
        discoveryTimer?.cancel()
        discoveryTimer = nil
        if discoverySocket >= 0 {
            close(discoverySocket)
            discoverySocket = -1
        }
        discoveryBroadcastSent = false
    }

    private func startStatisticsListener() {
        guard statisticsListener == nil else { return }
        do {
            let listener = try NWListener(
                using: .udp,
                on: NWEndpoint.Port(rawValue: statisticsPort)!
            )
            listener.newConnectionHandler = { [weak self] connection in
                guard let self else {
                    connection.cancel()
                    return
                }
                connection.start(queue: self.networkQueue)
                self.receiveStatisticsRequest(from: connection)
            }
            listener.stateUpdateHandler = { [weak self] state in
                if case .failed(let error) = state {
                    guard let self else { return }
                    AppLogger.error(
                        "Statistics listener failed on UDP \(self.statisticsPort): \(error.localizedDescription)",
                        category: "RTSP"
                    )
                }
            }
            statisticsListener = listener
            listener.start(queue: networkQueue)
            AppLogger.info(
                "RTSP statistics listener starting on UDP \(statisticsPort)",
                category: "RTSP"
            )
        } catch {
            AppLogger.error(
                "Could not start RTSP statistics listener on UDP \(statisticsPort): \(error.localizedDescription)",
                category: "RTSP"
            )
        }
    }

    private func receiveStatisticsRequest(from connection: NWConnection) {
        connection.receiveMessage { [weak self, weak connection] data, _, isComplete, error in
            guard let self, let connection else { return }
            if let error {
                AppLogger.warning(
                    "RTSP statistics request failed: \(error.localizedDescription)",
                    category: "RTSP"
                )
                connection.cancel()
                return
            }
            if let data, !data.isEmpty {
                if data == Self.statisticsRequest {
                    do {
                        let response = try JSONEncoder().encode(self.statisticsMessage())
                        connection.send(
                            content: response,
                            completion: .contentProcessed { [weak connection] sendError in
                                if let sendError {
                                    AppLogger.warning(
                                        "RTSP statistics response failed: \(sendError.localizedDescription)",
                                        category: "RTSP"
                                    )
                                }
                                connection?.cancel()
                            }
                        )
                    } catch {
                        AppLogger.error(
                            "Could not encode RTSP statistics: \(error.localizedDescription)",
                            category: "RTSP"
                        )
                        connection.cancel()
                    }
                    return
                }
            }
            if isComplete {
                connection.cancel()
            } else {
                self.receiveStatisticsRequest(from: connection)
            }
        }
    }

    private func statisticsMessage() -> RTSPStatisticsMessage {
        let now = Date()
        statisticsLock.lock()
        let elapsed = max(now.timeIntervalSince(statisticsSampleDate), 0.001)
        let captureFps = Double(
            capturedFramesForStatistics - previousCapturedFramesForStatistics
        ) / elapsed
        let encodeFps = Double(
            encodedFramesForStatistics - previousEncodedFramesForStatistics
        ) / elapsed
        let bitrateBps = Double(
            sentBytesForStatistics - previousSentBytesForStatistics
        ) * 8 / elapsed
        previousCapturedFramesForStatistics = capturedFramesForStatistics
        previousEncodedFramesForStatistics = encodedFramesForStatistics
        previousSentBytesForStatistics = sentBytesForStatistics
        statisticsSampleDate = now
        statisticsLock.unlock()

        return RTSPStatisticsMessage(
            type: "STATS",
            captureFps: captureFps,
            encodeFps: encodeFps,
            senderBitrateBps: bitrateBps
        )
    }

    private func resetStatistics() {
        statisticsLock.lock()
        capturedFramesForStatistics = 0
        encodedFramesForStatistics = 0
        sentBytesForStatistics = 0
        previousCapturedFramesForStatistics = 0
        previousEncodedFramesForStatistics = 0
        previousSentBytesForStatistics = 0
        statisticsSampleDate = Date()
        statisticsLock.unlock()
        rtpPacketCount = 0
        rtpOctetCount = 0
    }

    private func startSenderReportTimer() {
        stopSenderReportTimer()
        let timer = DispatchSource.makeTimerSource(queue: networkQueue)
        timer.schedule(deadline: .now() + .seconds(1), repeating: .seconds(1))
        timer.setEventHandler { [weak self] in
            self?.sendSenderReport()
        }
        senderReportTimer = timer
        timer.resume()
    }

    private func stopSenderReportTimer() {
        senderReportTimer?.cancel()
        senderReportTimer = nil
    }

    private func sendSenderReport() {
        guard isPlaying else { return }
        let unixTime = Date().timeIntervalSince1970
        let seconds = UInt32(unixTime + 2_208_988_800)
        let fractionalSeconds = UInt32(
            (unixTime.truncatingRemainder(dividingBy: 1) * 4_294_967_296).rounded(.down)
        )
        var report = Data([0x80, 200, 0, 6])
        appendUInt32(ssrc, to: &report)
        appendUInt32(seconds, to: &report)
        appendUInt32(fractionalSeconds, to: &report)
        appendUInt32(lastRtpTimestamp, to: &report)
        appendUInt32(UInt32(truncatingIfNeeded: rtpPacketCount), to: &report)
        appendUInt32(UInt32(truncatingIfNeeded: rtpOctetCount), to: &report)
        switch transport {
        case .tcp(let channel):
            guard let client else { return }
            var framed = Data([0x24, channel &+ 1, 0, UInt8(report.count)])
            framed.append(report)
            client.send(content: framed, completion: .contentProcessed { error in
                if let error {
                    AppLogger.warning(
                        "RTCP sender report failed: \(error.localizedDescription)",
                        category: "RTSP"
                    )
                }
            })
        case .udp:
            udpRtcpConnection?.send(content: report, completion: .contentProcessed { error in
                if let error {
                    AppLogger.warning(
                        "RTCP sender report failed: \(error.localizedDescription)",
                        category: "RTSP"
                    )
                }
            })
        case .none:
            break
        }
    }

    private func appendUInt32(_ value: UInt32, to data: inout Data) {
        data.append(UInt8(truncatingIfNeeded: value >> 24))
        data.append(UInt8(truncatingIfNeeded: value >> 16))
        data.append(UInt8(truncatingIfNeeded: value >> 8))
        data.append(UInt8(truncatingIfNeeded: value))
    }

    private func accept(_ connection: NWConnection) {
        let identifier = ObjectIdentifier(connection)
        connections[identifier] = connection
        connectionBuffers[identifier] = Data()
        AppLogger.info(
            "Incoming RTSP/HTTP client: endpoint=\(connection.endpoint) server_url=\(streamURL)",
            category: "RTSP"
        )
        connection.stateUpdateHandler = { [weak self, weak connection] state in
            guard let self, let connection else { return }
            switch state {
            case .ready:
                self.receive(from: connection)
            case .failed(let error):
                AppLogger.warning("RTSP/HTTP client disconnected: \(error.localizedDescription)", category: "RTSP")
                self.closeClient(connection)
            case .cancelled:
                self.closeClient(connection)
            default:
                break
            }
        }
        connection.start(queue: networkQueue)
        AppLogger.info("RTSP client connected", category: "RTSP")
    }

    private func receive(from connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { [weak self, weak connection] data, _, isComplete, error in
            guard let self, let connection,
                  self.connections[ObjectIdentifier(connection)] === connection else { return }
            if let error {
                AppLogger.warning("RTSP/HTTP client receive failed: \(error.localizedDescription)", category: "RTSP")
                self.closeClient(connection)
                return
            }
            if let data, !data.isEmpty {
                let identifier = ObjectIdentifier(connection)
                self.connectionBuffers[identifier, default: Data()].append(data)
                self.processRequests(from: connection)
            }
            let identifier = ObjectIdentifier(connection)
            guard !self.pendingHTTPResponses.contains(identifier) else { return }
            if isComplete {
                self.closeClient(connection)
            } else {
                self.receive(from: connection)
            }
        }
    }

    private func processRequests(from connection: NWConnection) {
        let delimiter = Data("\r\n\r\n".utf8)
        let identifier = ObjectIdentifier(connection)
        while connectionBuffers[identifier]?.isEmpty == false {
            guard var buffer = connectionBuffers[identifier] else { return }
            if buffer.first == 0x24 {
                guard buffer.count >= 4 else { return }
                guard client === connection else {
                    closeClient(connection)
                    return
                }
                let interleavedLength = Int(buffer[buffer.startIndex + 2]) << 8
                    | Int(buffer[buffer.startIndex + 3])
                let frameLength = 4 + interleavedLength
                guard buffer.count >= frameLength else { return }
                buffer.removeSubrange(buffer.startIndex ..< buffer.startIndex + frameLength)
                connectionBuffers[identifier] = buffer
                continue
            }
            guard let headerRange = buffer.range(of: delimiter) else { return }
            let headerData = buffer.subdata(in: buffer.startIndex ..< headerRange.lowerBound)
            guard let headerText = String(data: headerData, encoding: .utf8) else {
                closeClient(connection)
                return
            }
            let lines = headerText.components(separatedBy: "\r\n")
            guard let requestLine = lines.first else {
                closeClient(connection)
                return
            }
            let headers = Dictionary(
                lines.dropFirst().compactMap { line -> (String, String)? in
                    guard let separator = line.firstIndex(of: ":") else { return nil }
                    let name = line[..<separator].trimmingCharacters(in: .whitespaces)
                    let value = line[line.index(after: separator)...].trimmingCharacters(in: .whitespaces)
                    return (name.lowercased(), value)
                },
                uniquingKeysWith: { _, latest in latest }
            )
            let bodyLength = Int(headers["content-length"] ?? "0") ?? 0
            let requestLength = headerRange.upperBound + bodyLength
            guard buffer.count >= requestLength else { return }
            let body = buffer.subdata(in: headerRange.upperBound ..< requestLength)
            buffer.removeSubrange(buffer.startIndex ..< requestLength)
            connectionBuffers[identifier] = buffer

            let fields = requestLine.split(separator: " ")
            guard fields.count >= 3 else {
                sendResponse(to: connection, status: "400 Bad Request", cseq: headers["cseq"])
                continue
            }
            let method = String(fields[0]).uppercased()
            let url = String(fields[1])
            let version = String(fields[2]).uppercased()
            if version.hasPrefix("HTTP/") {
                pendingHTTPResponses.insert(identifier)
                handleHTTPAPIRequest(
                    method: method,
                    url: url,
                    body: body,
                    connection: connection
                )
                return
            }
            guard version.hasPrefix("RTSP/") else {
                closeClient(connection)
                return
            }
            if client !== connection {
                activateRTSPClient(connection)
            }
            handleRequest(method: method, url: url, headers: headers, connection: connection)
        }
    }

    private func activateRTSPClient(_ connection: NWConnection) {
        if let existingClient = client, existingClient !== connection {
            closeClient(existingClient)
        }
        stopSenderReportTimer()
        udpSender?.cancel()
        udpSender = nil
        udpRtcpConnection?.cancel()
        udpRtcpConnection = nil
        transport = nil
        isPlaying = false
        sessionID = UUID().uuidString.replacingOccurrences(of: "-", with: "")
        client = connection
        updateClientConnection(true)
        updateStatus("RTSP client connected")
        AppLogger.info("RTSP client connected: endpoint=\(connection.endpoint)", category: "RTSP")
    }

    private func handleHTTPAPIRequest(
        method: String,
        url: String,
        body: Data,
        connection: NWConnection
    ) {
        let rawPath = URLComponents(string: url)?.path ?? "/"
        let path = "/" + rawPath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        AppLogger.info("Camera API request method=\(method) path=\(path)", category: "RTSP")
        guard method == "GET" || method == "POST" else {
            sendHTTPResponse(to: connection, status: "405 Method Not Allowed", object: [
                "message": "Only GET and POST are supported."
            ])
            return
        }

        let request: [String: Any]
        if method == "POST", !body.isEmpty {
            do {
                guard let object = try JSONSerialization.jsonObject(with: body) as? [String: Any] else {
                    sendHTTPResponse(to: connection, status: "400 Bad Request", object: [
                        "message": "Request body must be a JSON object."
                    ])
                    return
                }
                request = object
            } catch {
                sendHTTPResponse(to: connection, status: "400 Bad Request", object: [
                    "message": "Invalid JSON request body."
                ])
                return
            }
        } else {
            request = [:]
        }

        switch path {
        case "/ping":
            sendHTTPResponse(to: connection, object: ["message": "pong"])
        case "/camera-toggle":
            let shouldStart = request["start"] as? Bool ?? true
            if shouldStart {
                isStreamingEnabled = true
                sendHTTPResponse(to: connection, object: ["message": "camera is already running"])
            } else {
                isStreamingEnabled = false
                isPlaying = false
                stopSenderReportTimer()
                sendHTTPResponse(to: connection, object: ["message": "camera did stop"])
            }
        case "/camera-settings" where method == "GET":
            sendHTTPResponse(to: connection, object: cameraSettingsResponse())
        case "/camera-settings":
            updateCameraSettings(request, connection: connection)
        case "/camera-autozoom":
            sendHTTPResponse(to: connection, status: "501 Not Implemented", object: [
                "message": "Automatic zoom is not supported by the iPhone sender."
            ])
        case "/audio", "/bandwidth", "/exit":
            sendHTTPResponse(to: connection, status: "501 Not Implemented", object: [
                "message": "This camera API is not supported by the iPhone sender."
            ])
        default:
            sendHTTPResponse(to: connection, status: "404 Not Found", object: [
                "message": "The requested resource was not found."
            ])
        }
    }

    private func cameraSettingsResponse() -> [String: Any] {
        let frameRates = sourceCamera?.availableFrameRates ?? [currentSettings().frameRate]
        let currentFrameRate = sourceCamera?.selectedFPS ?? currentSettings().frameRate
        let appVersion = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0"
        return [
            "speed": ["min": 1, "max": 1, "current": 1],
            "iso": ["min": 1, "max": 1, "current": 1],
            "zoom": ["min": 1.0, "max": 1.0, "current": 1.0],
            "frameRate": [
                "min": frameRates.min() ?? currentFrameRate,
                "max": frameRates.max() ?? currentFrameRate,
                "current": currentFrameRate
            ],
            "deviceInfo": [
                "appVersion": appVersion,
                "tcpSupport": true,
                "device": "iPhone"
            ]
        ]
    }

    private func updateCameraSettings(_ request: [String: Any], connection: NWConnection) {
        let exposureSettingChanged = ["speed", "iso"].contains { key in
            guard let value = request[key] as? NSNumber else { return false }
            return value.intValue != 1
        }
        let zoomChanged = (request["zoom"] as? NSNumber).map { $0.doubleValue != 1.0 } ?? false
        if exposureSettingChanged || zoomChanged {
            sendHTTPResponse(to: connection, object: [
                "code": "1",
                "message": "The iPhone sender supports frameRate changes only; speed, ISO, and zoom are fixed."
            ])
            return
        }

        if let frameRate = request["frameRate"] as? Int {
            let supportedRates = sourceCamera?.availableFrameRates ?? [currentSettings().frameRate]
            guard supportedRates.contains(frameRate), let sourceCamera else {
                sendHTTPResponse(to: connection, object: [
                    "code": "1",
                    "message": "The requested frame rate is not supported by the active iPhone camera."
                ])
                return
            }
            sourceCamera.setFrameRate(frameRate)
            updateVideoSettings(
                frameRate: frameRate,
                bitrate: currentSettings().bitrate,
                resolution: sourceCamera.selectedResolution
            )
        }
7
        
        sendHTTPResponse(to: connection, object: ["code": 0, "message": "success"])
    }

    private func sendHTTPResponse(
        to connection: NWConnection,
        status: String = "200 OK",
        object: [String: Any]
    ) {
        do {
            let body = try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys])
            var response = "HTTP/1.1 \(status)\r\n"
            response += "Content-Type: application/json; charset=utf-8\r\n"
            response += "Content-Length: \(body.count)\r\n"
            response += "Connection: close\r\n\r\n"
            var data = Data(response.utf8)
            data.append(body)
            connection.send(content: data, completion: .contentProcessed { [weak self, weak connection] error in
                guard let self, let connection else { return }
                if let error {
                    AppLogger.error("Camera API response failed: \(error.localizedDescription)", category: "RTSP")
                }
                self.closeClient(connection)
            })
        } catch {
            AppLogger.error("Could not encode camera API response: \(error.localizedDescription)", category: "RTSP")
            closeClient(connection)
        }
    }

    private func handleRequest(
        method: String,
        url: String,
        headers: [String: String],
        connection: NWConnection
    ) {
        let cseq = headers["cseq"]
        let requestPath = URLComponents(string: url)?.path ?? "/"
        AppLogger.info("RTSP request method=\(method) path=\(requestPath)", category: "RTSP")
        switch method {
        case "OPTIONS":
            sendResponse(
                to: connection,
                status: "200 OK",
                cseq: cseq,
                headers: ["Public": "OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN, GET_PARAMETER"]
            )
        case "DESCRIBE":
            guard let sps, let pps else {
                sendResponse(
                    to: connection,
                    status: "503 Service Unavailable",
                    cseq: cseq,
                    headers: ["Retry-After": "1"]
                )
                return
            }
            let serverAddress = currentServerAddress()
            let address = serverAddress.contains(":") ? "[\(serverAddress)]" : serverAddress
            let baseURL = "rtsp://\(address):\(port)/"
            let sdp = [
                "v=0",
                "o=- 0 0 IN IP4 \(serverAddress)",
                "s=iPhone H.264",
                "c=IN IP4 \(serverAddress)",
                "t=0 0",
                "a=control:*",
                "m=video 0 RTP/AVP 96",
                "a=rtpmap:96 H264/90000",
                "a=fmtp:96 packetization-mode=1;sprop-parameter-sets=\(sps.base64EncodedString()),\(pps.base64EncodedString())",
                "a=framerate:\(currentSettings().frameRate)",
                "a=control:trackID=0",
                ""
            ].joined(separator: "\r\n")
            sendResponse(
                to: connection,
                status: "200 OK",
                cseq: cseq,
                headers: [
                    "Content-Type": "application/sdp",
                    "Content-Base": baseURL
                ],
                body: Data(sdp.utf8)
            )
        case "SETUP":
            setupTransport(headers["transport"], cseq: cseq, connection: connection)
        case "PLAY":
            guard isStreamingEnabled else {
                sendResponse(to: connection, status: "503 Service Unavailable", cseq: cseq)
                return
            }
            guard transport != nil else {
                sendResponse(to: connection, status: "455 Method Not Valid in This State", cseq: cseq)
                return
            }
            resetStatistics()
            isPlaying = true
            startSenderReportTimer()
            updateStatus("Streaming RTSP to client")
            sendResponse(
                to: connection,
                status: "200 OK",
                cseq: cseq,
                headers: [
                    "Session": sessionID,
                    "Range": "npt=0.000-",
                    "RTP-Info": "url=\(url);seq=\(rtpSequence);rtptime=0"
                ]
            )
            requestKeyFrame()
            AppLogger.info("RTSP PLAY accepted", category: "RTSP")
        case "PAUSE":
            isPlaying = false
            stopSenderReportTimer()
            updateStatus("RTSP client paused")
            sendResponse(to: connection, status: "200 OK", cseq: cseq, headers: ["Session": sessionID])
        case "TEARDOWN":
            isPlaying = false
            stopSenderReportTimer()
            transport = nil
            udpSender?.cancel()
            udpSender = nil
            udpRtcpConnection?.cancel()
            udpRtcpConnection = nil
            sendResponse(to: connection, status: "200 OK", cseq: cseq, headers: ["Session": sessionID])
            updateStatus("RTSP server ready; client disconnected")
        case "GET_PARAMETER":
            sendResponse(to: connection, status: "200 OK", cseq: cseq, headers: ["Session": sessionID])
        default:
            sendResponse(to: connection, status: "405 Method Not Allowed", cseq: cseq)
        }
    }

    private func setupTransport(_ header: String?, cseq: String?, connection: NWConnection) {
        guard let header else {
            sendResponse(to: connection, status: "400 Bad Request", cseq: cseq)
            return
        }
        let lowercased = header.lowercased()
        if lowercased.contains("rtp/avp/tcp") {
            let channels = parameter(named: "interleaved", in: header) ?? "0-1"
            guard let channel = channels.split(separator: "-").first.flatMap({ UInt8($0) }),
                  channel < UInt8.max else {
                sendResponse(to: connection, status: "461 Unsupported Transport", cseq: cseq)
                return
            }
            transport = .tcp(channel: channel)
            udpSender?.cancel()
            udpSender = nil
            udpRtcpConnection?.cancel()
            udpRtcpConnection = nil
            AppLogger.info("RTSP transport selected=TCP interleaved_channel=\(channel)", category: "RTSP")
            sendResponse(
                to: connection,
                status: "200 OK",
                cseq: cseq,
                headers: [
                    "Session": sessionID,
                    "Transport": "RTP/AVP/TCP;unicast;interleaved=\(channels)"
                ]
            )
            return
        }

        guard lowercased.contains("rtp/avp"),
              let clientPorts = parameter(named: "client_port", in: header),
              let rtpPortText = clientPorts.split(separator: "-").first.map(String.init),
              let rtpPort = UInt16(rtpPortText),
              let clientRtcpPort = UInt16(exactly: Int(rtpPort) + 1),
              let endpoint = connection.currentPath?.remoteEndpoint,
              case let .hostPort(host, _) = endpoint,
              let remotePort = NWEndpoint.Port(rawValue: rtpPort),
              let localPort = NWEndpoint.Port(rawValue: udpRTPPort) else {
            sendResponse(to: connection, status: "461 Unsupported Transport", cseq: cseq)
            return
        }

        let localHost: NWEndpoint.Host
        switch host {
        case .ipv4(_):
            localHost = .ipv4(.any)
        case .ipv6(_):
            localHost = .ipv6(.any)
        default:
            sendResponse(to: connection, status: "461 Unsupported Transport", cseq: cseq)
            return
        }
        let parameters = NWParameters.udp
        parameters.requiredLocalEndpoint = .hostPort(host: localHost, port: localPort)
        let sender = NWConnection(
            to: .hostPort(host: host, port: remotePort),
            using: parameters
        )
        let rtcpParameters = NWParameters.udp
        rtcpParameters.requiredLocalEndpoint = .hostPort(
            host: localHost,
            port: NWEndpoint.Port(rawValue: udpRTPPort &+ 1)!
        )
        let rtcpConnection = NWConnection(
            to: .hostPort(
                host: host,
                port: NWEndpoint.Port(rawValue: clientRtcpPort)!
            ),
            using: rtcpParameters
        )
        udpSender?.cancel()
        udpSender = sender
        udpRtcpConnection?.cancel()
        udpRtcpConnection = rtcpConnection
        transport = .udp
        rtcpConnection.stateUpdateHandler = { [weak self, weak rtcpConnection] state in
            guard let self, let rtcpConnection,
                  self.udpRtcpConnection === rtcpConnection else { return }
            switch state {
            case .failed(let error):
                AppLogger.warning(
                    "RTCP connection failed: \(error.localizedDescription)",
                    category: "RTSP"
                )
            default:
                break
            }
        }
        rtcpConnection.start(queue: networkQueue)
        sender.stateUpdateHandler = { [weak self, weak connection, weak sender] state in
            guard let self, let connection, let sender, self.udpSender === sender else { return }
            switch state {
            case .ready:
                guard self.client === connection else { return }
                AppLogger.info(
                    "RTSP transport selected=UDP client_rtp_port=\(rtpPort) server_rtp_port=\(self.udpRTPPort)",
                    category: "RTSP"
                )
                self.sendResponse(
                    to: connection,
                    status: "200 OK",
                    cseq: cseq,
                    headers: [
                        "Session": self.sessionID,
                        "Transport": "RTP/AVP;unicast;client_port=\(rtpPort)-\(rtpPort &+ 1);server_port=\(self.udpRTPPort)-\(self.udpRTPPort &+ 1)"
                    ]
                )
            case .failed(let error):
                self.updateStatus("RTP UDP transport failed: \(error.localizedDescription)")
                self.transport = nil
                self.udpSender = nil
                AppLogger.error("Could not start RTP UDP transport: \(error.localizedDescription)", category: "RTSP")
                if self.client === connection {
                    self.sendResponse(to: connection, status: "461 Unsupported Transport", cseq: cseq)
                }
            default:
                break
            }
        }
        sender.start(queue: networkQueue)
    }

    private func sendResponse(
        to connection: NWConnection,
        status: String,
        cseq: String?,
        headers: [String: String] = [:],
        body: Data = Data()
    ) {
        var response = "RTSP/1.0 \(status)\r\n"
        if let cseq {
            response += "CSeq: \(cseq)\r\n"
        }
        response += "Server: iPhoneVideoSender-RTSP\r\n"
        for (name, value) in headers {
            response += "\(name): \(value)\r\n"
        }
        if !body.isEmpty {
            response += "Content-Length: \(body.count)\r\n"
        }
        response += "\r\n"
        var data = Data(response.utf8)
        data.append(body)
        connection.send(content: data, completion: .contentProcessed { [weak self] error in
            if let error {
                self?.updateStatus("RTSP response failed: \(error.localizedDescription)")
            }
        })
    }

    private func parameter(named name: String, in header: String) -> String? {
        header.split(separator: ";").compactMap { part -> String? in
            let pair = part.split(separator: "=", maxSplits: 1).map(String.init)
            guard pair.count == 2, pair[0].trimmingCharacters(in: .whitespaces)
                .caseInsensitiveCompare(name) == .orderedSame else { return nil }
            return pair[1].trimmingCharacters(in: .whitespaces)
        }.first
    }

    private func closeClient(_ connection: NWConnection) {
        let identifier = ObjectIdentifier(connection)
        connections.removeValue(forKey: identifier)
        connectionBuffers.removeValue(forKey: identifier)
        pendingHTTPResponses.remove(identifier)
        connection.stateUpdateHandler = nil
        connection.cancel()
        guard client === connection else { return }
        client = nil
        updateClientConnection(false)
        isPlaying = false
        stopSenderReportTimer()
        transport = nil
        udpSender?.cancel()
        udpSender = nil
        udpRtcpConnection?.cancel()
        udpRtcpConnection = nil
        updateStatus("RTSP server ready; waiting for a client")
        AppLogger.info("RTSP client disconnected", category: "RTSP")
    }

    private func encode(_ sampleBuffer: CMSampleBuffer) {
        statisticsLock.lock()
        capturedFramesForStatistics += 1
        statisticsLock.unlock()
        encoderQueue.async { [weak self] in
            guard let self,
                  let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
            if self.compressionSession == nil {
                AppLogger.error("H.264 encoder is unavailable; dropping frame", category: "RTSP")
                return
            }
            guard let compressionSession = self.compressionSession,
                  let inputBuffer = self.outputPixelBuffer(
                      for: pixelBuffer,
                      session: compressionSession
                  ) else { return }
            let presentationTime = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
            self.encodeNextFrame(
                inputBuffer,
                presentationTime: presentationTime,
                duration: CMSampleBufferGetDuration(sampleBuffer),
                session: compressionSession
            )
        }
    }

    private func outputPixelBuffer(
        for inputBuffer: CVPixelBuffer,
        session: VTCompressionSession
    ) -> CVPixelBuffer? {
        let inputWidth = CVPixelBufferGetWidth(inputBuffer)
        let inputHeight = CVPixelBufferGetHeight(inputBuffer)
        guard inputWidth != videoWidth || inputHeight != videoHeight else {
            return inputBuffer
        }

        guard let pool = VTCompressionSessionGetPixelBufferPool(session) else {
            AppLogger.error("H.264 encoder did not provide an output pixel-buffer pool", category: "RTSP")
            return nil
        }

        var outputBuffer: CVPixelBuffer?
        let status = CVPixelBufferPoolCreatePixelBuffer(kCFAllocatorDefault, pool, &outputBuffer)
        guard status == kCVReturnSuccess, let outputBuffer else {
            AppLogger.error("Could not allocate output pixel buffer: \(status)", category: "RTSP")
            return nil
        }

        let image = CIImage(cvPixelBuffer: inputBuffer)
        let targetAspectRatio = CGFloat(videoWidth) / CGFloat(videoHeight)
        var cropRect = image.extent
        if cropRect.width / cropRect.height > targetAspectRatio {
            cropRect.size.width = cropRect.height * targetAspectRatio
            cropRect.origin.x = image.extent.midX - cropRect.width / 2
        } else {
            cropRect.size.height = cropRect.width / targetAspectRatio
            cropRect.origin.y = image.extent.midY - cropRect.height / 2
        }

        let scaleX = CGFloat(videoWidth) / cropRect.width
        let scaleY = CGFloat(videoHeight) / cropRect.height
        let outputImage = image
            .cropped(to: cropRect)
            .transformed(by: CGAffineTransform(
                translationX: -cropRect.minX,
                y: -cropRect.minY
            ))
            .transformed(by: CGAffineTransform(scaleX: scaleX, y: scaleY))
        imageContext.render(
            outputImage,
            to: outputBuffer,
            bounds: CGRect(x: 0, y: 0, width: videoWidth, height: videoHeight),
            colorSpace: CGColorSpaceCreateDeviceRGB()
        )
        return outputBuffer
    }

    private func configureEncoder(width: Int, height: Int) {
        if let compressionSession {
            VTCompressionSessionCompleteFrames(compressionSession, untilPresentationTimeStamp: .invalid)
            VTCompressionSessionInvalidate(compressionSession)
            self.compressionSession = nil
            if let refcon = compressionSessionRefcon {
                Unmanaged<RTSPServer>.fromOpaque(refcon).release()
                compressionSessionRefcon = nil
            }
        }
        videoWidth = width
        videoHeight = height
        networkQueue.async {
            self.sps = nil
            self.pps = nil
        }

        var newSession: VTCompressionSession?
        let refcon = Unmanaged.passRetained(self).toOpaque()
        let status = VTCompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            width: Int32(width),
            height: Int32(height),
            codecType: kCMVideoCodecType_H264,
            encoderSpecification: nil,
            imageBufferAttributes: nil,
            compressedDataAllocator: nil,
            outputCallback: rtspCompressionOutputCallback,
            refcon: refcon,
            compressionSessionOut: &newSession
        )
        guard status == noErr, let newSession else {
            Unmanaged<RTSPServer>.fromOpaque(refcon).release()
            AppLogger.error("Could not create H.264 encoder: \(status)", category: "RTSP")
            return
        }
        compressionSession = newSession
        compressionSessionRefcon = refcon
        let settings = currentSettings()
        VTSessionSetProperty(newSession, key: kVTCompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        VTSessionSetProperty(
            newSession,
            key: kVTCompressionPropertyKey_AverageBitRate,
            value: NSNumber(value: settings.bitrate)
        )
        VTSessionSetProperty(
            newSession,
            key: kVTCompressionPropertyKey_ExpectedFrameRate,
            value: NSNumber(value: settings.frameRate)
        )
        VTSessionSetProperty(
            newSession,
            key: kVTCompressionPropertyKey_MaxKeyFrameInterval,
            value: NSNumber(value: settings.frameRate)
        )
        let prepareStatus = VTCompressionSessionPrepareToEncodeFrames(newSession)
        if prepareStatus != noErr {
            AppLogger.error("Could not prepare H.264 encoder: \(prepareStatus)", category: "RTSP")
        } else {
            AppLogger.info(
                "H.264 encoder configured: \(width)x\(height) at \(settings.frameRate) FPS, \(settings.bitrate) bps",
                category: "RTSP"
            )
        }
    }

    private func requestKeyFrame() {
        encoderQueue.async { [weak self] in
            guard let self, self.compressionSession != nil else { return }
            self.forceKeyFrameOnNextInput = true
        }
    }

    private var forceKeyFrameOnNextInput = false

    private func encodeNextFrame(
        _ pixelBuffer: CVPixelBuffer,
        presentationTime: CMTime,
        duration: CMTime,
        session: VTCompressionSession
    ) {
        var flags = VTEncodeInfoFlags()
        let properties: CFDictionary? = forceKeyFrameOnNextInput
            ? [kVTEncodeFrameOptionKey_ForceKeyFrame as String: true] as CFDictionary
            : nil
        forceKeyFrameOnNextInput = false
        let status = VTCompressionSessionEncodeFrame(
            session,
            imageBuffer: pixelBuffer,
            presentationTimeStamp: presentationTime,
            duration: duration,
            frameProperties: properties,
            sourceFrameRefcon: nil,
            infoFlagsOut: &flags
        )
        if status != noErr {
            AppLogger.error("H.264 encoding failed: \(status)", category: "RTSP")
        }
    }

    fileprivate func handleEncodedFrame(
        status: OSStatus,
        sampleBuffer: CMSampleBuffer?
    ) {
        guard status == noErr else {
            AppLogger.error("VideoToolbox returned an encoding error: \(status)", category: "RTSP")
            return
        }
        guard let sampleBuffer,
              CMSampleBufferDataIsReady(sampleBuffer),
              let blockBuffer = CMSampleBufferGetDataBuffer(sampleBuffer) else { return }

        let formatDescription = CMSampleBufferGetFormatDescription(sampleBuffer)
        var parameterSetCount = 0
        var nalHeaderLength: Int32 = 0
        var parameterSets: [Data] = []
        if let formatDescription {
            for index in 0 ..< 2 {
                var pointer: UnsafePointer<UInt8>?
                var size = 0
                let result = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                    formatDescription,
                    parameterSetIndex: index,
                    parameterSetPointerOut: &pointer,
                    parameterSetSizeOut: &size,
                    parameterSetCountOut: &parameterSetCount,
                    nalUnitHeaderLengthOut: &nalHeaderLength
                )
                if result == noErr, let pointer, size > 0 {
                    parameterSets.append(Data(bytes: pointer, count: size))
                }
            }
        }
        let lengthFieldBytes = Int(nalHeaderLength)

        let totalLength = CMBlockBufferGetDataLength(blockBuffer)
        guard totalLength > 0 else { return }
        var encodedData = Data(count: totalLength)
        let copyStatus: OSStatus = encodedData.withUnsafeMutableBytes {
            (destination: UnsafeMutableRawBufferPointer) -> OSStatus in
            guard let baseAddress = destination.baseAddress else {
                return kCMBlockBufferBadPointerParameterErr
            }
            return CMBlockBufferCopyDataBytes(
                blockBuffer,
                atOffset: 0,
                dataLength: totalLength,
                destination: baseAddress
            )
        }
        guard copyStatus == kCMBlockBufferNoErr else {
            AppLogger.error("Could not read encoded H.264 frame: \(copyStatus)", category: "RTSP")
            return
        }
        let timestamp = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        let isKeyFrame = Self.isKeyFrame(sampleBuffer)

        networkQueue.async { [weak self] in
            guard let self else { return }
            if parameterSets.count >= 2 {
                self.sps = parameterSets[0]
                self.pps = parameterSets[1]
            }
            if (1 ... 4).contains(lengthFieldBytes) {
                self.nalLengthFieldBytes = lengthFieldBytes
            }
            self.encodedFrameCount += 1
            self.statisticsLock.lock()
            self.encodedFramesForStatistics += 1
            self.statisticsLock.unlock()
            if self.encodedFrameCount == 1 || self.encodedFrameCount % 150 == 0 {
                AppLogger.info(
                    "RTSP H.264 frame=\(self.encodedFrameCount) bytes=\(encodedData.count) keyframe=\(isKeyFrame)",
                    category: "RTSP"
                )
            }
            guard self.isPlaying, self.transport != nil else { return }
            self.sendAccessUnit(encodedData, timestamp: timestamp)
        }
    }

    private func sendAccessUnit(_ data: Data, timestamp: CMTime) {
        var nals: [Data] = []
        var offset = 0
        let lengthFieldBytes = nalLengthFieldBytes
        while offset + lengthFieldBytes <= data.count {
            var nalLength = 0
            for byte in data[offset ..< offset + lengthFieldBytes] {
                nalLength = (nalLength << 8) | Int(byte)
            }
            offset += lengthFieldBytes
            guard nalLength > 0, offset + nalLength <= data.count else { return }
            nals.append(data.subdata(in: offset ..< offset + nalLength))
            offset += nalLength
        }
        guard !nals.isEmpty, offset == data.count else {
            AppLogger.warning("Dropping malformed H.264 access unit", category: "RTSP")
            return
        }

        let scaledTimestamp = CMTimeConvertScale(timestamp, timescale: 90_000, method: .default).value
        let rtpTimestamp = UInt32(truncatingIfNeeded: scaledTimestamp)
        lastRtpTimestamp = rtpTimestamp
        var rtpPackets: [Data] = []
        for (index, nal) in nals.enumerated() {
            let isLastNal = index == nals.count - 1
            rtpPackets.append(contentsOf: packetize(nal, timestamp: rtpTimestamp, markerOnLast: isLastNal))
        }

        switch transport {
        case .tcp(let channel):
            sendInterleavedRTPPackets(rtpPackets, channel: channel)
        case .udp:
            for packet in rtpPackets {
                sendRTPPacket(packet)
            }
        case .none:
            break
        }
    }

    private func packetize(_ nal: Data, timestamp: UInt32, markerOnLast: Bool) -> [Data] {
        let maximumPayload = 1_200
        if nal.count <= maximumPayload {
            return [makeRTPPacket(payload: nal, timestamp: timestamp, marker: markerOnLast)]
        }
        guard let nalHeader = nal.first else { return [] }

        let fragmentPayloadSize = maximumPayload - 2
        let nalPayload = Data(nal.dropFirst())
        var packets: [Data] = []
        var offset = 0
        while offset < nalPayload.count {
            let end = min(offset + fragmentPayloadSize, nalPayload.count)
            let fragmentIndicator = (nalHeader & 0xe0) | 28
            var fragmentHeader = nalHeader & 0x1f
            if offset == 0 {
                fragmentHeader |= 0x80
            }
            if end == nalPayload.count {
                fragmentHeader |= 0x40
            }
            var payload = Data([fragmentIndicator, fragmentHeader])
            payload.append(nalPayload[offset ..< end])
            packets.append(makeRTPPacket(
                payload: payload,
                timestamp: timestamp,
                marker: markerOnLast && end == nalPayload.count
            ))
            offset = end
        }
        return packets
    }

    private func makeRTPPacket(payload: Data, timestamp: UInt32, marker: Bool) -> Data {
        let sequence = rtpSequence
        rtpSequence &+= 1
        var packet = Data([
            0x80,
            (marker ? 0x80 : 0x00) | 96,
            UInt8(truncatingIfNeeded: sequence >> 8),
            UInt8(truncatingIfNeeded: sequence),
            UInt8(truncatingIfNeeded: timestamp >> 24),
            UInt8(truncatingIfNeeded: timestamp >> 16),
            UInt8(truncatingIfNeeded: timestamp >> 8),
            UInt8(truncatingIfNeeded: timestamp),
            UInt8(truncatingIfNeeded: ssrc >> 24),
            UInt8(truncatingIfNeeded: ssrc >> 16),
            UInt8(truncatingIfNeeded: ssrc >> 8),
            UInt8(truncatingIfNeeded: ssrc)
        ])
        packet.append(payload)
        return packet
    }

    private func sendRTPPacket(_ packet: Data) {
        switch transport {
        case .tcp:
            return
        case .udp:
            guard let udpSender else { return }
            udpSender.send(content: packet, completion: .contentProcessed { [weak self] error in
                if let error {
                    self?.updateStatus("RTP UDP send failed: \(error.localizedDescription)")
                }
            })
        case .none:
            return
        }
        recordSentRTPPacket(packet)
    }

    private func sendInterleavedRTPPackets(_ packets: [Data], channel: UInt8) {
        guard let client, !packets.isEmpty else { return }
        var framed = Data()
        for packet in packets {
            guard packet.count <= Int(UInt16.max) else {
                AppLogger.error("RTP TCP packet exceeds the interleaved frame limit", category: "RTSP")
                return
            }
            framed.append(contentsOf: [
                0x24,
                channel,
                UInt8(packet.count >> 8),
                UInt8(truncatingIfNeeded: packet.count)
            ])
            framed.append(packet)
        }

        client.send(content: framed, completion: .contentProcessed { [weak self] error in
            if let error {
                self?.updateStatus("RTP TCP send failed: \(error.localizedDescription)")
                AppLogger.error("RTP TCP send failed: \(error.localizedDescription)", category: "RTSP")
            }
        })
        for packet in packets {
            recordSentRTPPacket(packet)
        }
    }

    private func recordSentRTPPacket(_ packet: Data) {
        rtpPacketCount += 1
        let payloadSize = UInt64(max(0, packet.count - 12))
        rtpOctetCount += payloadSize
        statisticsLock.lock()
        sentBytesForStatistics += UInt64(packet.count)
        statisticsLock.unlock()
        if rtpPacketCount == 1 || rtpPacketCount % 1_500 == 0 {
            AppLogger.info("RTSP RTP packets sent=\(rtpPacketCount)", category: "RTSP")
        }
    }

    private func updateStatus(_ value: String) {
        DispatchQueue.main.async {
            self.status = value
        }
    }

    private func updateClientConnection(_ connected: Bool) {
        DispatchQueue.main.async {
            self.isClientConnected = connected
        }
    }

    private func setRunning(_ running: Bool, status: String) {
        DispatchQueue.main.async {
            self.isRunning = running
            self.status = status
        }
    }

    private static func isKeyFrame(_ sampleBuffer: CMSampleBuffer) -> Bool {
        guard let attachments = CMSampleBufferGetSampleAttachmentsArray(
            sampleBuffer,
            createIfNecessary: false
        ) as? [[CFString: Any]],
        let first = attachments.first else { return false }
        return (first[kCMSampleAttachmentKey_NotSync] as? Bool) != true
    }

    private static func localIPv4Address() -> String? {
        wifiIPv4BroadcastConfiguration()?.localAddressText
    }

    private static func wifiIPv4BroadcastConfiguration() -> IPv4BroadcastConfiguration? {
        var interfaceList: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&interfaceList) == 0, let firstInterface = interfaceList else {
            return nil
        }
        defer { freeifaddrs(interfaceList) }

        var interface = firstInterface
        while true {
            let item = interface.pointee
            if let address = item.ifa_addr,
               address.pointee.sa_family == UInt8(AF_INET),
               let name = String(validatingUTF8: item.ifa_name),
               name == "en0",
               item.ifa_flags & UInt32(IFF_UP) != 0,
               item.ifa_flags & UInt32(IFF_LOOPBACK) == 0,
               let netmask = item.ifa_netmask,
               netmask.pointee.sa_family == UInt8(AF_INET) {
                let localAddress = address.withMemoryRebound(to: sockaddr_in.self, capacity: 1) {
                    $0.pointee.sin_addr
                }
                let subnetMask = netmask.withMemoryRebound(to: sockaddr_in.self, capacity: 1) {
                    $0.pointee.sin_addr
                }
                let broadcastAddress = in_addr(
                    s_addr: (localAddress.s_addr & subnetMask.s_addr) | ~subnetMask.s_addr
                )
                let interfaceIndex = if_nametoindex(name)
                guard interfaceIndex > 0,
                      let localAddressText = ipv4String(localAddress),
                      let broadcastAddressText = ipv4String(broadcastAddress) else {
                    return nil
                }
                return IPv4BroadcastConfiguration(
                    interfaceName: name,
                    interfaceIndex: Int32(interfaceIndex),
                    localAddress: localAddress,
                    broadcastAddress: broadcastAddress,
                    localAddressText: localAddressText,
                    broadcastAddressText: broadcastAddressText
                )
            }
            guard let next = item.ifa_next else { break }
            interface = next
        }
        return nil
    }

    private static func ipv4String(_ address: in_addr) -> String? {
        var address = address
        var buffer = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
        guard inet_ntop(AF_INET, &address, &buffer, socklen_t(INET_ADDRSTRLEN)) != nil else {
            return nil
        }
        return String(cString: buffer)
    }
}

private struct IPv4BroadcastConfiguration {
    let interfaceName: String
    let interfaceIndex: Int32
    let localAddress: in_addr
    let broadcastAddress: in_addr
    let localAddressText: String
    let broadcastAddressText: String
}

private enum RTPTransport {
    case tcp(channel: UInt8)
    case udp
}

private struct RTSPStatisticsMessage: Encodable {
    let type: String
    let captureFps: Double
    let encodeFps: Double
    let senderBitrateBps: Double
}

private func rtspCompressionOutputCallback(
    _ outputCallbackRefCon: UnsafeMutableRawPointer?,
    _ sourceFrameRefCon: UnsafeMutableRawPointer?,
    _ status: OSStatus,
    _ infoFlags: VTEncodeInfoFlags,
    _ sampleBuffer: CMSampleBuffer?
) {
    guard let outputCallbackRefCon else { return }
    Unmanaged<RTSPServer>.fromOpaque(outputCallbackRefCon)
        .takeUnretainedValue()
        .handleEncodedFrame(status: status, sampleBuffer: sampleBuffer)
}
