import AVFoundation
import Combine
import CoreMedia
import Foundation
import QuartzCore

final class CameraManager: NSObject, ObservableObject, @unchecked Sendable {
    @Published private(set) var permissionDenied = false
    @Published private(set) var isRunning = false
    @Published private(set) var status = "Requesting camera access"
    @Published private(set) var captureFPS = 0.0
    @Published private(set) var lastFrameTimestamp: String?
    @Published private(set) var availableCameras: [CameraPosition] = []
    @Published private(set) var capabilities: [CameraFormatCapability] = []
    @Published private(set) var capabilitiesByCamera: [CameraPosition: [CameraFormatCapability]] = [:]
    @Published private(set) var selectedResolution: VideoResolution = .fullHD
    @Published private(set) var selectedFPS = 60
    @Published private(set) var selectedCamera: CameraPosition = .back

    var onFrame: ((CMSampleBuffer) -> Void)?
    var onConfigurationApplied: (() -> Void)?
    var onConfigurationError: ((String) -> Void)?

    let session = AVCaptureSession()

    private let sessionQueue = DispatchQueue(label: "video.sender.camera.session")
    private let frameQueue = DispatchQueue(label: "video.sender.camera.frames")
    private let videoOutput = AVCaptureVideoDataOutput()
    private var currentInput: AVCaptureDeviceInput?
    private var frameCount = 0
    private var fpsWindowStart = CACurrentMediaTime()

    override init() {
        super.init()
        videoOutput.alwaysDiscardsLateVideoFrames = true
        videoOutput.videoSettings = [
            kCVPixelBufferPixelFormatTypeKey as String:
                kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        ]
        videoOutput.setSampleBufferDelegate(self, queue: frameQueue)
    }

    func start() async {
        AppLogger.info("Starting camera", category: "Camera")
        let authorized = await requestCameraAccess()
        guard authorized else {
            AppLogger.error("Camera permission is not available", category: "Camera")
            updateOnMain {
                self.permissionDenied = true
                self.status = "Camera permission is required"
            }
            return
        }

        updateOnMain {
            self.permissionDenied = false
            self.status = "Starting camera"
        }

        await withCheckedContinuation { continuation in
            sessionQueue.async { [weak self] in
                guard let self else {
                    continuation.resume()
                    return
                }
                self.configureAndStart {
                    continuation.resume()
                }
            }
        }
    }

    func stop() {
        sessionQueue.async { [weak self] in
            guard let self, self.session.isRunning else { return }
            self.session.stopRunning()
            self.updateOnMain {
                self.isRunning = false
                self.status = "Camera stopped"
            }
        }
    }

    func dismissPermissionAlert() {
        permissionDenied = false
    }

    func setResolution(_ resolution: VideoResolution) {
        apply(resolution: resolution, fps: selectedFPS, camera: selectedCamera)
    }

    func setFrameRate(_ fps: Int) {
        apply(resolution: selectedResolution, fps: fps, camera: selectedCamera)
    }

    func setCamera(_ camera: CameraPosition) {
        apply(resolution: selectedResolution, fps: selectedFPS, camera: camera)
    }

    var availableResolutions: [VideoResolution] {
        VideoResolution.allCases.filter { resolution in
            capabilities.contains {
                $0.resolution == resolution && $0.supports(frameRate: selectedFPS)
            }
        }
    }

    var availableFrameRates: [Int] {
        StreamingConfiguration.supportedFrameRates.filter { fps in
            capabilities.contains {
                $0.resolution == selectedResolution && $0.supports(frameRate: fps)
            }
        }
    }

