import AVFoundation
import Combine
import CoreMedia
import Foundation
import WebRTC

final class WebRTCManager: NSObject, ObservableObject, @unchecked Sendable {
    @Published private(set) var status = "Not streaming"
    @Published private(set) var isConnected = false
    @Published private(set) var statistics = StreamingStatistics(
        encodeFPS: 0,
        bitrate: 0,
        packetsLost: 0,
        roundTripTime: 0
    )

    var onLocalCandidate: ((RTCIceCandidate) -> Void)?
    var onStatus: ((String) -> Void)?
    var onStatistics: ((StreamingStatistics) -> Void)?

    private static let factory: RTCPeerConnectionFactory = {
        RTCInitializeSSL()
        return RTCPeerConnectionFactory(
            encoderFactory: RTCVideoEncoderFactoryH264(),
            decoderFactory: RTCDefaultVideoDecoderFactory()
        )
    }()

    private var peerConnection: RTCPeerConnection?
    private var videoSource: RTCVideoSource?
    private var videoTrack: RTCVideoTrack?
    private var frameCapturer: RTCVideoCapturer?
    private var videoSender: RTCRtpSender?
    private var statsTimer: Timer?
    private let statsCollector = StreamingStatisticsCollector()
    private let iceStateQueue = DispatchQueue(label: "video.sender.webrtc.ice-state")
    private weak var cameraManager: CameraManager?
    private var hasRemoteDescription = false
    private var pendingRemoteCandidates: [RemoteIceCandidate] = []
    private var bitrate = StreamingConfiguration.defaultBitrate
    private var frameRate = 60

    var isPrepared: Bool { peerConnection != nil }

    func prepare(
        cameraManager: CameraManager,
        bitrate: Int,
        frameRate: Int
    ) throws {
        AppLogger.info("Preparing WebRTC video stream", category: "WebRTC")
        close()
        self.cameraManager = cameraManager
        self.bitrate = min(
            max(bitrate, StreamingConfiguration.minimumBitrate),
            StreamingConfiguration.maximumBitrate
        )
        self.frameRate = frameRate

        let configuration = RTCConfiguration()
        configuration.iceServers = []
        configuration.sdpSemantics = .unifiedPlan
        configuration.bundlePolicy = .maxBundle
        let constraints = RTCMediaConstraints(mandatoryConstraints: nil, optionalConstraints: nil)

        guard let peerConnection = Self.factory.peerConnection(
            with: configuration,
            constraints: constraints,
            delegate: self
        ) else {
            throw WebRTCError.peerConnectionUnavailable
        }

        let source = Self.factory.videoSource()
        source.adaptOutputFormat(
            toWidth: cameraManager.selectedResolution.width,
            height: cameraManager.selectedResolution.height,
            fps: Int32(frameRate)
        )
        let capturer = RTCVideoCapturer(delegate: source)
        let track = Self.factory.videoTrack(with: source, trackId: "iphone-video")
        track.isEnabled = true
        guard let sender = peerConnection.add(track, streamIds: ["iphone-stream"]) else {
            peerConnection.close()
            throw WebRTCError.videoTrackUnavailable
        }

        self.peerConnection = peerConnection
        videoSource = source
        frameCapturer = capturer
        videoTrack = track
        videoSender = sender
        configureSender()
        cameraManager.onFrame = { [weak self] sampleBuffer in
            self?.capture(sampleBuffer)
        }
        DispatchQueue.main.async {
            self.status = "Waiting for Windows offer"
            self.onStatus?("connecting")
        }
        AppLogger.info(
            "WebRTC peer connection prepared at \(cameraManager.selectedResolution.rawValue), \(frameRate) FPS",
            category: "WebRTC"
        )
    }

