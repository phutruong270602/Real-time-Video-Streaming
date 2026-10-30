# Quy trình triển khai – iPhone Video Sender

**Tài liệu tham chiếu:** `iPhoneVideoSender_Design.md`  
**Dự án đối tác:** `WindowsVideoReceiver_Implementation.md`

> **Cách dùng:** Mỗi mục có ô tích `[ ]`. Khi hoàn thành, sửa thành `[x]` trong editor. Ô tích của mục cha chỉ nên tích khi tất cả mục con đã xong.
> Xem preview bằng `Ctrl + K` rồi `V`.

---

## Tổng quan tiến độ

- [ ] **Giai đoạn 0 – Chuẩn bị môi trường**
- [ ] **Giai đoạn 1 – Camera Application**
- [ ] **Giai đoạn 2 – WebRTC Sender (MVP 1080p60)**
- [ ] **Giai đoạn 3 – Điều khiển từ Windows**
- [ ] **Giai đoạn 4 – Nâng lên 1080p120**
- [ ] **Giai đoạn 5 – Tối ưu hiệu năng**
- [ ] **Giai đoạn 6 – Benchmark và nghiệm thu**

---

## Giai đoạn 0 – Chuẩn bị môi trường

- [ ] **0.1 Chuẩn bị phần cứng và phần mềm**
  - [ ] Máy macOS có cài Xcode bản mới
  - [ ] iPhone thật để test (simulator không có camera thật)
  - [ ] Apple ID / tài khoản developer để ký ứng dụng
  - [ ] Cáp kết nối iPhone với Mac, bật Developer Mode trên iPhone
  - [ ] Wi-Fi Access Point băng tần 5 GHz / 6 GHz
- [ ] **0.2 Tạo repository**
  - [ ] Khởi tạo Git repo
  - [ ] Tạo cấu trúc thư mục theo mục 9 của tài liệu thiết kế
  - [ ] Tạo `README.md`, thư mục `Documents/`
- [ ] **0.3 Tạo Xcode project**
  - [ ] Project SwiftUI, tên `iPhoneVideoSender`
  - [ ] Cấu hình Signing & Capabilities, chạy được "Hello World" trên iPhone thật
  - [ ] Thêm `NSCameraUsageDescription` vào Info.plist
  - [ ] Thêm `NSLocalNetworkUsageDescription` (cần cho kết nối mạng LAN trên iOS)
  - [ ] Thêm `_wvr-signal._tcp` vào `NSBonjourServices` để duyệt dịch vụ Bonjour của Windows
- [ ] **0.4 Tạo cấu trúc module**
  - [ ] Tạo các nhóm `App/`, `Camera/`, `Streaming/`, `Network/`, `Configuration/`, `Monitoring/`
  - [ ] Tạo các file rỗng theo mục 4.1 của tài liệu thiết kế

**Điều kiện hoàn thành:** app rỗng chạy được trên iPhone thật.

---

## Giai đoạn 1 – Camera Application

- [ ] **1.1 Xin quyền camera**
  - [ ] Hiển thị yêu cầu quyền khi mở app
  - [ ] Xử lý trường hợp người dùng từ chối (hiện hướng dẫn mở Settings)
- [ ] **1.2 Liệt kê camera format**
  - [ ] Viết `CameraManager` liệt kê `AVCaptureDevice.Format` của camera trước/sau
  - [ ] Ghi log resolution, `videoSupportedFrameRateRanges`, pixel format
  - [ ] Xác định thiết bị có hỗ trợ 1080p120 hay không
  - [ ] Lưu kết quả vào file/ghi chú (dùng cho mục tiêu 120 FPS và `CAPABILITIES`)
- [ ] **1.3 Capture camera**
  - [ ] Cấu hình `AVCaptureSession`
  - [ ] Thêm `AVCaptureVideoDataOutput`, nhận từng frame
  - [ ] Hiển thị preview trên SwiftUI
- [ ] **1.4 Chọn thông số**
  - [ ] Chọn resolution (720p / 1080p)
  - [ ] Chọn FPS (30 / 60 / 120, chỉ cho chọn giá trị được hỗ trợ)
  - [ ] Chọn camera trước/sau
  - [ ] Áp dụng format bằng `activeFormat` và `activeVideoMinFrameDuration`/`MaxFrameDuration`