    private func requestCameraAccess() async -> Bool {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized:
            return true
        case .notDetermined:
            return await withCheckedContinuation { continuation in
                AVCaptureDevice.requestAccess(for: .video) { granted in
                    continuation.resume(returning: granted)
                }
            }
        case .denied, .restricted:
            return false
        @unknown default:
            return false
        }
    }

    private func configureAndStart(completion: @escaping () -> Void) {
        guard !session.isRunning else {
            completion()
            return
        }

        do {
            let discoveredCameras = supportedCameras()
            guard !discoveredCameras.isEmpty else {
                throw CameraError.noCamera
            }
            let capabilitiesByCamera = try Dictionary(
                uniqueKeysWithValues: discoveredCameras.map { position in
                    let discoveredDevice = try cameraDevice(for: position)
                    logFormats(for: discoveredDevice)
                    return (position, formatCapabilities(for: discoveredDevice))
                }
            )

            let initialCamera = discoveredCameras.contains(.back)
                ? CameraPosition.back
                : discoveredCameras[0]
            let device = try cameraDevice(for: initialCamera)
            let modes = capabilitiesByCamera[initialCamera] ?? []

            let initialResolution: VideoResolution
            if modes.contains(where: { $0.resolution == .fullHD }) {
                initialResolution = .fullHD
            } else if modes.contains(where: { $0.resolution == .hd720 }) {
                initialResolution = .hd720
            } else {
                throw CameraError.noSupportedFormat
            }

            let rates = supportedFrameRates(for: initialResolution, in: modes)
            let initialFPS = rates.contains(60) ? 60 : (rates.first ?? 30)

            try configureSession(
                device: device,
                resolution: initialResolution,
                fps: initialFPS
            )
            session.startRunning()
            AppLogger.info(
                "Camera started: \(initialCamera.rawValue), \(initialResolution.rawValue), \(initialFPS) FPS",
                category: "Camera"
            )

            updateOnMain {
                self.availableCameras = discoveredCameras
                self.capabilitiesByCamera = capabilitiesByCamera
                self.capabilities = modes
                self.selectedCamera = initialCamera
                self.selectedResolution = initialResolution
                self.selectedFPS = initialFPS
                self.isRunning = true
                self.status = "Camera running"
                self.onConfigurationApplied?()
                completion()
            }
        } catch {
            AppLogger.error("Camera startup failed: \(error.localizedDescription)", category: "Camera")
            updateOnMain {
                self.isRunning = false
                self.status = error.localizedDescription
                self.onConfigurationError?(error.localizedDescription)
                completion()
            }
        }
    }

    private func apply(
        resolution: VideoResolution,
        fps: Int,
        camera: CameraPosition
    ) {
        sessionQueue.async { [weak self] in
            guard let self else { return }

            do {
                let device = try self.cameraDevice(for: camera)
                let modes = self.formatCapabilities(for: device)
                guard modes.contains(where: {
                    $0.resolution == resolution && $0.supports(frameRate: fps)
                }) else {
                    throw CameraError.unsupportedMode(
                        resolution: resolution,
                        fps: fps,
                        camera: camera
                    )
                }

                try self.configureSession(device: device, resolution: resolution, fps: fps)
                AppLogger.info(
                    "Camera configuration applied: \(camera.rawValue), \(resolution.rawValue), \(fps) FPS",
                    category: "Camera"
                )
                self.updateOnMain {
                    self.availableCameras = self.supportedCameras()
                    self.capabilitiesByCamera[camera] = modes
                    self.capabilities = modes
                    self.selectedCamera = camera
                    self.selectedResolution = resolution
                    self.selectedFPS = fps
                    self.status = "Camera running"
                    self.onConfigurationApplied?()
                }
            } catch {
                AppLogger.error(
                    "Camera configuration failed: \(error.localizedDescription)",
                    category: "Camera"
                )
                self.updateOnMain {
                    self.status = error.localizedDescription
                    self.onConfigurationError?(error.localizedDescription)
                }
            }
        }
    }

    private func configureSession(
        device: AVCaptureDevice,
        resolution: VideoResolution,
        fps: Int
    ) throws {
        guard let format = device.formats.first(where: { format in
            let dimensions = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
            return dimensions.width == resolution.width
                && dimensions.height == resolution.height
                && format.videoSupportedFrameRateRanges.contains {
                    Double(fps) >= $0.minFrameRate && Double(fps) <= $0.maxFrameRate
                }
        }) else {
            throw CameraError.unsupportedMode(
                resolution: resolution,
                fps: fps,
                camera: CameraPosition(rawValue: device.position == .front ? "front" : "back") ?? .back
            )
        }

        let newInput = try AVCaptureDeviceInput(device: device)
        session.beginConfiguration()
        session.sessionPreset = .inputPriority

        if let currentInput {
            session.removeInput(currentInput)
        }

        guard session.canAddInput(newInput) else {
            if let currentInput, session.canAddInput(currentInput) {
                session.addInput(currentInput)
            }
            session.commitConfiguration()
            throw CameraError.cannotAddCameraInput
        }
        session.addInput(newInput)

        if !session.outputs.contains(videoOutput) {
            guard session.canAddOutput(videoOutput) else {
                session.removeInput(newInput)
                if let currentInput, session.canAddInput(currentInput) {
                    session.addInput(currentInput)
                }
                session.commitConfiguration()
                throw CameraError.cannotAddVideoOutput
            }
            session.addOutput(videoOutput)
        }

        do {
            try device.lockForConfiguration()
            device.activeFormat = format
            let frameDuration = CMTime(value: 1, timescale: CMTimeScale(fps))
            device.activeVideoMinFrameDuration = frameDuration
            device.activeVideoMaxFrameDuration = frameDuration
            device.unlockForConfiguration()
        } catch {
            session.removeInput(newInput)
            if let currentInput, session.canAddInput(currentInput) {
                session.addInput(currentInput)
            }
            session.commitConfiguration()
            throw error
        }

        currentInput = newInput
        session.commitConfiguration()
    }

    private func cameraDevice(for position: CameraPosition) throws -> AVCaptureDevice {
        let discovery = AVCaptureDevice.DiscoverySession(
            deviceTypes: [.builtInWideAngleCamera, .builtInUltraWideCamera, .builtInTelephotoCamera],
            mediaType: .video,
            position: position.avPosition
        )
        guard let device = discovery.devices.first else {
            throw CameraError.cameraUnavailable(position)
        }
        return device
    }

    private func supportedCameras() -> [CameraPosition] {
        CameraPosition.allCases.filter { position in
            let discovery = AVCaptureDevice.DiscoverySession(
                deviceTypes: [.builtInWideAngleCamera, .builtInUltraWideCamera, .builtInTelephotoCamera],
                mediaType: .video,
                position: position.avPosition
            )
            return !discovery.devices.isEmpty
        }
    }

    private func formatCapabilities(for device: AVCaptureDevice) -> [CameraFormatCapability] {
        device.formats.flatMap { format -> [CameraFormatCapability] in
            let dimensions = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
            guard let resolution = VideoResolution(dimensions: dimensions) else { return [] }
            return format.videoSupportedFrameRateRanges.map {
                CameraFormatCapability(
                    resolution: resolution,
                    minimumFPS: $0.minFrameRate,
                    maximumFPS: $0.maxFrameRate
                )
            }
        }
    }

    private func supportedFrameRates(
        for resolution: VideoResolution,
        in modes: [CameraFormatCapability]
    ) -> [Int] {
        StreamingConfiguration.supportedFrameRates.filter { fps in
            modes.contains { $0.resolution == resolution && $0.supports(frameRate: fps) }
        }
    }

    private func logFormats(for device: AVCaptureDevice) {
        for format in device.formats {
            let dimensions = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
            let pixelFormat = CMFormatDescriptionGetMediaSubType(format.formatDescription)
            let frameRates = format.videoSupportedFrameRateRanges
                .map { "\(Int($0.minFrameRate))-\(Int($0.maxFrameRate))" }
                .joined(separator: ",")
            AppLogger.info(
                "\(device.position.rawValue) \(dimensions.width)x\(dimensions.height), pixel format 0x\(String(pixelFormat, radix: 16)), fps \(frameRates)",
                category: "Camera"
            )
        }
    }

    private func updateOnMain(_ update: @escaping () -> Void) {
        DispatchQueue.main.async(execute: update)
    }
}

