# Checklist: tách RTSP receiver mới cho OptitrackCameraDLL

Mục tiêu: xây dựng một receiver C++ độc lập tại
`D:\project_protect\Real-time Video Streaming\OptitrackRtspReceiver`, sau đó tích hợp vào
`IncepIT-TECSWING-Bay-Camera-64bit` để thay phần nhận RTSP trong
`RtspCamera.cpp` mà không làm thay đổi API camera đang được các module khác sử dụng.

## Hiện trạng và phạm vi

- `Real-time Video Streaming\WindowsVideoReceiver` là ứng dụng WPF/.NET dùng
  WebView2 và WebRTC để nhận video từ iPhone. Nó không nhận RTSP và không tạo
  `RawH264Frame`, nên không thể thay trực tiếp đường nhận frame C++ hiện tại.
- `RtspCamera` hiện vừa quản lý kết nối RTSP/FFmpeg và frame queue, vừa điều khiển
  camera qua HTTP, audio TCP và các setting như frame rate, speed, ISO, zoom.
- Phạm vi receiver mới là RTSP transport, đọc packet, lifecycle và giao frame
  cho DLL. Các tính năng HTTP/audio và API camera chỉ được di chuyển nếu có yêu
  cầu riêng và đã được kiểm thử.

## Các bước thực hiện

### Giai đoạn 1 - Chốt contract và baseline

- [ ] Ghi lại baseline trước khi sửa: build Debug/Release x64, frame rate thực tế,
  độ trễ, CPU và hành vi Start/Stop/Reconnect với camera thật.
- [ ] Kiểm tra nơi gọi `RtspCamera::Start`, `Stop`, `StartStream`, `StopStream`,
  `GetFrame` và `GetLatestFrame` trong toàn DLL; xác định hành vi mà caller đang
  phụ thuộc.
- [x] Chốt contract của receiver mới: input RTSP URL/protocol; đầu ra packet H.264
  và metadata; cách báo lỗi; timeout/reconnect; chính sách queue và ownership
  của bộ nhớ.
- [ ] Xác nhận camera luôn gửi H.264 hay có thể gửi codec khác. Nếu codec thay đổi,
  quyết định rõ receiver chỉ chuyển packet encoded hay cũng decode/convert.
- [x] Chốt cách đồng bộ/sở hữu packet đầu ra: payload được receiver sở hữu trong
  `std::vector<uint8_t>`, kèm timestamp, PTS/DTS, codec và kích thước; adapter
  chuyển ownership vào `RawH264Frame` vẫn cần làm khi tích hợp DLL.

### Giai đoạn 2 - Tạo receiver độc lập

- [x] Tạo project C++17 x64 trong thư mục này, có interface public nhỏ gọn để
  DLL đích có thể gọi mà không phụ thuộc UI WPF/WebView2.
- [x] Cấu hình CMake để dùng SDK FFmpeg Release x64 của
  `OptitrackCameraDLL.vcxproj`; đã build receiver/CLI ở cấu hình Release.
- [ ] Kiểm tra cấu hình Debug và runtime deployment trên máy đích; tránh link
  trùng các bộ FFmpeg khác version.
- [x] Cài đặt mở RTSP, chọn video stream, lấy codec/stream metadata và đọc packet;
  cho phép chọn UDP/TCP theo cấu hình camera.
- [x] Cài đặt timeout, cancellation, báo lỗi có nguyên nhân và cleanup tại mọi
  điểm lỗi mở stream/codec/đọc packet.
- [x] Cài đặt queue có giới hạn và ownership rõ ràng; không để queue tăng vô hạn,
  không làm hỏng frame đang được caller đọc khi drop frame cũ.
- [x] Cài đặt Start/Stop lặp lại an toàn, reconnect theo chính sách đã chốt và
  ngăn thao tác đọc packet tiếp tục sau khi Stop.
- [x] Tạo executable/test harness độc lập để nhận một RTSP URL và thống kê packet,
  frame, timestamp, codec, resolution, lỗi và thời gian reconnect.
- [x] Tạo ứng dụng WPF C# gọi native C++ receiver, hiển thị video và theo dõi
  receive/decode/render FPS, bitrate, packet count, queue drop và lỗi.
- [x] Giữ bộ nút Start/Stop và các điều khiển nguồn như giao diện receiver hiện
  có; không thêm nút preview/snapshot/fullscreen/reset.
- [x] Tích hợp signaling WebSocket/Bonjour hiện có để nhận CAPABILITIES và gửi
  lệnh SET_RESOLUTION, SET_FPS, SET_CAMERA, SET_BITRATE cho iPhone; các control
  chỉ bật khi thiết bị kết nối và gửi capability hợp lệ.
- [x] Giữ nút cấu hình firewall LAN hiện có cho signaling TCP 8080/Bonjour UDP
  5353; quy tắc giới hạn LocalSubnet và profile Private/Domain.