- [ ] **1.5 Đo Capture FPS**
  - [ ] Đếm frame/giây trong callback và hiển thị lên UI
  - [ ] Ghi nhận timestamp từng frame

**Điều kiện hoàn thành:** thấy preview ổn định ở 1080p60; biết chắc máy có hỗ trợ 1080p120 hay không.

---

## Giai đoạn 2 – WebRTC Sender (MVP 1080p60)

- [ ] **2.1 Tích hợp WebRTC iOS SDK**
  - [ ] Chọn nguồn SDK (Swift Package hoặc framework build sẵn) và ghi lại phiên bản
  - [ ] Thêm vào project, build thành công
- [ ] **2.2 Tạo PeerConnection**
  - [ ] Tạo `RTCPeerConnectionFactory` với video encoder/decoder factory hỗ trợ H.264 hardware
  - [ ] Tạo `RTCPeerConnection` với cấu hình ICE cho LAN (không cần STUN/TURN)
  - [ ] Cài đặt delegate xử lý ICE candidate và trạng thái kết nối
- [ ] **2.3 Tạo video track**
  - [ ] Tạo `RTCVideoSource`, `RTCVideoTrack`
  - [ ] Đẩy frame từ `AVCaptureVideoDataOutput` vào video source (capturer tùy biến để kiểm soát FPS/resolution)
  - [ ] Thêm track vào PeerConnection ở chế độ send-only
- [ ] **2.4 Cấu hình H.264**
  - [ ] Ưu tiên H.264 trong danh sách codec
  - [ ] Kiểm tra SDP Answer có chọn H.264
  - [ ] Đặt bitrate ban đầu 15 Mbps
- [ ] **2.5 Signaling client**
  - [ ] Cài Bonjour/mDNS browser duyệt DNS-SD service type `_wvr-signal._tcp` trên domain `local.` do Windows công bố
  - [ ] Hiển thị các Windows Receiver tìm thấy và cho phép người dùng chọn một thiết bị
  - [ ] Cài `WebSocketManager` kết nối tới `ws://<Windows host>:<port>` sau khi chọn dịch vụ
  - [ ] Cung cấp lựa chọn nhập IP/port thủ công (mặc định port `8080`) nếu không dùng được discovery
  - [ ] Đảm bảo cả hai cách đều dùng cùng WebSocket signaling server và luồng WebRTC
  - [ ] Xử lý `OFFER` → tạo `ANSWER`
  - [ ] Trao đổi `ICE_CANDIDATE` hai chiều
  - [ ] Gửi/nhận `STATUS`
- [ ] **2.6 Start / Stop streaming**
  - [ ] Nút Start/Stop trên UI
  - [ ] Xử lý lệnh `START` / `STOP` từ Windows
  - [ ] Dọn dẹp tài nguyên khi stop (đóng PeerConnection, dừng capture)
- [ ] **2.7 Test với receiver mẫu**
  - [ ] Test với trang WebRTC receiver đơn giản trên trình duyệt hoặc app Windows (nếu đã có)
  - [ ] Xác nhận video hiển thị phía nhận

**Điều kiện hoàn thành (MVP):** iPhone mở camera, stream 1080p60 sang Windows, Start/Stop hoạt động.

---

## Giai đoạn 3 – Điều khiển từ Windows

- [ ] **3.1 Định nghĩa message theo Interface Contract**
  - [ ] Tạo model Swift cho các message (mục 6 tài liệu thiết kế)
  - [ ] Viết parser JSON và dispatcher theo `type`
- [ ] **3.2 Báo cáo trạng thái về Windows**
  - [ ] Gửi `CAPABILITIES` ngay sau khi kết nối
  - [ ] Gửi `CONFIG_APPLIED` sau mỗi lần đổi cấu hình
  - [ ] Gửi `ERROR` khi lệnh không áp dụng được
- [ ] **3.3 Áp dụng lệnh điều khiển**
  - [ ] `SET_BITRATE` (clamp trong khoảng 2–30 Mbps)
  - [ ] `SET_FPS`
  - [ ] `SET_RESOLUTION`
  - [ ] `SET_CAMERA`
- [ ] **3.4 Thống kê phía iPhone**
  - [ ] Cài `StreamingStatistics` đọc từ `RTCStatisticsReport`
  - [ ] Gửi `STATS` định kỳ (ví dụ mỗi 1 giây): Capture FPS, Encode FPS, bitrate, packet loss, RTT