extension CameraManager: AVCaptureVideoDataOutputSampleBufferDelegate {
    func captureOutput(
        _ output: AVCaptureOutput,
        didOutput sampleBuffer: CMSampleBuffer,
        from connection: AVCaptureConnection
    ) {
        onFrame?(sampleBuffer)
        frameCount += 1
        let now = CACurrentMediaTime()
        let presentationTime = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)

        if now - fpsWindowStart >= 1 {
            let elapsed = now - fpsWindowStart
            let measuredFPS = Double(frameCount) / elapsed
            frameCount = 0
            fpsWindowStart = now
            updateOnMain {
                self.captureFPS = measuredFPS
                self.lastFrameTimestamp = String(format: "PTS %.3f s", presentationTime.seconds)
            }
        }
    }
}

private enum CameraError: LocalizedError {
    case noCamera
    case noSupportedFormat
    case cameraUnavailable(CameraPosition)
    case cannotAddCameraInput
    case cannotAddVideoOutput
    case unsupportedMode(resolution: VideoResolution, fps: Int, camera: CameraPosition)

    var errorDescription: String? {
        switch self {
        case .noCamera:
            "No camera is available on this device."
        case .noSupportedFormat:
            "No supported 720p or 1080p camera format was found."
        case .cameraUnavailable(let position):
            "The \(position.rawValue) camera is unavailable."
        case .cannotAddCameraInput:
            "The camera input could not be added to the capture session."
        case .cannotAddVideoOutput:
            "The video output could not be added to the capture session."
        case .unsupportedMode(let resolution, let fps, let camera):
            "\(camera.rawValue.capitalized) camera does not support \(resolution.rawValue) at \(fps) FPS."
        }
    }
}
