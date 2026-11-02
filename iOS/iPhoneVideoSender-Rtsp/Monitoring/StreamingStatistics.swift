import Foundation
import WebRTC

struct StreamingStatistics {
    let encodeFPS: Double
    let bitrate: Int
    let packetsLost: Int
    let roundTripTime: Double
}

final class StreamingStatisticsCollector {
    private var previousBytesSent: Int64?
    private var previousFramesEncoded: Int64?
    private var previousTimestamp: TimeInterval?

    func consume(_ report: RTCStatisticsReport) -> StreamingStatistics {
        let timestamp = report.timestamp_us / 1_000_000
        let outboundVideo = report.statistics.values.first {
            $0.type == "outbound-rtp"
                && ($0.values["kind"] as? String == "video"
                    || $0.values["mediaType"] as? String == "video")
        }
        let remoteInboundVideo = report.statistics.values.first {
            $0.type == "remote-inbound-rtp"
                && ($0.values["kind"] as? String == "video"
                    || $0.values["mediaType"] as? String == "video")
        }

        let bytesSent = (outboundVideo?.values["bytesSent"] as? NSNumber)?.int64Value
        let framesEncoded = (outboundVideo?.values["framesEncoded"] as? NSNumber)?.int64Value
        let packetsLost = (remoteInboundVideo?.values["packetsLost"] as? NSNumber)?.intValue ?? 0
        let roundTripTime = (remoteInboundVideo?.values["roundTripTime"] as? NSNumber)?.doubleValue ?? 0

        let elapsed = previousTimestamp.map { timestamp - $0 } ?? 0
        let bitrate: Int
        if let bytesSent, let previousBytesSent, elapsed > 0 {
            bitrate = Int(Double(max(0, bytesSent - previousBytesSent)) * 8 / elapsed)
        } else {
            bitrate = 0
        }

        let encodeFPS: Double
        if let framesEncoded, let previousFramesEncoded, elapsed > 0 {
            encodeFPS = Double(max(0, framesEncoded - previousFramesEncoded)) / elapsed
        } else {
            encodeFPS = 0
        }

        previousBytesSent = bytesSent
        previousFramesEncoded = framesEncoded
        previousTimestamp = timestamp
        return StreamingStatistics(
            encodeFPS: encodeFPS,
            bitrate: bitrate,
            packetsLost: packetsLost,
            roundTripTime: roundTripTime
        )
    }

    func reset() {
        previousBytesSent = nil
        previousFramesEncoded = nil
        previousTimestamp = nil
    }
}
