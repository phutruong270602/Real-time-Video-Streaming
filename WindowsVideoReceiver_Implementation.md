# Quy trình triển khai – Windows Video Receiver

**Tài liệu tham chiếu:** `WindowsVideoReceiver_Design.md`  
**Dự án đối tác:** `iPhoneVideoSender_Implementation.md`

> **Cách dùng:** Mỗi mục có ô tích `[ ]`. Khi hoàn thành, sửa thành `[x]` trong editor. Ô tích của mục cha chỉ nên tích khi tất cả mục con đã xong.
> Xem preview bằng `Ctrl + K` rồi `V`.

---

## Tổng quan tiến độ

- [ ] **Giai đoạn 0 – Chuẩn bị môi trường**
- [x] **Giai đoạn 1 – Khung ứng dụng WPF và WebView2**
- [ ] **Giai đoạn 2 – Signaling Server**
- [ ] **Giai đoạn 3 – WebRTC Receiver (MVP)**
- [ ] **Giai đoạn 4 – Giao diện điều khiển**
- [ ] **Giai đoạn 5 – Performance Monitoring**
- [ ] **Giai đoạn 6 – Tối ưu hiệu năng**
- [ ] **Giai đoạn 7 – Benchmark và nghiệm thu**

---

## Giai đoạn 0 – Chuẩn bị môi trường

- [ ] **0.1 Công cụ**
  - [x] Cài Visual Studio Community 2022 (17.14.41)
  - [ ] Cài workload ".NET desktop development" vào VS 2022 (đã kiểm tra VS 2022 có sẵn, nhưng workload chưa xác nhận là đã cài/được kích hoạt trong instance hiện tại)
  - [x] Cài .NET SDK phù hợp (10.0.403 / .NET 10 SDK tương thích với project)
  - [x] Cài Microsoft Edge WebView2 Runtime (156.0.4314.50)
  - [x] Cài Git
  - [ ] Cài Wireshark
  - [ ] Cài VLC
  - [x] Cài FFmpeg (9.0.2; bản portable cho tài khoản hiện tại)
- [ ] **0.2 Kiểm tra phần cứng**
  - [x] GPU hỗ trợ giải mã H.264 bằng hardware (NVIDIA GeForce RTX 4070 Laptop GPU; xác minh WebView2 sử dụng hardware decode ở giai đoạn kiểm thử)
  - [x] Xác định tần số quét màn hình: 240 Hz (2560 × 1600, theo dxdiag)
  - [x] Windows đang kết nối Wi-Fi 5 GHz (802.11ax)
  - [ ] Xác nhận iPhone cùng mạng Wi-Fi 5/6 GHz với Windows
- [x] **0.3 Tạo repository và solution**
  - [x] Khởi tạo Git repo, tạo `README.md`, `Documents/`
  - [x] Tạo solution `WindowsVideoReceiver.sln` (WPF, .NET 10)
  - [x] Tạo cấu trúc thư mục theo mục 4.1 của tài liệu thiết kế
- [ ] **0.4 Cấu hình mạng**
  - [x] Chọn port WebSocket (8080)
  - [ ] Áp dụng quy tắc Windows Firewall (script đã có; cần UAC, profile Private/Domain và kiểm thử trên LAN)
  - [x] Ghi lại IP của máy Windows để dùng khi kết nối thủ công (địa chỉ động; xem `Documents/DevelopmentEnvironment.md`)

**Điều kiện hoàn thành:** [x] solution build và chạy được cửa sổ WPF trống (đã kiểm tra).

**Trạng thái hiện tại:** Giai đoạn 0 chưa hoàn tất. Đã xác nhận VS 2022, .NET SDK, WebView2, Git và FFmpeg; còn workload .NET desktop trong VS 2022, Wireshark, VLC, xác nhận iPhone cùng Wi-Fi và quy tắc Firewall chưa hoàn tất.

---

## Giai đoạn 1 – Khung ứng dụng WPF và WebView2

