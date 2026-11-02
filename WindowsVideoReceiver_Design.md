# Technical Design Document
# Windows Video Receiver – Real-time Video Streaming

**Version:** 1.0  
**Project type:** Prototype / Proof of Concept (PoC)  
**Primary protocol:** WebRTC  
**Transport:** UDP over Wi-Fi LAN  
**Dự án đối tác:** iPhone Video Sender (`iPhoneVideoSender_Design.md`)

---

## 1. Project Overview

### 1.1. Objective

Xây dựng ứng dụng Windows nhận, giải mã và hiển thị video trực tiếp từ iPhone qua Wi-Fi bằng WebRTC, đồng thời cung cấp giao diện để điều chỉnh các thông số streaming và theo dõi hiệu năng.

Ứng dụng này là **Receiver** trong hệ thống hai ứng dụng độc lập. Phía gửi (iPhone Video Sender) được mô tả trong tài liệu riêng. Hai dự án chỉ giao tiếp với nhau qua **hợp đồng giao tiếp (Interface Contract)** ở mục 6.

### 1.2. Technical Requirements

| Parameter | Target |
|---|---|
| Receiver | Windows PC |
| Resolution | 1920 × 1080 |
| Target FPS | 120 FPS |
| Video Codec | H.264 |
| Decoding | Hardware accelerated |
| Protocol | WebRTC |
| Transport | UDP |
| Network | Wi-Fi 5 GHz / 6 GHz |
| Priority | Lowest possible latency |
| Connection | Local Network |

**Lưu ý:** 1080p120 là mục tiêu cần kiểm chứng trên phần cứng thực tế. Màn hình Windows cũng cần hỗ trợ tần số quét đủ cao thì mới hiển thị được 120 FPS.

### 1.3. Phạm vi của dự án này

**Bao gồm:**
- Chạy WebSocket signaling server.
- Công bố WebSocket server qua Bonjour/mDNS để iPhone tự động tìm thấy.
- Nhận video qua WebRTC, giải mã và hiển thị.
- Giao diện điều khiển (Start/Stop, resolution, FPS, bitrate, camera).
- Gửi lệnh điều khiển sang iPhone.
- Hiển thị thống kê hiệu năng và đo độ trễ.

**Không bao gồm:**
- Capture camera, mã hóa video (do iPhone đảm nhiệm).

---

## 2. Development Environment

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
| Device Discovery | Bonjour / mDNS (DNS-SD) |

**Lý do chọn Visual Studio:**
- Dễ thiết kế giao diện desktop, có sẵn WPF Designer.
- Hỗ trợ Slider, Button, ComboBox, TextBlock.
- Debug C# thuận tiện.
- Tích hợp được WebView2 để dùng WebRTC engine của Chromium.

**Quyết định thiết kế:** ở phiên bản đầu tiên, dùng WebRTC trong WebView2 thay vì tự tích hợp WebRTC Native C++ SDK. Cách này giảm đáng kể độ phức tạp của phần Windows.

### Công cụ bổ trợ

| Tool | Purpose |
|---|---|
| Git | Version control |
| Wireshark | Phân tích gói tin mạng |
| VLC | Test video stream |
| FFmpeg | Phân tích video |
| Visual Studio Code | Chỉnh sửa JS / file cấu hình |

Ứng dụng Windows có thể phát triển trực tiếp trên máy Windows, không cần macOS.

---

## 3. Architecture

### 3.1. Vị trí trong hệ thống

```text
+------------------+                 +----------------------+
|      iPhone      |    Wi-Fi LAN    |       Windows        |
| (dự án đối tác)  | <-------------> |     (dự án này)      |
|                  |                 |                      |
|   WebRTC SDK     | <--- Media ---> |   WebRTC JavaScript  |
|  WebSocket client| <-- Control --> |   WebSocket server   |
|                  |                 |        |             |
|                  |                 |     WebView2         |
|                  |                 |        |             |
|                  |                 |   WPF Application    |
+------------------+                 +----------------------+
```

### 3.2. Tách Control Plane và Media Plane

**Media Plane** – nhận và hiển thị video:

```text
iPhone  --->  UDP / Wi-Fi
                  |
                  v
        WebRTC Receiver (WebView2)
                  |
                  v
          Hardware Decode
                  |
                  v
           Video Display
```

