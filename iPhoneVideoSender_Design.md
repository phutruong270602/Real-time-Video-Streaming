# Technical Design Document
# iPhone Video Sender – Real-time Video Streaming

**Version:** 1.0  
**Project type:** Prototype / Proof of Concept (PoC)  
**Primary protocol:** WebRTC  
**Transport:** UDP over Wi-Fi LAN  
**Dự án đối tác:** Windows Video Receiver (`WindowsVideoReceiver_Design.md`)

---

## 1. Project Overview

### 1.1. Objective

Xây dựng ứng dụng iOS capture video từ camera iPhone và truyền trực tiếp đến máy tính Windows qua mạng Wi-Fi bằng WebRTC, với độ trễ thấp nhất có thể.

Ứng dụng này là **Sender** trong hệ thống hai ứng dụng độc lập. Phía nhận (Windows Video Receiver) được mô tả trong tài liệu riêng. Hai dự án chỉ giao tiếp với nhau qua **hợp đồng giao tiếp (Interface Contract)** ở mục 6.

### 1.2. Technical Requirements

| Parameter | Target |
|---|---|
| Source | iPhone Camera |
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

### 1.3. Phạm vi của dự án này

**Bao gồm:**
- Capture camera, chọn format/FPS/camera.
- Mã hóa H.264 bằng hardware.
- Tạo WebRTC PeerConnection, gửi video track.
- Tìm Windows Receiver qua Bonjour/mDNS hoặc kết nối thủ công bằng IP/port.
- Kết nối signaling (WebSocket client).
- Nhận và áp dụng lệnh điều khiển từ Windows.
- Thu thập và hiển thị thống kê phía sender.

**Không bao gồm:**
- Giao diện nhận/hiển thị video từ xa.
- Signaling server (do Windows đảm nhiệm).

---

## 2. Development Environment

| Component | Technology |
|---|---|
| IDE | Xcode (chạy trên macOS) |
| Language | Swift |
| UI Framework | SwiftUI |
| Camera | AVFoundation |
| Video Encoder | VideoToolbox |
| Streaming | WebRTC iOS SDK |
| Build | Xcode / Swift Package Manager hoặc framework tích hợp |
| Device Discovery | Bonjour / mDNS (DNS-SD) |

**Lý do chọn Xcode:**
- IDE chính thức để phát triển iOS.
- Debug trực tiếp trên iPhone.
- Quản lý provisioning profile và code signing.
- Theo dõi camera capture và performance.
- Tích hợp tốt với SwiftUI, AVFoundation và VideoToolbox.

### Công cụ bổ trợ

| Tool | Purpose |
|---|---|
| Git | Version control |
| Xcode Instruments | Performance profiling trên iPhone |
| Wireshark | Phân tích gói tin mạng |
| FFmpeg | Phân tích video |
| Visual Studio Code | Chỉnh sửa file cấu hình / phụ trợ |

**Không cần sử dụng Unity cho dự án này.**

---

## 3. Architecture

### 3.1. Vị trí trong hệ thống

```text
+------------------+                 +----------------------+
|      iPhone      |    Wi-Fi LAN    |       Windows        |
|   (dự án này)    | <-------------> |   (dự án đối tác)    |
|                  |                 |                      |
|  AVFoundation    |                 |   WPF + WebView2     |
|       |          |                 |                      |
|  VideoToolbox    |                 |                      |
|       |          |                 |                      |
|   WebRTC SDK     | <--- Media ---> |   WebRTC JavaScript  |
|  WebSocket client| <-- Control --> |   WebSocket server   |
+------------------+                 +----------------------+
```

### 3.2. Tách Control Plane và Media Plane

**Media Plane** – truyền video trực tiếp:

```text
iPhone Camera
     |
     v
AVFoundation
     |
     v
VideoToolbox (H.264 hardware)
     |
     v
WebRTC (RTP/SRTP)
     |
     v
UDP / Wi-Fi  --->  Windows
```

**Control Plane** – signaling và lệnh điều khiển:

```text
Windows (WebSocket server)
      |
      v
WebSocket
      |
      v
SignalingClient (iPhone)
      |
      v
Cập nhật Streaming Configuration
```

Không gửi video qua WebSocket. WebSocket chỉ dùng cho signaling và lệnh điều khiển.

---

## 4. Module Design

### 4.1. Cấu trúc thư mục

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
|   +-- BonjourDiscoveryClient.swift
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
- FPS: 30 / 60 / 120 / 150 / 200 / 240 (chỉ hiển thị mức camera hỗ trợ).
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

Cần kiểm tra `videoSupportedFrameRateRanges` trước khi cho phép chọn từng mức FPS.

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

### 4.4. Streaming Statistics (phía iPhone)

| Metric | Description |
|---|---|
| Capture FPS | Tốc độ capture của camera |
| Encode FPS | Tốc độ output của encoder |
| Sent Bitrate | Bitrate thực tế đang gửi |
| Packet Loss | Gói tin mất (từ RTCP feedback) |
| RTT | Round-trip time |
| CPU / Memory | Đo bằng Instruments |

