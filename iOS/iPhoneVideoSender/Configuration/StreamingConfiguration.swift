import Foundation

struct StreamingConfiguration {
    static let minimumBitrate = 2_000_000
    static let maximumBitrate = 30_000_000
    static let defaultBitrate = 15_000_000
    static let supportedFrameRates = [30, 60, 120]

    var resolution: VideoResolution = .fullHD
    var frameRate = 60
    var bitrate = defaultBitrate
    var camera: CameraPosition = .back
}