    func handleOffer(_ sdp: String, completion: @escaping (Result<String, Error>) -> Void) {
        guard let peerConnection else {
            completion(.failure(WebRTCError.peerConnectionUnavailable))
            return
        }

        let offer = RTCSessionDescription(type: .offer, sdp: sdp)
        peerConnection.setRemoteDescription(offer) { [weak self] error in
            guard let self else { return }
            if let error {
                AppLogger.error("Could not apply remote offer: \(error.localizedDescription)", category: "WebRTC")
                DispatchQueue.main.async {
                    completion(.failure(error))
                }
                return
            }

            self.iceStateQueue.async {
                guard self.peerConnection === peerConnection else { return }
                self.hasRemoteDescription = true
                let pendingCandidates = self.pendingRemoteCandidates
                self.pendingRemoteCandidates.removeAll()
                for candidate in pendingCandidates {
                    self.applyRemoteCandidate(candidate, to: peerConnection)
                }

                let constraints = RTCMediaConstraints(mandatoryConstraints: nil, optionalConstraints: nil)
                peerConnection.answer(for: constraints) { answer, error in
                    guard let answer, error == nil else {
                        AppLogger.error(
                            "Could not create WebRTC answer: \(error?.localizedDescription ?? "unknown error")",
                            category: "WebRTC"
                        )
                        DispatchQueue.main.async {
                            completion(.failure(error ?? WebRTCError.answerUnavailable))
                        }
                        return
                    }
                    peerConnection.setLocalDescription(answer) { error in
                        DispatchQueue.main.async {
                            if let error {
                                AppLogger.error(
                                    "Could not set local WebRTC description: \(error.localizedDescription)",
                                    category: "WebRTC"
                                )
                                completion(.failure(error))
                            } else {
                                AppLogger.info("WebRTC answer created", category: "WebRTC")
                                completion(.success(answer.sdp))
                            }
                        }
                    }
                }
            }
        }
    }

    func addRemoteCandidate(
        sdp: String,
        sdpMid: String?,
        sdpMLineIndex: Int32
    ) {
        let candidate = RemoteIceCandidate(
            sdp: sdp,
            mid: sdpMid,
            lineIndex: sdpMLineIndex
        )
        iceStateQueue.async { [weak self] in
            guard let self, let peerConnection = self.peerConnection else { return }
            guard self.hasRemoteDescription else {
                self.pendingRemoteCandidates.append(candidate)
                return
            }
            self.applyRemoteCandidate(candidate, to: peerConnection)
        }
    }

    private func applyRemoteCandidate(_ candidate: RemoteIceCandidate, to peerConnection: RTCPeerConnection) {
        let rtcCandidate = RTCIceCandidate(
            sdp: candidate.sdp,
            sdpMLineIndex: candidate.lineIndex,
            sdpMid: candidate.mid
        )
        peerConnection.add(rtcCandidate) { [weak self] error in
            if let error {
                AppLogger.error("Could not add remote ICE candidate: \(error.localizedDescription)", category: "WebRTC")
                self?.updateStatus("Could not add ICE candidate: \(error.localizedDescription)")
            }
        }
    }

    func updateEncoding(bitrate: Int, frameRate: Int) {
        self.bitrate = min(
            max(bitrate, StreamingConfiguration.minimumBitrate),
            StreamingConfiguration.maximumBitrate
        )
        self.frameRate = frameRate
        configureSender()
    }

    func close() {
        iceStateQueue.sync {
            hasRemoteDescription = false
            pendingRemoteCandidates.removeAll()
        }
        statsTimer?.invalidate()
        statsTimer = nil
        cameraManager?.onFrame = nil
        cameraManager = nil
        hasRemoteDescription = false
        pendingRemoteCandidates.removeAll()
        peerConnection?.close()
        peerConnection = nil
        videoSender = nil
        videoTrack = nil
        videoSource = nil
        frameCapturer = nil
        statsCollector.reset()
        DispatchQueue.main.async {
            self.isConnected = false
            self.statistics = StreamingStatistics(
                encodeFPS: 0,
                bitrate: 0,
                packetsLost: 0,
                roundTripTime: 0
            )
            self.status = "Not streaming"
        }
    }

    private func capture(_ sampleBuffer: CMSampleBuffer) {
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer),
              let capturer = frameCapturer else {
            return
        }