- [x] Cấu hình timeout, reconnect/delay và giới hạn packet queue qua các trường
  cấu hình.
- [x] Phân biệt chỉ số RTSP có thể đo với thông số sender/WebRTC không được
  receiver cung cấp; các thông số không có dữ liệu hiển thị N/A thay vì số giả.

### Giai đoạn 3 - Kiểm thử receiver độc lập

- [x] CLI báo lỗi rõ ràng khi kết nối tới RTSP endpoint không mở (đã thử trên
  `127.0.0.1:1`); chưa thay thế cho kiểm thử camera thật.
- [x] Build ứng dụng WPF và native receiver Release/Debug x64 thành công; smoke
  test mở cửa sổ, nạp native DLL, gọi `start_ex` và xác nhận endpoint đóng trả
  lỗi rõ ràng.
- [ ] Kiểm thử với RTSP test stream/clip và camera thật; xác nhận packet H.264 có
  thể decode bằng decoder hiện tại của DLL.
- [ ] Kiểm thử cả UDP và TCP; ghi lại packet loss, frame rate, bitrate, latency
  và CPU để so với baseline.
- [ ] Kiểm thử URL sai, mất mạng, camera tắt, stream dừng packet, timeout, Stop
  trong lúc đang đọc và Start/Stop nhiều lần.
- [ ] Kiểm tra frame output: kích thước packet hợp lệ, timestamp tăng đúng, frame
  number không lặp, queue không vượt giới hạn, không memory leak.
- [ ] Chỉ chuyển sang tích hợp khi các test trên pass và đã ghi rõ sai lệch so với
  baseline.

### Giai đoạn 4 - Adapter và tích hợp vào DLL đích

- [ ] Thêm adapter trong `RtspCamera` để receiver mới cung cấp packet vào queue
  `BaseCamera`, giữ nguyên chu kỳ sử dụng của `GetFrame`/`GetLatestFrame`.
- [ ] Thay từng phần nhận RTSP cũ: khởi tạo/kết nối FFmpeg, `readFramePacket`,
  `monitorAndGrabRtspFrame`, giải phóng stream và logic `StartStream`/`StopStream`.
- [ ] Giữ nguyên và kiểm thử riêng HTTP camera control (`/camera-settings`,
  `/camera-toggle`, `/bandwidth`), audio TCP và các API setting; không để việc
  thay receiver làm đổi hành vi các tính năng này.
- [ ] Dùng cùng một nguồn cấu hình protocol/URL; tránh receiver tự sửa setting
  camera hoặc ghi đè trạng thái do `GetSetting` quản lý.
- [ ] Đảm bảo Stop/destructor hủy thread và giải phóng queue/FFmpeg resources trước
  khi đối tượng `RtspCamera` bị hủy; xử lý lỗi khởi tạo một phần.
- [ ] Cập nhật project Visual Studio và dependency/runtime deployment để DLL và
  executable tiêu thụ có thể tìm thấy đúng FFmpeg DLL.

### Giai đoạn 5 - Regression, rollout và dọn dẹp

- [ ] Build DLL Debug và Release x64; chạy các test camera/recording/decoder đang
  có và xác nhận không thay đổi API public.
- [ ] Regression các thao tác Start, Stop, bật/tắt RTSP server, reconnect, đổi
  settings, optimize blur và audio.
- [ ] So sánh frame rate, latency, CPU, memory và độ ổn định với baseline; đặt
  ngưỡng chấp nhận trước khi rollout.
- [ ] Test chạy dài và test nhiều camera đồng thời; kiểm tra tránh deadlock, frame
  stale và tranh chấp khi Stop trong lúc `GetFrame`.
- [ ] Chạy song song receiver cũ/mới hoặc duy trì nhánh rollback; chỉ chuyển mặc
  định sau khi test trên camera và máy đích đạt yêu cầu.
- [ ] Xóa code RTSP cũ chỉ sau khi receiver mới được chấp nhận, không còn caller
  nào phụ thuộc và bản rollback đã được lưu.
- [ ] Cập nhật tài liệu build/deploy, cách cấu hình RTSP, log lỗi và quy trình
  troubleshooting cho receiver mới.

## Điều kiện hoàn tất

- [ ] Receiver native độc lập build được x64 và nhận được RTSP từ camera mục tiêu.
- [ ] `RtspCamera` vẫn tương thích với các caller hiện tại và cấp `RawH264Frame`
  hợp lệ qua API frame đang có.
- [ ] Start/Stop/reconnect và cleanup pass test; không có memory leak, deadlock
  hoặc queue không giới hạn.
- [ ] Các chức năng ngoài phạm vi nhận RTSP (HTTP settings, audio, recording và
  decoder downstream) regression pass.
- [ ] Có kết quả so sánh baseline và hướng rollback rõ ràng trước khi rollout.