- [x] **1.1 Giao diện khung**
  - [x] `MainWindow` chia 3 khu vực: Video Display, Streaming Configuration, Performance Monitoring
  - [x] Thanh trạng thái hiển thị Connection status
- [x] **1.2 Tích hợp WebView2**
  - [x] Thêm NuGet `Microsoft.Web.WebView2`
  - [x] Khởi tạo `CoreWebView2` trong `VideoPlayerView`
  - [x] Nạp trang HTML nội bộ chứa thẻ `<video>`
  - [x] Đặt video `muted`, `autoplay`, `playsinline` để phát video không tiếng khi được gán stream
- [x] **1.3 Cầu nối C# ↔ JavaScript**
  - [x] C# gửi sang JS bằng `PostWebMessageAsJson`
  - [x] JS gửi sang C# bằng `window.chrome.webview.postMessage`
  - [x] Test gửi/nhận message ping–pong bằng cửa sổ WPF
- [x] **1.4 MVVM cơ bản**
  - [x] Tạo `StreamingControlViewModel`
  - [x] Binding trạng thái kết nối lên UI

**Điều kiện hoàn thành:** WebView2 hiển thị trang HTML, hai chiều C# ↔ JS hoạt động.

**Trạng thái hiện tại:** [x] Đã build thành công và xác nhận JavaScript ping–pong qua cửa sổ ứng dụng.

---

## Giai đoạn 2 – Signaling Server

- [ ] **2.1 Bonjour/mDNS service discovery**
  - [x] Công bố DNS-SD service type `_wvr-signal._tcp` trên domain `local.`, port WebSocket `8080`
  - [x] Chỉ công bố sau khi WebSocket server bắt đầu; gỡ công bố khi ứng dụng dừng server
  - [ ] Mở/kiểm tra Windows Firewall cho WebSocket TCP `8080` và mDNS UDP `5353` trên mạng LAN (script giới hạn Private/Domain + LocalSubnet; áp dụng cần Administrator)
  - [ ] Xác nhận service có thể được duyệt từ iPhone cùng mạng Wi-Fi
- [x] **2.2 WebSocket server**
  - [x] Cài `SignalingServer.cs` lắng nghe trên port đã chọn
  - [x] Chấp nhận kết nối từ iPhone, xử lý ngắt kết nối/kết nối lại
  - [x] Chỉ cho một iPhone kết nối tại một thời điểm (PoC)
- [x] **2.3 Định tuyến message**
  - [x] Tạo model C# cho message theo Interface Contract (mục 6 tài liệu thiết kế)
  - [x] Dispatcher theo `type`
  - [x] Chuyển `ANSWER`, `ICE_CANDIDATE` từ iPhone sang JS
  - [x] Chuyển `OFFER`, `ICE_CANDIDATE` từ JS sang iPhone
- [ ] **2.4 Trạng thái kết nối**
  - [x] Hiển thị: chờ kết nối / đã kết nối / lỗi
  - [x] Hiển thị trạng thái công bố dịch vụ và địa chỉ IP:port để kết nối thủ công
- [ ] **2.5 Test độc lập**
  - [ ] Test server bằng một WebSocket client bất kỳ (trình duyệt hoặc công cụ test)

**Điều kiện hoàn thành:** iPhone kết nối được bằng Automatic Discovery và Manual Connection; cả hai cách đều tới cùng WebSocket server và trao đổi được message.

---

## Giai đoạn 3 – WebRTC Receiver (MVP)

- [x] **3.1 Receiver bằng JavaScript**
  - [x] Cài `WebRTCClient.js` và `PeerConnectionManager.js` (cơ chế được tích hợp trực tiếp vào `receiver.html` để giảm file phụ)
  - [x] Tạo `RTCPeerConnection` (cấu hình ICE cho LAN, không cần STUN/TURN)
  - [x] Thêm transceiver video ở chế độ `recvonly`