- [ ] **3.5 Test điều khiển**
  - [ ] Đổi từng thông số từ Windows, xác nhận phía iPhone áp dụng đúng
  - [ ] Xác nhận stream không bị đứt khi đổi bitrate

**Điều kiện hoàn thành:** Windows điều chỉnh được bitrate/FPS/resolution/camera và nhận được thống kê.

---

## Giai đoạn 4 – Nâng lên 1080p120

*Chỉ thực hiện khi Giai đoạn 2 ổn định và mục 1.2 xác nhận thiết bị hỗ trợ 120 FPS.*

- [ ] **4.1 Kiểm chứng từng khâu ở 120 FPS**
  - [ ] Camera capture đạt 120 FPS ổn định
  - [ ] Capturer WebRTC không bị giới hạn FPS
  - [ ] Encoder xuất ra gần 120 FPS (xem Encode FPS)
- [ ] **4.2 Điều chỉnh WebRTC**
  - [ ] Đặt `maxFramerate` cho encoding parameters
  - [ ] Tăng bitrate phù hợp cho 120 FPS
  - [ ] Kiểm tra SDP/encoder có giới hạn ngầm không
- [ ] **4.3 Đánh giá ổn định**
  - [ ] Chạy liên tục 10–15 phút, theo dõi nhiệt độ (`ProcessInfo.thermalState`)
  - [ ] Ghi nhận FPS tối thiểu và mức độ tụt FPS
  - [ ] Ghi lại kết luận: 120 FPS đạt / không đạt, nguyên nhân

**Điều kiện hoàn thành:** có kết luận rõ ràng bằng số liệu về khả năng 1080p120.

---

## Giai đoạn 5 – Tối ưu hiệu năng

- [ ] **5.1 Xác nhận hardware encoding**
  - [ ] Kiểm tra encoder đang dùng VideoToolbox
  - [ ] Đo CPU bằng Xcode Instruments
- [ ] **5.2 Giảm độ trễ pipeline**
  - [ ] Giảm số buffer trong capture session
  - [ ] Loại bỏ chuyển đổi/copy frame không cần thiết
  - [ ] Drop frame cũ thay vì xếp hàng
- [ ] **5.3 Adaptive bitrate**
  - [ ] Theo dõi packet loss và RTT
  - [ ] Tự giảm/tăng bitrate theo điều kiện mạng
  - [ ] Kiểm tra trong điều kiện Wi-Fi yếu
- [ ] **5.4 Profiling**
  - [ ] Profile bằng Instruments (CPU, Memory, Thermal, Energy)
  - [ ] Phân tích gói tin bằng Wireshark khi cần

**Điều kiện hoàn thành:** CPU và độ trễ phía sender ở mức chấp nhận được, có số liệu đo.

---

## Giai đoạn 6 – Benchmark và nghiệm thu

- [ ] **6.1 Chuẩn bị điều kiện thử**
  - [ ] Cùng iPhone, cùng Windows PC, cùng Access Point
  - [ ] Cùng resolution, FPS, bitrate mục tiêu với bản RTP cũ
- [ ] **6.2 Đo phía iPhone**
  - [ ] Average FPS, Minimum FPS
  - [ ] Bitrate thực tế
  - [ ] Packet loss
  - [ ] CPU usage, Memory usage
- [ ] **6.3 Ghi báo cáo**
  - [ ] Điền kết quả vào `Documents/Benchmark.md`
  - [ ] Phối hợp lấy số liệu latency/jitter từ phía Windows
- [ ] **6.4 Nghiệm thu theo tiêu chí thành công**
  - [ ] Video truyền trực tiếp iPhone → Windows qua Wi-Fi
  - [ ] Điều chỉnh được thông số từ Windows
  - [ ] Đo được FPS, bitrate, packet loss phía sender
  - [ ] Viết `README.md` và `DevelopmentGuide.md` (cách build, cách chạy, cách kết nối)

**Điều kiện hoàn thành:** dự án đạt tiêu chí thành công của tài liệu thiết kế.

---

## Ghi chú theo dõi

| Ngày | Nội dung / Vấn đề gặp phải | Cách xử lý |
|---|---|---|
|  |  |  |
