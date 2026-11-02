import AVFoundation

enum VideoResolution: String, CaseIterable, Identifiable {
    case hd720 = "1280 × 720"
    case fullHD = "1920 × 1080"

    var id: String { rawValue }

    var width: Int32 {
        switch self {
        case .hd720: 1280
        case .fullHD: 1920
        }
    }

    var height: Int32 {
        switch self {
        case .hd720: 720
        case .fullHD: 1080
        }
    }

    init?(dimensions: CMVideoDimensions) {
        self.init(
            rawValue: "\(dimensions.width) × \(dimensions.height)"
        )
    }
}

enum CameraPosition: String, CaseIterable, Identifiable {
    case back
    case front

    var id: String { rawValue }

    var avPosition: AVCaptureDevice.Position {
        switch self {
        case .back: .back
        case .front: .front
        }
    }
}

struct CameraFormatCapability: Hashable {
    let resolution: VideoResolution
    let minimumFPS: Double
    let maximumFPS: Double

    func supports(frameRate: Int) -> Bool {
        Double(frameRate) >= minimumFPS && Double(frameRate) <= maximumFPS
    }
}
