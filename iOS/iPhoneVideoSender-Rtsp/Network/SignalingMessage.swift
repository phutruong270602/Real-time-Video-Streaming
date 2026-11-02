import Foundation

enum SignalingMessageType: String, Codable {
    case offer = "OFFER"
    case answer = "ANSWER"
    case iceCandidate = "ICE_CANDIDATE"
    case status = "STATUS"
    case start = "START"
    case startRequest = "START_REQUEST"
    case stop = "STOP"
    case setBitrate = "SET_BITRATE"
    case setFPS = "SET_FPS"
    case setResolution = "SET_RESOLUTION"
    case setCamera = "SET_CAMERA"
    case capabilities = "CAPABILITIES"
    case configApplied = "CONFIG_APPLIED"
    case stats = "STATS"
    case error = "ERROR"
}

enum SignalingValue: Codable, Equatable {
    case integer(Int)
    case string(String)

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if let integer = try? container.decode(Int.self) {
            self = .integer(integer)
        } else {
            self = .string(try container.decode(String.self))
        }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .integer(let value):
            try container.encode(value)
        case .string(let value):
            try container.encode(value)
        }
    }

    var integerValue: Int? {
        guard case .integer(let value) = self else { return nil }
        return value
    }

    var stringValue: String? {
        guard case .string(let value) = self else { return nil }
        return value
    }
}

struct SignalingMessage: Codable {
    let type: SignalingMessageType
    var sdp: String?
    var candidate: String?
    var sdpMid: String?
    var sdpMLineIndex: Int32?
    var state: String?
    var value: SignalingValue?
    var fps: Int?
    var width: Int?
    var height: Int?
    var camera: String?
    var resolutions: [ResolutionCapability]?
    var cameras: [String]?
    var captureFPS: Double?
    var encodeFPS: Double?
    var bitrate: Int?
    var packetsLost: Int?
    var rtt: Double?
    var code: String?
    var message: String?

    init(
        type: SignalingMessageType,
        sdp: String? = nil,
        candidate: String? = nil,
        sdpMid: String? = nil,
        sdpMLineIndex: Int32? = nil,
        state: String? = nil,
        value: SignalingValue? = nil,
        fps: Int? = nil,
        width: Int? = nil,
        height: Int? = nil,
        camera: String? = nil,
        resolutions: [ResolutionCapability]? = nil,
        cameras: [String]? = nil,
        captureFPS: Double? = nil,
        encodeFPS: Double? = nil,
        bitrate: Int? = nil,
        packetsLost: Int? = nil,
        rtt: Double? = nil,
        code: String? = nil,
        message: String? = nil
    ) {
        self.type = type
        self.sdp = sdp
        self.candidate = candidate
        self.sdpMid = sdpMid
        self.sdpMLineIndex = sdpMLineIndex
        self.state = state
        self.value = value
        self.fps = fps
        self.width = width
        self.height = height
        self.camera = camera
        self.resolutions = resolutions
        self.cameras = cameras
        self.captureFPS = captureFPS
        self.encodeFPS = encodeFPS
        self.bitrate = bitrate
        self.packetsLost = packetsLost
        self.rtt = rtt
        self.code = code
        self.message = message
    }
}

struct ResolutionCapability: Codable {
    let width: Int
    let height: Int
    let fps: [Int]
    let cameras: [String]
}

enum SignalingState: Equatable {
    case disconnected
    case connecting
    case connected
    case failed(String)

    var title: String {
        switch self {
        case .disconnected: "Disconnected"
        case .connecting: "Connecting"
        case .connected: "Connected"
        case .failed(let reason): reason
        }
    }
}
