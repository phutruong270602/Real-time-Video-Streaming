# Technical Design Document
# iPhone to Windows – Real-time Video Streaming

**Version:** 1.0  
**Project type:** Prototype / Proof of Concept (PoC)  
**Primary protocol:** WebRTC  
**Transport:** UDP over Wi-Fi LAN

---

## 1. Project Overview

### 1.1. Objective

Xây dựng hệ thống truyền video trực tiếp từ camera iPhone sang máy tính Windows thông qua mạng Wi-Fi.

Hệ thống gồm hai ứng dụng:

1. **iPhone Video Sender:** Capture video từ camera iPhone và truyền đến Windows.
2. **Windows Video Receiver:** Nhận, giải mã, hiển thị video và cung cấp giao diện điều chỉnh các thông số streaming.

### 1.2. Technical Requirements

| Parameter | Target |
|---|---|
| Source | iPhone Camera |
| Receiver | Windows PC |
| Resolution | 1920 × 1080 |
| Target FPS | 120 FPS |
| Video Codec | H.264 |
| Encoding | Hardware accelerated |
| Protocol | WebRTC |
| Transport | UDP |
| Network | Wi-Fi 5 GHz / 6 GHz |
| Priority | Lowest possible latency |
| Connection | Local Network |

**Lưu ý:** 1080p120 là mục tiêu cần kiểm chứng trên phần cứng thực tế, không phải cấu hình được đảm bảo trên mọi iPhone.

---

## 2. Development Environment – IDE & Tools

### 2.1. iPhone Application

| Component | Technology |
|---|---|
| IDE | Xcode |
| Language | Swift |
| UI Framework | SwiftUI |
| Camera | AVFoundation |
| Video Encoder | VideoToolbox |
| Streaming | WebRTC iOS SDK |
| Build | Xcode / Swift Package Manager hoặc framework tích hợp |

**IDE đề xuất: Xcode**

Lý do:
- IDE chính thức để phát triển iOS.
- Debug trực tiếp trên iPhone.
- Quản lý provisioning profile và code signing.
- Theo dõi camera capture và performance.
- Tích hợp tốt với SwiftUI, AVFoundation và VideoToolbox.

### 2.2. Windows Application

| Component | Technology |
|---|---|
| IDE | Visual Studio |
| Language | C# |
| UI Framework | WPF |
| Runtime | .NET |
| Web Browser Engine | Microsoft Edge WebView2 |
| Streaming | WebRTC JavaScript API |
| Video Rendering | WebRTC / WebView2 |
| Signaling | WebSocket |

**IDE đề xuất: Visual Studio**

Lý do:
- Dễ thiết kế giao diện desktop.
- Có sẵn WPF Designer.
- Hỗ trợ Slider, Button, ComboBox, TextBlock.
- Debug C# thuận tiện.
- Có thể tích hợp WebView2 để sử dụng WebRTC engine của Chromium.

Ở phiên bản đầu tiên, mình đề xuất sử dụng WebRTC trong WebView2 thay vì tự tích hợp toàn bộ WebRTC Native C++ SDK vào ứng dụng Windows.

Cách này giảm đáng kể độ phức tạp của phần Windows.

### 2.3. Các công cụ bổ trợ

| Tool | Purpose |
|---|---|
| Git | Version control |
| Wireshark | Analyze network packets |
| VLC | Test video stream |
| FFmpeg | Video analysis |
| Visual Studio Code | Edit configuration / auxiliary files |
| Xcode Instruments | Performance profiling trên iPhone |

---

## 3. System Architecture

### 3.1. High-level Architecture

```text
                  Wi-Fi LAN
                     |
       +-------------+-------------+
       |                           |
       v                           v

+------------------+       +----------------------+
|      iPhone      |       |       Windows        |
|                  |       |                      |
|   Video Sender   |       |   Video Receiver     |
|                  |       |                      |
|  AVFoundation    |       |   WPF Application    |
|       |          |       |        |             |
|       v          |       |        v             |
|  VideoToolbox    |       |     WebView2         |
|       |          |       |        |             |
|       v          |       |        v             |
|   WebRTC SDK     | <---> |   WebRTC JavaScript  |
|                  |       |                      |
+------------------+       +----------------------+
```

### 3.2. Separate Control Plane and Media Plane

Hệ thống nên tách thành hai luồng:

**Media Plane**