**Control Plane** – điều khiển streaming:

```text
Windows WPF UI
      |
      v
WebSocket (server)
      |
      v
iPhone Application
      |
      v
Update Streaming Configuration
```

Không nhận video qua WebSocket. WebSocket chỉ dùng cho signaling và lệnh điều khiển.

### 3.3. Phân chia trách nhiệm C# và JavaScript

| Lớp | Trách nhiệm |
|---|---|
| C# / WPF | UI điều khiển, WebSocket server, ViewModel, hiển thị thống kê, quản lý vòng đời WebView2 |
| JavaScript (trong WebView2) | RTCPeerConnection, tạo Offer, nhận track, render `<video>`, đọc `getStats()` |
| Cầu nối | `CoreWebView2.WebMessageReceived` / `PostWebMessageAsJson` giữa C# và JS |

Gợi ý luồng: signaling message từ WebSocket được C# chuyển vào JS qua `PostWebMessageAsJson`; Offer/ICE sinh ra từ JS được gửi ngược về C# để chuyển sang iPhone.

---

## 4. Module Design

### 4.1. Cấu trúc thư mục

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

### 4.2. User Interface

Giao diện chia thành 3 khu vực chính.

**A. Video Display**
- Hiển thị video từ iPhone.
- Full screen mode.
- Hiển thị resolution.
- Hiển thị FPS.
- Hiển thị connection status.

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

Các tùy chọn Resolution / FPS / Camera nên được điền dựa trên message `CAPABILITIES` từ iPhone, tránh cho người dùng chọn cấu hình không được hỗ trợ.

**C. Performance Monitoring**

| Metric | Description |
|---|---|
| Capture FPS | Camera capture frame rate (nhận từ iPhone qua `STATS`) |
| Encode FPS | Encoder output frame rate (nhận từ iPhone qua `STATS`) |
| Receive FPS | Frames received |
| Decode FPS | Frames decoded |
| Render FPS | Frames displayed |
| Bitrate | Actual transmission bitrate |
| Packet Loss | Lost packets |
| Jitter | Network jitter |
| RTT | Round-trip time |
| Latency | Estimated end-to-end delay |

Nguồn dữ liệu phía Windows: `RTCPeerConnection.getStats()` (inbound-rtp: `framesReceived`, `framesDecoded`, `framesPerSecond`, `packetsLost`, `jitter`, `jitterBufferDelay`; candidate-pair: `currentRoundTripTime`) và `requestVideoFrameCallback` metadata `presentedFrames` để đo số frame đã submit lên compositor cho Render FPS.

---

## 5. WebRTC Connection

### 5.1. Vai trò

- Windows chạy **WebSocket server** (`SignalingServer.cs`), iPhone kết nối vào.
- Windows là bên **tạo SDP Offer** với transceiver video ở chế độ `recvonly`; iPhone trả Answer.

### 5.1.1. Tìm và kết nối thiết bị

Hỗ trợ hai cách để iPhone tìm địa chỉ signaling server của Windows:

1. **Automatic Discovery:** Windows công bố dịch vụ Bonjour/mDNS (DNS-SD); iPhone tự động duyệt dịch vụ và chọn Windows Receiver tìm thấy.
2. **Manual Connection:** Người dùng nhập địa chỉ IP và port của Windows trên iPhone.

Windows không cần chủ động tìm iPhone. Ở chế độ tự động, Windows chỉ công bố dịch vụ để iPhone tìm thấy. Hai cách đều kết nối tới cùng WebSocket signaling server (`ws://<Windows host>:8080`); sau đó signaling trao đổi SDP/ICE và WebRTC truyền video.

Quy ước discovery:

- DNS-SD service type: `_wvr-signal._tcp`, trên domain `local.`.
- Windows chỉ công bố dịch vụ khi WebSocket server đang lắng nghe, và gỡ công bố khi server dừng.
- iPhone resolve dịch vụ để lấy host/port rồi kết nối WebSocket; không truyền signaling hoặc video qua mDNS.
- Hai thiết bị cần ở cùng mạng LAN có multicast mDNS. Nếu discovery không hoạt động do cấu hình mạng, người dùng có thể chuyển sang nhập IP/port thủ công.
- Discovery Bonjour/mDNS này độc lập với mDNS host candidate mà WebRTC/Chromium có thể dùng trong ICE.