Các số liệu này nên được gửi định kỳ về Windows qua control channel (message `STATS`) để hiển thị trên giao diện phía Windows.

---

## 5. WebRTC Connection

### 5.1. Vai trò

- iPhone là **WebSocket client**, kết nối tới signaling server chạy trên Windows.
- Theo quy trình thiết kế gốc, **Windows gửi SDP Offer**, iPhone trả **SDP Answer** kèm video track gửi đi (send-only).

### 5.1.1. Tìm và kết nối Windows

Ứng dụng cung cấp hai cách kết nối:

1. **Automatic Discovery:** iPhone duyệt dịch vụ Bonjour/mDNS (DNS-SD) do Windows công bố, hiển thị các Windows Receiver tìm thấy để người dùng chọn.
2. **Manual Connection:** Người dùng nhập địa chỉ IP và port của Windows.

Ở chế độ tự động, Windows công bố dịch vụ để iPhone tìm thấy; Windows không cần tìm kiếm iPhone. DNS-SD service type được thống nhất là `_wvr-signal._tcp` trên domain `local.`. iPhone resolve host/port rồi kết nối tới cùng WebSocket signaling server (`ws://<Windows host>:8080`) như chế độ thủ công.

Hai thiết bị cần ở cùng mạng LAN có multicast mDNS để tự động tìm thấy nhau. Nếu mạng chặn multicast hoặc không tìm thấy dịch vụ, người dùng có thể nhập IP/port thủ công. Bonjour/mDNS discovery chỉ dùng để tìm signaling server; SDP/ICE vẫn trao đổi qua WebSocket và video vẫn truyền bằng WebRTC.

### 5.2. Quy trình signaling

```text
iPhone                  Windows
   |                       |
   |------ WebSocket ------|
   |                       |
   |<----- SDP Offer ------|
   |------ SDP Answer ---->|
   |                       |
   |<---- ICE Candidates ->|
   |                       |
   |====== WebRTC P2P =====|
   |                       |
   |------- Video -------->|
```

Trong cùng mạng LAN, ưu tiên kết nối trực tiếp bằng ICE host candidates. Không cần TURN server nếu hai thiết bị kết nối trực tiếp được.

### 5.3. Media Transport

- RTP/SRTP: Media transport.
- RTCP: Feedback và thống kê.
- UDP: Transport.
- ICE: Connection establishment.
- DTLS: Key negotiation và bảo mật.

### 5.4. Adaptive Bitrate

Khi Wi-Fi yếu:

1. Theo dõi packet loss.
2. Theo dõi RTT và jitter.
3. Điều chỉnh bitrate.
4. Giảm bitrate khi cần thiết.
5. Giữ độ trễ thấp bằng cách tránh tích tụ frame (drop frame cũ thay vì queue).

Lưu ý: Điều chỉnh bitrate không đồng nghĩa tự động thay đổi FPS hoặc độ phân giải. Những thông số này cần được xử lý riêng.

---

## 6. Interface Contract với Windows Receiver

Đây là phần **phải thống nhất** giữa hai dự án. Mọi thay đổi ở đây cần cập nhật ở cả hai tài liệu.

### 6.1. Kênh WebSocket

- Windows chạy WebSocket server; iPhone kết nối vào.
- Mọi message là JSON, có trường `type`.
- Automatic Discovery và Manual Connection đều kết nối tới cùng server trên port `8080`; discovery chỉ cung cấp host/port, không thay đổi WebSocket message contract.
- Contract discovery: iPhone duyệt DNS-SD service type `_wvr-signal._tcp` trên domain `local.` do Windows công bố. Windows không duyệt tìm iPhone.

### 6.2. Message signaling

| type | Hướng | Payload |
|---|---|---|
| `OFFER` | Windows → iPhone | `sdp` |
| `ANSWER` | iPhone → Windows | `sdp` |
| `ICE_CANDIDATE` | Hai chiều | `candidate`, `sdpMid`, `sdpMLineIndex` |
| `STATUS` | Hai chiều | `state` (connecting / connected / failed / closed) |

### 6.3. Message điều khiển

| type | Giá trị | Hành vi trên iPhone |
|---|---|---|
| `START` | – | Bắt đầu capture và streaming |
| `STOP` | – | Dừng streaming |
| `SET_BITRATE` | `value` (bps) | Cập nhật bitrate encoder / WebRTC sender |
| `SET_FPS` | `value` (30/60/120) | Cấu hình lại camera format và encoder |
| `SET_RESOLUTION` | `width`, `height` | Cấu hình lại camera format và encoder |
| `SET_CAMERA` | `value` (front/back) | Chuyển camera |

Khi người dùng nhấn Start trên iPhone, iPhone gửi `START_REQUEST` tới Windows để Windows khởi tạo WebRTC receiver và tạo Offer. Message này khác với `START` do Windows gửi xuống để tránh lặp lệnh.

Ví dụ:

```json
{
  "type": "SET_BITRATE",
  "value": 15000000
}
```

### 6.4. Message báo cáo (iPhone → Windows)