Truyền video trực tiếp:

```text
iPhone Camera
     |
     v
AVFoundation
     |
     v
VideoToolbox
     |
     v
WebRTC
     |
     v
UDP / Wi-Fi
     |
     v
Windows WebRTC Receiver
     |
     v
Video Display
```

**Control Plane**

Điều khiển quá trình streaming:

```text
Windows WPF UI
      |
      v
WebSocket
      |
      v
iPhone Application
      |
      v
Update Streaming Configuration
```

Ví dụ Windows gửi yêu cầu:

```json
{
  "type": "SET_BITRATE",
  "value": 15000000
}
```

iPhone nhận lệnh và cập nhật bitrate encoder hoặc WebRTC sender phù hợp.

Không nên gửi video qua WebSocket. WebSocket chỉ nên dùng cho signaling và các lệnh điều khiển.

---

## 4. iPhone Video Sender Design

### 4.1. Main Modules

```text
iPhoneVideoSender/
|
+-- App/
|   +-- VideoSenderApp.swift
|   +-- ContentView.swift
|
+-- Camera/
|   +-- CameraManager.swift
|   +-- CameraConfiguration.swift
|
+-- Streaming/
|   +-- WebRTCManager.swift
|   +-- PeerConnectionManager.swift
|   +-- VideoTrackManager.swift
|
+-- Network/
|   +-- SignalingClient.swift
|   +-- WebSocketManager.swift
|
+-- Configuration/
|   +-- StreamingConfiguration.swift
|
+-- Monitoring/
    +-- StreamingStatistics.swift
```

### 4.2. Camera Configuration

Sử dụng AVFoundation để capture camera.

Các thông số cần hỗ trợ:

- Resolution: 1280 × 720 / 1920 × 1080.
- FPS: 30 / 60 / 120.
- Camera: Front / Back.
- Camera format.
- Frame timestamp.

Các API quan trọng:

```swift
AVCaptureSession
AVCaptureDevice
AVCaptureDevice.Format
AVCaptureVideoDataOutput
```

Quy trình:

1. Request camera permission.
2. Enumerate camera formats.
3. Kiểm tra resolution và FPS hỗ trợ.
4. Chọn format phù hợp.
5. Configure capture session.
6. Start camera capture.
7. Forward frames vào WebRTC video pipeline.

Cần kiểm tra `videoSupportedFrameRateRanges` trước khi cho phép chọn 120 FPS.

### 4.3. Streaming Configuration

Cấu hình đề xuất ban đầu:

| Parameter | Initial Value |
|---|---|
| Resolution | 1920 × 1080 |
| FPS | 60 |
| Codec | H.264 |
| Bitrate | 15 Mbps |
| Transport | UDP |
| Camera | Back Camera |

Sau khi xác nhận pipeline hoạt động ổn định ở 60 FPS, tiến hành thử 120 FPS.

---

## 5. Windows Video Receiver Design

### 5.1. Main Modules

```text
WindowsVideoReceiver/
|
+-- App/
|   +-- App.xaml
|   +-- MainWindow.xaml
|   +-- MainWindow.xaml.cs
|
+-- Streaming/
|   +-- WebRTCClient.js
|   +-- PeerConnectionManager.js
|
+-- Network/
|   +-- SignalingServer.cs
|   +-- WebSocketManager.cs
|   +-- BonjourServiceAdvertiser.cs
|
+-- Controls/
|   +-- StreamingControlViewModel.cs
|
+-- Monitoring/
|   +-- StreamingStatistics.cs
|
+-- Views/
    +-- VideoPlayerView.xaml
    +-- SettingsView.xaml
```

### 5.2. User Interface Design

Windows application nên chia thành 3 khu vực chính.

**A. Video Display**

- Hiển thị video từ iPhone.
- Full screen mode.
- Display resolution.
- Display FPS.
- Connection status.

**B. Streaming Configuration**

| Setting | Control |
|---|---|
| Resolution | ComboBox |
| FPS | ComboBox |
| Bitrate | Slider |
| Camera | ComboBox |
| Start | Button |
| Stop | Button |

Bitrate slider đề xuất:

- Minimum: 2 Mbps.
- Maximum: 30 Mbps.
- Default: 15 Mbps.

Các giá trị này là giới hạn thử nghiệm ban đầu, cần điều chỉnh theo khả năng mạng và encoder.