### 5.2. Quy trình signaling

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

Trong cùng mạng LAN, ưu tiên kết nối trực tiếp bằng ICE host candidates. Không cần TURN server nếu hai thiết bị kết nối trực tiếp được.

Lưu ý: WebView2 (Chromium) có thể ẩn địa chỉ IP local bằng mDNS candidate. Nếu iPhone không resolve được, cần kiểm tra cấu hình hoặc cờ Chromium tương ứng.

### 5.3. Media Transport

- RTP/SRTP: Media transport.
- RTCP: Feedback và thống kê.
- UDP: Transport.
- ICE: Connection establishment.
- DTLS: Key negotiation và bảo mật.

### 5.4. Tối ưu phía nhận

- Ưu tiên H.264 trong SDP (đặt thứ tự codec preference / `setCodecPreferences`).
- Bật hardware decoding của WebView2/Chromium (kiểm tra GPU được dùng bằng `edge://gpu`).
- Giảm jitter buffer tối đa có thể bằng `RTCRtpReceiver.jitterBufferTarget` (hoặc `playoutDelayHint`) để ưu tiên độ trễ thấp.
- Render bằng `<video>` tối giản, không thêm lớp xử lý canvas trừ khi cần.

### 5.5. Adaptive Bitrate

Windows theo dõi chất lượng kết nối và hiển thị bitrate thực tế.

Khi Wi-Fi yếu:
1. Theo dõi packet loss.
2. Theo dõi RTT và jitter.
3. Gửi `SET_BITRATE` để giảm bitrate khi cần thiết.
4. Giữ độ trễ thấp bằng cách tránh tích tụ frame.

Lưu ý: Điều chỉnh bitrate không đồng nghĩa tự động thay đổi FPS hoặc độ phân giải. Những thông số này cần được xử lý riêng.

---

## 6. Interface Contract với iPhone Sender

Đây là phần **phải thống nhất** giữa hai dự án. Mọi thay đổi ở đây cần cập nhật ở cả hai tài liệu.

### 6.1. Kênh WebSocket

- Windows chạy WebSocket server; iPhone kết nối vào.
- Mọi message là JSON, có trường `type`.
- Cả Automatic Discovery và Manual Connection đều kết nối tới cùng server trên port `8080`; discovery chỉ cung cấp host/port, không thay đổi WebSocket message contract.
- Contract discovery: Windows công bố DNS-SD service type `_wvr-signal._tcp` trên domain `local.`; iPhone duyệt dịch vụ. Windows không duyệt tìm iPhone.

### 6.2. Message signaling

| type | Hướng | Payload |
|---|---|---|
| `OFFER` | Windows → iPhone | `sdp` |
| `ANSWER` | iPhone → Windows | `sdp` |
| `ICE_CANDIDATE` | Hai chiều | `candidate`, `sdpMid`, `sdpMLineIndex` |
| `STATUS` | Hai chiều | `state` (connecting / connected / failed / closed) |

### 6.3. Message điều khiển

| type | Giá trị | Kích hoạt từ UI |
|---|---|---|
| `START` | – | Nút Start |
| `STOP` | – | Nút Stop |
| `SET_BITRATE` | `value` (bps) | Bitrate Slider |
| `SET_FPS` | `value` (30/60/120) | FPS ComboBox |
| `SET_RESOLUTION` | `width`, `height` | Resolution ComboBox |
| `SET_CAMERA` | `value` (front/back) | Camera ComboBox |

Khi nhận `START_REQUEST` từ iPhone, Windows khởi tạo WebRTC receiver và tạo Offer; không gửi lại lệnh `START`.

Ví dụ:

```json
{
  "type": "SET_BITRATE",
  "value": 15000000
}
```

### 6.4. Message báo cáo (iPhone → Windows)

| type | Payload | Dùng để |
|---|---|---|
| `CAPABILITIES` | Danh sách resolution/FPS/camera hỗ trợ | Điền ComboBox |
| `CONFIG_APPLIED` | Cấu hình thực sự đang chạy | Hiển thị cấu hình hiện tại |
| `STATS` | Capture FPS, Encode FPS, bitrate, packet loss, RTT | Khu vực Performance Monitoring |
| `ERROR` | `code`, `message` | Hiển thị lỗi |

