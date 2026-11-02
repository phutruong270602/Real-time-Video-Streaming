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
  - [x] Tạo project SwiftUI, tên `iPhoneVideoSender`
  - [x] Build bằng Swift Package WebRTC M156 và mở app trên iPhone 17 Simulator
  - [ ] Cấu hình Signing & Capabilities, chạy được "Hello World" trên iPhone thật
  - [x] Thêm `NSCameraUsageDescription` vào Info.plist
  - [x] Thêm `NSLocalNetworkUsageDescription` (cần cho kết nối mạng LAN trên iOS)
  - [x] Thêm `_wvr-signal._tcp` vào `NSBonjourServices` để duyệt dịch vụ Bonjour của Windows
  - [x] Ghi log ra Xcode/device console và `Documents/iPhoneVideoSender.log`; bật chia sẻ file để lưu log từ iPhone
- [x] **0.4 Tạo cấu trúc module**
  - [x] Tạo các nhóm `App/`, `Camera/`, `Streaming/`, `Network/`, `Configuration/`, `Monitoring/`
  - [x] Tạo các file module; camera, signaling, streaming và monitoring đã có implementation ban đầu

**Điều kiện hoàn thành:** project đã build cho iOS Simulator; còn cấu hình signing và chạy kiểm chứng trên iPhone thật.

### Xem và lưu log trên iPhone

- Trong app, cuộn xuống cuối trang và mở **Application logs** để xem tối đa 60 log gần nhất; danh sách tự cập nhật khi đang mở. Version/build hiển thị ngay đầu trang.
- Khi chạy app từ Xcode trên iPhone, xem log trong **Debug console**; log cũng có thể xem trong ứng dụng **Console** trên macOS bằng cách chọn iPhone.
- File `iPhoneVideoSender.log` được ghi trong thư mục Documents của ứng dụng. Trên iPhone, mở **Files → On My iPhone → iPhoneVideoSender** để xem hoặc chia sẻ file.
- Ứng dụng ghi log khởi động, camera, Bonjour, WebSocket, signaling và trạng thái WebRTC. Trong khi WebRTC kết nối, app ghi một dòng `PERFORMANCE` mỗi 5 giây gồm capture/encode FPS, bitrate gửi, packet loss, RTT và các thông số mục tiêu để đối chiếu; thay đổi thông số điều khiển được ghi bằng dòng `CONTROL`. Không ghi từng frame hoặc nội dung SDP/ICE.

---

## Giai đoạn 1 – Camera Application

- [x] **1.1 Xin quyền camera**
  - [x] Hiển thị yêu cầu quyền khi mở app
  - [x] Xử lý trường hợp người dùng từ chối và mở Settings
- [ ] **1.2 Liệt kê camera format**
  - [x] Viết `CameraManager` liệt kê `AVCaptureDevice.Format` của camera trước/sau
  - [x] Ghi log resolution, `videoSupportedFrameRateRanges`, pixel format
  - [x] Xác định khả năng 1080p120 theo format thiết bị
  - [ ] Lưu kết quả vào file/ghi chú (dùng cho mục tiêu 120 FPS và `CAPABILITIES`)
- [x] **1.3 Capture camera**
  - [x] Cấu hình `AVCaptureSession`
  - [x] Thêm `AVCaptureVideoDataOutput`, nhận từng frame
  - [x] Hiển thị preview trên SwiftUI
- [x] **1.4 Chọn thông số**
  - [x] Chọn resolution (720p / 1080p)
  - [x] Chọn FPS (30 / 60 / 120, chỉ cho chọn giá trị được hỗ trợ)
  - [x] Chọn camera trước/sau
  - [x] Áp dụng format bằng `activeFormat` và `activeVideoMinFrameDuration`/`MaxFrameDuration`
- [x] **1.5 Đo Capture FPS**
  - [x] Đếm frame/giây trong callback và hiển thị lên UI
  - [x] Ghi nhận timestamp từng frame để hiển thị/chuyển tiếp WebRTC

**Điều kiện hoàn thành:** implementation đã có; cần xác nhận preview/FPS/format thực tế trên iPhone.

---

## Giai đoạn 2 – WebRTC Sender (MVP 1080p60)

- [x] **2.1 Tích hợp WebRTC iOS SDK**
  - [x] Chọn Swift Package `stasel/WebRTC` phiên bản M156
  - [x] Thêm vào project; build simulator thành công với package M156
- [x] **2.2 Tạo PeerConnection**
  - [x] Tạo `RTCPeerConnectionFactory` với H.264 encoder factory
  - [x] Tạo `RTCPeerConnection` cho LAN, không STUN/TURN
  - [x] Xử lý ICE candidate và trạng thái kết nối bằng delegate
- [x] **2.3 Tạo video track**
  - [x] Tạo `RTCVideoSource`, `RTCVideoTrack`
  - [x] Chuyển frame từ `AVCaptureVideoDataOutput` sang WebRTC, cấu hình output format theo độ phân giải/FPS đã chọn
  - [x] Thêm video track vào PeerConnection
- [ ] **2.4 Cấu hình H.264**
  - [x] Dùng H.264 encoder factory
  - [ ] Xác nhận SDP Answer thực tế đã chọn H.264 với receiver
  - [x] Đặt bitrate ban đầu 15 Mbps
- [x] **2.5 Signaling client**
  - [x] Duyệt Bonjour DNS-SD `_wvr-signal._tcp` trên `local.`
  - [x] Hiển thị receiver tìm thấy để người dùng chọn
  - [x] Kết nối WebSocket tới host/port của receiver
  - [x] Hỗ trợ nhập IP/port thủ công (mặc định `8080`)
  - [x] Dùng cùng signaling/WebRTC flow cho hai cách kết nối
  - [x] Xử lý `OFFER` → tạo và gửi `ANSWER`
  - [x] Trao đổi `ICE_CANDIDATE` hai chiều
  - [x] Gửi trạng thái kết nối
- [x] **2.6 Start / Stop streaming**
  - [x] Nút Start/Stop trên UI
  - [x] Xử lý lệnh `START` / `STOP` từ Windows
  - [x] Đóng PeerConnection và dừng capture khi Stop
- [ ] **2.7 Test với receiver mẫu**
  - [ ] Test với trang WebRTC receiver đơn giản trên trình duyệt hoặc app Windows (nếu đã có)
  - [ ] Xác nhận video hiển thị phía nhận

**Điều kiện hoàn thành (MVP):** iPhone mở camera, stream 1080p60 sang Windows, Start/Stop hoạt động.

---

## Giai đoạn 3 – Điều khiển từ Windows

- [x] **3.1 Định nghĩa message theo Interface Contract**
  - [x] Tạo model Swift cho các message (mục 6 tài liệu thiết kế)
  - [x] Parse JSON và dispatch theo `type`
- [x] **3.2 Báo cáo trạng thái về Windows**
  - [x] Gửi `CAPABILITIES` ngay sau khi kết nối
  - [x] Gửi `CONFIG_APPLIED` sau khi đổi cấu hình
  - [x] Gửi `ERROR` khi lệnh không áp dụng được
- [x] **3.3 Áp dụng lệnh điều khiển**
  - [x] `SET_BITRATE` (clamp trong khoảng 2–30 Mbps)
  - [x] `SET_FPS`
  - [x] `SET_RESOLUTION`
  - [x] `SET_CAMERA`
- [x] **3.4 Thống kê phía iPhone**
  - [x] Cài `StreamingStatistics` đọc từ `RTCStatisticsReport`
  - [x] Gửi `STATS` định kỳ (1 giây): Capture FPS, Encode FPS, bitrate, packet loss, RTT
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