**C. Performance Monitoring**

Hiển thị:

| Metric | Description |
|---|---|
| Capture FPS | Camera capture frame rate |
| Encode FPS | Encoder output frame rate |
| Receive FPS | Frames received |
| Decode FPS | Frames decoded |
| Render FPS | Frames displayed |
| Bitrate | Actual transmission bitrate |
| Packet Loss | Lost packets |
| Jitter | Network jitter |
| RTT | Round-trip time |
| Latency | Estimated end-to-end delay |

---

## 6. WebRTC Connection Architecture

### 6.1. Signaling

Sử dụng WebSocket để trao đổi:

- SDP Offer.
- SDP Answer.
- ICE Candidates.
- Connection status.
- Streaming configuration commands.

#### Tìm Windows Receiver

Windows công bố WebSocket signaling server bằng Bonjour/mDNS (DNS-SD), service type `_wvr-signal._tcp` trên domain `local.`, port `8080`. iPhone duyệt dịch vụ này, cho người dùng chọn Windows Receiver rồi resolve host/port để kết nối WebSocket. Windows không chủ động tìm iPhone.

Ngoài Automatic Discovery, iPhone hỗ trợ Manual Connection bằng cách nhập IP và port của Windows. Cả hai cách đều kết nối tới cùng WebSocket signaling server; sau đó SDP/ICE được trao đổi qua WebSocket và video truyền bằng WebRTC. Nếu mDNS multicast không hoạt động trên mạng, dùng phương thức nhập IP thủ công.

Quy trình:

```text
Windows                 iPhone
   |                       |
   |------ WebSocket ------|
   |                       |
   |------ SDP Offer ----->|
   |<----- SDP Answer -----|
   |                       |
   |<---- ICE Candidates ->|
   |                       |
   |====== WebRTC P2P =====|
   |                       |
   |<------ Video ---------|
```

Trong cùng mạng LAN, ưu tiên kết nối trực tiếp bằng ICE host candidates.

Không cần TURN server trong trường hợp kết nối LAN đơn giản nếu hai thiết bị có thể kết nối trực tiếp.

### 6.2. Media Transport

WebRTC sử dụng:

- RTP/SRTP: Media transport.
- RTCP: Feedback và thống kê.
- UDP: Transport.
- ICE: Connection establishment.
- DTLS: Key negotiation và bảo mật.

### 6.3. Adaptive Bitrate

Windows cần theo dõi chất lượng kết nối và hiển thị bitrate thực tế.

Khi Wi-Fi yếu:

1. Theo dõi packet loss.
2. Theo dõi RTT và jitter.
3. Điều chỉnh bitrate.
4. Giảm bitrate khi cần thiết.
5. Giữ độ trễ thấp bằng cách tránh tích tụ frame.

Lưu ý: Điều chỉnh bitrate không đồng nghĩa tự động thay đổi FPS hoặc độ phân giải. Những thông số này cần được xử lý riêng.

---

## 7. Development Roadmap

### Phase 1 – iPhone Camera Application

**Goal:** Capture camera và hiển thị video trực tiếp trên iPhone.

Tasks:

- Create Xcode project.
- Request camera permission.
- Enumerate camera formats.
- Implement camera capture.
- Implement resolution selection.
- Implement FPS selection.
- Display capture FPS.

Expected result:

Camera hoạt động độc lập và có thể xác nhận khả năng capture 1080p120.

### Phase 2 – WebRTC iPhone Sender

**Goal:** Stream video từ iPhone.

Tasks:

- Integrate WebRTC iOS SDK.
- Create PeerConnection.
- Create video track.
- Configure H.264.
- Implement SDP Offer/Answer.
- Implement ICE candidate exchange.
- Start video transmission.

Expected result:

iPhone có thể phát video đến một WebRTC receiver.

### Phase 3 – Windows Receiver

**Goal:** Nhận và hiển thị video.

Tasks:

- Create WPF project.
- Integrate WebView2.
- Implement WebRTC JavaScript receiver.
- Implement WebSocket signaling.
- Display incoming video.
- Add Start / Stop controls.
- Display connection status.

Expected result:

Windows có thể nhận và hiển thị video từ iPhone.

### Phase 4 – Streaming Configuration

**Goal:** Điều khiển thông số streaming từ Windows.

Tasks:

- Add bitrate slider.
- Add FPS selector.
- Add resolution selector.
- Add camera selector.
- Implement WebSocket control commands.
- Update iPhone streaming configuration.
- Display current configuration.

Expected result:

Windows có thể điều chỉnh thông số streaming của iPhone.

### Phase 5 – Performance Optimization

**Goal:** Optimize video streaming over Wi-Fi.

Tasks:

- Hardware encoding.
- Hardware decoding.
- Optimize frame buffers.
- Reduce unnecessary frame copies.
- Optimize jitter buffer.
- Configure adaptive bitrate.
- Monitor packet loss.
- Measure end-to-end latency.

Expected result:

Đạt hiệu năng tốt nhất trong giới hạn phần cứng và Wi-Fi thực tế.

### Phase 6 – Benchmark

So sánh:

1. Existing RTP implementation.
2. New WebRTC implementation.

Cùng sử dụng:
- Một iPhone.
- Một Windows PC.
- Cùng Wi-Fi Access Point.
- Cùng độ phân giải.
- Cùng FPS.
- Cùng bitrate mục tiêu.

Ghi nhận:

- Average FPS.
- Minimum FPS.
- Average latency.
- P95 latency.
- Packet loss.
- Jitter.
- CPU usage.
- Memory usage.

---

## 8. Project Folder Structure

Đề xuất quản lý chung trong một Git repository:

```text
iPhone-Windows-VideoStreaming/
|
+-- iOS/
|   +-- iPhoneVideoSender/
|       +-- iPhoneVideoSender.xcodeproj
|
+-- Windows/
|   +-- WindowsVideoReceiver.sln
|
+-- Documents/
|   +-- Architecture.md
|   +-- DevelopmentGuide.md
|   +-- Benchmark.md
|
+-- README.md
```

Hai ứng dụng có thể build và chạy độc lập.

---

## 9. Development IDE Recommendation

| Task | Recommended IDE |
|---|---|
| iOS Application | Xcode |
| Swift Development | Xcode |
| Windows Application | Visual Studio |
| C# / WPF | Visual Studio |
| JavaScript | VS Code / Visual Studio |
| Network Analysis | Wireshark |
| Video Analysis | FFmpeg |

**Không cần sử dụng Unity cho dự án này.**

Xcode phải chạy trên macOS. Windows application có thể phát triển trực tiếp trên máy Windows.

---

## 10. Initial Milestone – Minimum Viable Product

Phiên bản đầu tiên không nên triển khai tất cả tính năng cùng lúc.

Mục tiêu MVP:

**iPhone**
- Open camera.
- Capture 1080p60.
- Start streaming.
- Stop streaming.

**Windows**
- Connect to iPhone.
- Display video.
- Start / Stop.
- Display actual FPS.
- Display bitrate.
- Display connection status.

Sau khi MVP chạy ổn định, mới bổ sung 120 FPS, slider điều chỉnh bitrate và performance monitoring.

---

## 11. Technical Risks

| Risk | Mitigation |
|---|---|
| iPhone không hỗ trợ 1080p120 capture | Check camera formats trước khi triển khai |
| Wi-Fi packet loss | Monitor RTCP statistics |
| High latency | Optimize buffering and encoding |
| FPS không ổn định | Measure each pipeline stage |
| Windows decoder bottleneck | Use hardware decoding |
| WebRTC integration complexity | Start with a minimal sender/receiver sample |
| Device compatibility | Test trên nhiều model iPhone |

---

## 12. Final Recommendation

Technology stack được đề xuất:

**iPhone**
- Xcode
- Swift
- SwiftUI
- AVFoundation
- VideoToolbox
- WebRTC iOS SDK

**Windows**
- Visual Studio
- C#
- WPF
- WebView2
- WebRTC JavaScript API
- WebSocket

**Network**
- WebRTC
- RTP/SRTP
- UDP
- Wi-Fi 5 GHz / 6 GHz

**Development strategy:**

Build iPhone Sender → Build Windows Receiver → Establish WebRTC Connection → Display Video → Add Streaming Controls → Optimize Performance.

Ưu tiên hoàn thành một luồng video 1080p60 ổn định trước, sau đó mới nâng lên 1080p120.

**Success criteria:** Video được truyền trực tiếp từ iPhone sang Windows qua Wi-Fi, có khả năng điều chỉnh thông số từ Windows và đo lường được FPS, latency, bitrate và packet loss.