- [x] **3.2 Offer/Answer**
  - [x] Tạo SDP Offer, gửi qua cầu nối C# → iPhone
  - [x] Nhận Answer, gọi `setRemoteDescription`
  - [x] Trao đổi ICE candidate hai chiều
- [ ] **3.3 Ưu tiên H.264**
  - [ ] Dùng `setCodecPreferences` đặt H.264 lên đầu
  - [ ] Kiểm tra SDP Answer xác nhận H.264
- [ ] **3.4 Hiển thị video**
  - [x] Gắn `track` nhận được vào thẻ `<video>`
  - [x] Hiển thị trạng thái kết nối
- [ ] **3.5 Start / Stop**
  - [x] Nút Start: bắt đầu phiên kết nối, gửi `START`
  - [x] Nút Stop: gửi `STOP`, đóng PeerConnection, dọn dẹp
  - [x] Cho phép Start lại nhiều lần không cần khởi động lại app
- [ ] **3.6 Hiển thị số liệu cơ bản (MVP)**
  - [x] FPS thực tế
  - [x] Bitrate thực tế
  - [x] Connection status
- [ ] **3.7 Test end-to-end**
  - [ ] Test với app iPhone Sender thật
  - [ ] Xác nhận video hiển thị liên tục ít nhất 5 phút

**Điều kiện hoàn thành (MVP):** Windows kết nối, hiển thị video 1080p60 từ iPhone, Start/Stop hoạt động, hiển thị FPS/bitrate/trạng thái.

---

## Giai đoạn 4 – Giao diện điều khiển

- [ ] **4.1 Nhận khả năng của iPhone**
  - [x] Xử lý `CAPABILITIES`, điền dữ liệu cho các ComboBox
  - [x] Ẩn/vô hiệu hóa lựa chọn không được hỗ trợ (ví dụ 120 FPS)
  - [x] Lọc FPS theo độ phân giải và camera từ từng mode trong `CAPABILITIES`
- [ ] **4.2 Các điều khiển**
  - [x] Resolution ComboBox → `SET_RESOLUTION`
  - [x] FPS ComboBox → `SET_FPS`
  - [x] Camera ComboBox → `SET_CAMERA`
  - [x] Bitrate Slider (2–30 Mbps, mặc định 15) → `SET_BITRATE`
  - [x] Debounce Slider 250 ms trước khi gửi lệnh
- [ ] **4.3 Hiển thị cấu hình hiện tại**
  - [x] Xử lý `CONFIG_APPLIED` và cập nhật UI
  - [x] Xử lý `ERROR`, hiển thị thông báo lỗi
- [ ] **4.4 Full screen**
  - [ ] Chế độ toàn màn hình cho khu vực video
  - [ ] Thoát bằng phím Esc
- [ ] **4.5 Test điều khiển**
  - [ ] Đổi từng thông số khi đang stream, xác nhận iPhone áp dụng
  - [ ] Xác nhận video phía Windows vẫn liên tục sau khi đổi thông số

**Điều kiện hoàn thành:** điều chỉnh được toàn bộ thông số streaming từ giao diện Windows.

---

## Giai đoạn 5 – Performance Monitoring

- [ ] **5.1 Thu thập từ `getStats()`**
  - [x] Gọi định kỳ mỗi 1 giây trong JS, gửi về C#
  - [x] Receive FPS (`framesReceived`)
  - [x] Decode FPS (`framesDecoded`)
  - [x] Bitrate (`bytesReceived`)
  - [x] Packet Loss (`packetsLost`)
  - [x] Jitter (`jitter`)
  - [x] RTT (`currentRoundTripTime`)
- [ ] **5.2 Render FPS**
  - [x] Đo từ `presentedFrames` trong metadata của `requestVideoFrameCallback` trên thẻ `<video>`
- [ ] **5.3 Số liệu từ iPhone**
  - [x] Nhận `STATS`: Capture FPS, Encode FPS, bitrate, số packet bị mất (`packetsLost`), RTT