| type | Payload |
|---|---|
| `CAPABILITIES` | Danh sách resolution/FPS/camera mà thiết bị hỗ trợ |
| `CONFIG_APPLIED` | Cấu hình hiện tại thực sự đang chạy |
| `STATS` | Capture FPS, Encode FPS, bitrate, packet loss, RTT |
| `ERROR` | `code`, `message` |

**Khuyến nghị:** gửi `CAPABILITIES` ngay sau khi kết nối để Windows chỉ hiển thị các tùy chọn hợp lệ (ví dụ chỉ hiện 120 FPS nếu iPhone hỗ trợ).

### 6.5. Giới hạn bitrate thử nghiệm

- Tối thiểu: 2 Mbps. Tối đa: 30 Mbps. Mặc định: 15 Mbps.
- iPhone nên tự kiểm tra và clamp giá trị nhận được.

---

## 7. Development Roadmap

### Phase 1 – Camera Application

**Goal:** Capture camera và hiển thị video trực tiếp trên iPhone.

Tasks:
- Create Xcode project.
- Request camera permission.
- Enumerate camera formats.
- Implement camera capture.
- Implement resolution selection.
- Implement FPS selection.
- Display capture FPS.

**Expected result:** Camera hoạt động độc lập và xác nhận được khả năng capture 1080p120.

### Phase 2 – WebRTC Sender

**Goal:** Stream video từ iPhone.

Tasks:
- Integrate WebRTC iOS SDK.
- Create PeerConnection.
- Create video track.
- Configure H.264.
- Implement Bonjour/mDNS browsing for Windows Receivers and manual IP/port entry.
- Implement SDP Offer/Answer.
- Implement ICE candidate exchange.
- Start video transmission.

**Expected result:** iPhone phát video đến một WebRTC receiver (có thể test với receiver mẫu trên trình duyệt trước khi có app Windows).

### Phase 3 – Streaming Configuration

**Goal:** Nhận điều khiển từ Windows.

Tasks:
- Implement `SignalingClient` và xử lý các message ở mục 6.
- Áp dụng `SET_BITRATE`, `SET_FPS`, `SET_RESOLUTION`, `SET_CAMERA`.
- Gửi `CAPABILITIES`, `CONFIG_APPLIED`, `STATS`.

**Expected result:** Windows điều chỉnh được thông số streaming của iPhone.

### Phase 4 – Performance Optimization

**Goal:** Tối ưu truyền video qua Wi-Fi.

Tasks:
- Xác nhận hardware encoding.
- Tối ưu frame buffer, giảm copy không cần thiết.
- Cấu hình adaptive bitrate.
- Theo dõi packet loss.
- Đo độ trễ từng công đoạn (capture → encode → send).

**Expected result:** Đạt hiệu năng tốt nhất trong giới hạn phần cứng và Wi-Fi thực tế.

### Phase 5 – Benchmark (phối hợp với Windows)

So sánh implementation RTP cũ với implementation WebRTC mới, cùng điều kiện:
- Một iPhone, một Windows PC, cùng Wi-Fi Access Point.
- Cùng độ phân giải, FPS, bitrate mục tiêu.

Ghi nhận phía iPhone: Average/Minimum FPS, bitrate, packet loss, CPU usage, Memory usage. Độ trễ end-to-end và jitter do phía Windows đo.

---

## 8. MVP

**iPhone**
- Open camera.
- Capture 1080p60.
- Start streaming.
- Stop streaming.

Sau khi MVP chạy ổn định mới bổ sung 120 FPS, điều chỉnh bitrate động và performance monitoring.

---

## 9. Repository Structure

```text
iPhoneVideoSender/
|
+-- iPhoneVideoSender.xcodeproj
+-- iPhoneVideoSender/        (source theo mục 4.1)
+-- Documents/
|   +-- Architecture.md
|   +-- DevelopmentGuide.md
|   +-- Benchmark.md
|
+-- README.md
```

Dự án có thể build và chạy độc lập với dự án Windows. Nên có repo riêng, hoặc thư mục `iOS/` trong monorepo nếu muốn gộp.

---

## 10. Technical Risks

| Risk | Mitigation |
|---|---|
| iPhone không hỗ trợ 1080p120 capture | Kiểm tra camera formats trước khi triển khai; gửi `CAPABILITIES` |
| Wi-Fi packet loss | Monitor RTCP statistics |
| High latency | Tối ưu buffering và encoding |
| FPS không ổn định | Đo từng công đoạn pipeline |
| WebRTC integration complexity | Bắt đầu từ sample sender/receiver tối thiểu |
| WebRTC SDK có thể giới hạn FPS/độ phân giải | Kiểm chứng sớm ở Phase 2; cân nhắc tùy biến capturer |
| Device compatibility | Test trên nhiều model iPhone |
| Nóng máy khi chạy 1080p120 lâu | Theo dõi thermal state (`ProcessInfo.thermalState`) |

---

## 11. Success Criteria

- iPhone truyền video trực tiếp sang Windows qua Wi-Fi.
- Nhận và áp dụng được lệnh điều chỉnh thông số từ Windows.
- Đo được Capture FPS, Encode FPS, bitrate và packet loss phía sender.

Ưu tiên hoàn thành một luồng 1080p60 ổn định trước, sau đó mới nâng lên 1080p120.