### 6.5. Giới hạn bitrate thử nghiệm

- Tối thiểu: 2 Mbps. Tối đa: 30 Mbps. Mặc định: 15 Mbps.
- Slider nên debounce (ví dụ 200–300 ms) trước khi gửi `SET_BITRATE` để tránh spam lệnh.

---

## 7. Development Roadmap

### Phase 1 – Windows Receiver cơ bản

**Goal:** Nhận và hiển thị video.

Tasks:
- Create WPF project.
- Integrate WebView2.
- Implement WebRTC JavaScript receiver.
- Implement WebSocket signaling server.
- Display incoming video.
- Add Start / Stop controls.
- Display connection status.

**Expected result:** Windows nhận và hiển thị được video từ iPhone. Có thể test sớm bằng một sender mẫu (ví dụ trang web WebRTC trên trình duyệt) trước khi app iPhone hoàn thiện.

### Phase 2 – Streaming Configuration

**Goal:** Điều khiển thông số streaming từ Windows.

Tasks:
- Add bitrate slider.
- Add FPS selector.
- Add resolution selector.
- Add camera selector.
- Implement WebSocket control commands (mục 6).
- Điền ComboBox từ `CAPABILITIES`.
- Display current configuration (`CONFIG_APPLIED`).

**Expected result:** Windows điều chỉnh được thông số streaming của iPhone.

### Phase 3 – Performance Monitoring & Optimization

**Goal:** Tối ưu và đo lường.

Tasks:
- Thu thập `getStats()` định kỳ, hiển thị Receive/Decode/Render FPS, bitrate, packet loss, jitter, RTT.
- Xác nhận hardware decoding.
- Tối ưu jitter buffer.
- Giảm frame copy / lớp xử lý thừa.
- Cấu hình adaptive bitrate.
- Đo end-to-end latency.

**Expected result:** Đạt hiệu năng tốt nhất trong giới hạn phần cứng và Wi-Fi thực tế.

### Phase 4 – Benchmark

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

## 8. MVP

**Windows**
- Connect to iPhone.
- Display video.
- Start / Stop.
- Display actual FPS.
- Display bitrate.
- Display connection status.

Sau khi MVP chạy ổn định mới bổ sung 120 FPS, slider điều chỉnh bitrate và performance monitoring đầy đủ.

---

## 9. Repository Structure

```text
WindowsVideoReceiver/
|
+-- WindowsVideoReceiver.sln
+-- WindowsVideoReceiver/     (source theo mục 4.1)
+-- Documents/
|   +-- Architecture.md
|   +-- DevelopmentGuide.md
|   +-- Benchmark.md
|
+-- README.md
```

Dự án có thể build và chạy độc lập với dự án iPhone. Nên có repo riêng, hoặc thư mục `Windows/` trong monorepo nếu muốn gộp.

---

## 10. Technical Risks

| Risk | Mitigation |
|---|---|
| Windows decoder bottleneck | Dùng hardware decoding; kiểm tra `edge://gpu` |
| Wi-Fi packet loss | Monitor RTCP / `getStats()` |
| High latency | Giảm jitter buffer, tối ưu pipeline |
| FPS không ổn định | Đo từng công đoạn (receive → decode → render) |
| WebView2 giới hạn render rate / vsync | Kiểm chứng sớm Render FPS ở 60 và 120; kiểm tra tần số quét màn hình |
| WebRTC mDNS ICE candidate không resolve được | Kiểm tra cấu hình WebRTC/Chromium và ICE connectivity; đây là vấn đề riêng với Bonjour discovery |
| WebRTC integration complexity | Bắt đầu từ sample sender/receiver tối thiểu |
| Firewall chặn WebSocket / UDP | Mở port trong Windows Firewall, hướng dẫn trong README |
| Device compatibility | Test với nhiều model iPhone |

---

## 11. Success Criteria

- Windows nhận và hiển thị video trực tiếp từ iPhone qua Wi-Fi.
- Điều chỉnh được thông số streaming từ giao diện Windows.
- Đo lường được FPS, latency, bitrate và packet loss.

Ưu tiên hoàn thành một luồng 1080p60 ổn định trước, sau đó mới nâng lên 1080p120.