- [ ] **5.4 Hiển thị trên UI**
  - [x] Bảng Performance Monitoring hiển thị các chỉ số nhận được
  - [ ] Đổi màu cảnh báo khi packet loss/RTT vượt ngưỡng
- [ ] **5.5 Đo độ trễ end-to-end**
  - [ ] Chọn phương pháp đo (ví dụ chụp đồng hồ trên màn hình đối chiếu, hoặc nhúng timestamp)
  - [ ] Ghi nhận Average và P95 latency
- [ ] **5.6 Ghi log**
  - [ ] Xuất số liệu ra file CSV để phân tích và benchmark
  - [x] Ghi cấu hình nhận từ iPhone và số liệu receiver/iPhone bằng tiếng Anh vào `WindowsVideoReceiver/log/received-statistics.log` (mỗi nguồn thống kê tối đa một dòng mỗi 5 giây)

**Điều kiện hoàn thành:** thấy đủ các chỉ số trên UI, xuất được log.

---

## Giai đoạn 6 – Tối ưu hiệu năng

- [ ] **6.1 Hardware decoding**
  - [ ] Kiểm tra GPU decode đang hoạt động (`edge://gpu`, Task Manager)
  - [ ] Xử lý nếu WebView2 đang dùng software decode
- [ ] **6.2 Giảm độ trễ phía nhận**
  - [ ] Đặt `jitterBufferTarget` / `playoutDelayHint` ở mức thấp
  - [ ] Kiểm tra ảnh hưởng đến độ mượt khi Wi-Fi yếu
- [ ] **6.3 Tối ưu render**
  - [ ] Dùng thẻ `<video>` tối giản, không qua canvas
  - [ ] Kiểm chứng Render FPS ở 60 và 120 FPS
  - [ ] Kiểm tra tần số quét màn hình và vsync
- [ ] **6.4 Adaptive bitrate**
  - [ ] Dựa vào packet loss/RTT, tự gửi `SET_BITRATE` giảm hoặc tăng
  - [ ] Test khi Wi-Fi yếu
- [ ] **6.5 Kiểm tra tài nguyên**
  - [ ] Đo CPU, GPU, Memory của ứng dụng Windows
  - [ ] Phát hiện memory leak khi chạy lâu

**Điều kiện hoàn thành:** hiệu năng phía nhận ổn định, có số liệu đo trước/sau tối ưu.

---

## Giai đoạn 7 – Benchmark và nghiệm thu

- [ ] **7.1 Chuẩn bị điều kiện thử**
  - [ ] Cùng iPhone, cùng Windows PC, cùng Access Point
  - [ ] Cùng resolution, FPS, bitrate với bản RTP cũ
- [ ] **7.2 Đo phía Windows**
  - [ ] Average FPS, Minimum FPS
  - [ ] Average latency, P95 latency
  - [ ] Packet loss, Jitter
  - [ ] CPU usage, Memory usage
- [ ] **7.3 So sánh và báo cáo**
  - [ ] Điền kết quả vào `Documents/Benchmark.md`
  - [ ] So sánh RTP cũ với WebRTC mới
  - [ ] Tổng hợp số liệu phía iPhone từ dự án đối tác
- [ ] **7.4 Nghiệm thu theo tiêu chí thành công**
  - [ ] Nhận và hiển thị video trực tiếp từ iPhone qua Wi-Fi
  - [ ] Điều chỉnh được thông số từ giao diện Windows
  - [ ] Đo được FPS, latency, bitrate, packet loss
  - [ ] Viết `README.md` và `DevelopmentGuide.md` (cách build, cách chạy, cách kết nối, cấu hình firewall)

**Điều kiện hoàn thành:** dự án đạt tiêu chí thành công của tài liệu thiết kế.

---

## Ghi chú theo dõi

| Ngày | Nội dung / Vấn đề gặp phải | Cách xử lý |
|---|---|---|
|  |  |  |