        let frameBuffer = RTCCVPixelBuffer(pixelBuffer: pixelBuffer)
        let presentationTime = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        let timestamp = CMTimeConvertScale(
            presentationTime,
            timescale: 1_000_000_000,
            method: .default
        )
        let frame = RTCVideoFrame(
            buffer: frameBuffer,
            rotation: ._0,
            timeStampNs: timestamp.value
        )
        capturer.delegate?.capturer(capturer, didCapture: frame)
    }

    private func configureSender() {
        guard let videoSender else { return }
        let parameters = videoSender.parameters
        for encoding in parameters.encodings {
            encoding.maxBitrateBps = NSNumber(value: bitrate)
            encoding.maxFramerate = NSNumber(value: frameRate)
        }
        videoSender.parameters = parameters
        _ = peerConnection?.setBweMinBitrateBps(
            NSNumber(value: StreamingConfiguration.minimumBitrate),
            currentBitrateBps: NSNumber(value: bitrate),
            maxBitrateBps: NSNumber(value: StreamingConfiguration.maximumBitrate)
        )
    }

    private func startStatisticsTimer() {
        statsTimer?.invalidate()
        statsTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            self?.collectStatistics()
        }
    }

    private func collectStatistics() {
        peerConnection?.statistics { [weak self] report in
            guard let self else { return }
            let result = self.statsCollector.consume(report)
            DispatchQueue.main.async {
                self.statistics = result
                self.onStatistics?(result)
            }
        }
    }

    private func updateStatus(_ message: String) {
        DispatchQueue.main.async {
            self.status = message
            self.onStatus?(message)
        }
    }
}

extension WebRTCManager: RTCPeerConnectionDelegate {
    func peerConnection(_ peerConnection: RTCPeerConnection, didChange stateChanged: RTCSignalingState) {}

    func peerConnection(_ peerConnection: RTCPeerConnection, didAdd stream: RTCMediaStream) {}

    func peerConnection(_ peerConnection: RTCPeerConnection, didRemove stream: RTCMediaStream) {}

    func peerConnectionShouldNegotiate(_ peerConnection: RTCPeerConnection) {}

    func peerConnection(_ peerConnection: RTCPeerConnection, didChange stateChanged: RTCIceConnectionState) {
        switch stateChanged {
        case .connected, .completed:
            AppLogger.info("WebRTC ICE connection established", category: "WebRTC")
            DispatchQueue.main.async {
                self.isConnected = true
                self.status = "WebRTC connected"
                self.onStatus?("connected")
            }
            startStatisticsTimer()
        case .failed:
            AppLogger.error("WebRTC ICE connection failed", category: "WebRTC")
            updateStatus("WebRTC ICE connection failed")
            DispatchQueue.main.async {
                self.isConnected = false
                self.onStatus?("failed")
            }
        case .disconnected:
            AppLogger.warning("WebRTC ICE connection disconnected", category: "WebRTC")
            updateStatus("WebRTC disconnected")
            DispatchQueue.main.async {
                self.isConnected = false
                self.onStatus?("connecting")
            }
        case .new, .checking, .closed, .count:
            break
        @unknown default:
            updateStatus("WebRTC connection state is unknown")
        }
    }

    func peerConnection(_ peerConnection: RTCPeerConnection, didChange stateChanged: RTCIceGatheringState) {}

    func peerConnection(_ peerConnection: RTCPeerConnection, didGenerate candidate: RTCIceCandidate) {
        DispatchQueue.main.async {
            self.onLocalCandidate?(candidate)
        }
    }

    func peerConnection(_ peerConnection: RTCPeerConnection, didRemove candidates: [RTCIceCandidate]) {}

    func peerConnection(_ peerConnection: RTCPeerConnection, didOpen dataChannel: RTCDataChannel) {}
}

private enum WebRTCError: LocalizedError {
    case peerConnectionUnavailable
    case videoTrackUnavailable
    case answerUnavailable

    var errorDescription: String? {
        switch self {
        case .peerConnectionUnavailable:
            "Could not create the WebRTC peer connection."
        case .videoTrackUnavailable:
            "Could not add the camera video track to the peer connection."
        case .answerUnavailable:
            "Could not create a WebRTC answer for the Windows offer."
        }
    }
}

private struct RemoteIceCandidate {
    let sdp: String
    let mid: String?
    let lineIndex: Int32
}
