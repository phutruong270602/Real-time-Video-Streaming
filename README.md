# Windows Video Receiver

Ứng dụng WPF nhận video từ iPhone qua WebRTC trên mạng LAN. Tài liệu thiết kế và checklist triển khai:

- [WindowsVideoReceiver_Design.md](./WindowsVideoReceiver_Design.md)
- [WindowsVideoReceiver_Implementation.md](./WindowsVideoReceiver_Implementation.md)
- [Tổng quan hệ thống](./overview.md)

## iPhone Video Sender

Ứng dụng SwiftUI nằm trong [iOS/iPhoneVideoSender](./iOS/iPhoneVideoSender). Mở [project Xcode](./iOS/iPhoneVideoSender/iPhoneVideoSender.xcodeproj) để chạy trên iPhone. Ứng dụng hiện có camera preview/cấu hình capture, Bonjour và kết nối WebSocket thủ công, WebRTC sender H.264, Start/Stop, điều khiển từ Windows và thống kê sender. Theo dõi các bước kiểm thử thiết bị thật trong [iPhoneVideoSender_Implementation.md](./iPhoneVideoSender_Implementation.md).

Xcode cần tải WebRTC iOS SDK **M156** qua Swift Package Manager ở lần build đầu. Có thể build cho simulator bằng:

```sh
xcodebuild -project iOS/iPhoneVideoSender/iPhoneVideoSender.xcodeproj \
  -scheme iPhoneVideoSender \
  -destination 'generic/platform=iOS Simulator' \
  CODE_SIGNING_ALLOWED=NO build
```

Để chạy trên iPhone thật, chọn Team và provisioning profile trong Xcode; mở ứng dụng sẽ xin quyền Camera và Local Network.

## Yêu cầu

- .NET 10 SDK
- Microsoft Edge WebView2 Runtime
- Visual Studio với workload **.NET desktop development** nếu phát triển bằng Visual Studio

## Build và chạy

Từ thư mục gốc:

```powershell
dotnet build .\WindowsVideoReceiver.sln
dotnet run --project .\WindowsVideoReceiver\WindowsVideoReceiver.csproj
```

Ứng dụng hiện có receiver WebRTC trên WebView2, WebSocket signaling server, điều khiển stream theo `CAPABILITIES`, thống kê `getStats()` và quảng bá Bonjour/mDNS `_wvr-signal._tcp`. Các lựa chọn resolution/FPS/camera được lấy từ iPhone; bitrate có thể điều chỉnh trong khoảng 2–30 Mbps.

Để test stream, chạy app Windows và app sender trên iPhone trong cùng mạng LAN, kết nối iPhone tới địa chỉ WebSocket hiển thị trong cửa sổ (hoặc dùng Bonjour), rồi bấm **Start**. Cần app iPhone gửi `CAPABILITIES`, trả lời SDP và gửi video track WebRTC. Nút **Test JavaScript ping–pong** vẫn dùng để kiểm tra cầu nối WebView2.

Nếu video không hiển thị, xem khung **Video diagnostics** trong phần cấu hình: log chỉ rõ signaling, trạng thái WebRTC/ICE, video track, giải mã hay render đang dừng ở bước nào. Log được lưu tại `WindowsVideoReceiver\log\video-diagnostics.log` và cũng xuất hiện trong Debug Output của Visual Studio.

## Mạng LAN và firewall

Port signaling: **8080**. iPhone có thể tìm Windows Receiver qua Bonjour/mDNS hoặc nhập địa chỉ `ws://<Windows-IP>:8080`; cả hai cách dùng cùng WebSocket server. mDNS cần multicast hoạt động trên cùng LAN.

Trong cửa sổ receiver, chọn **Configure LAN firewall (UAC)** để thêm quy tắc inbound TCP `8080` và UDP `5353`. Script yêu cầu quyền Administrator, chỉ áp dụng cho profile **Private/Domain** và giới hạn nguồn đến **LocalSubnet**; profile Public không được mở. Nếu Wi-Fi là mạng cá nhân đáng tin cậy, đặt Network profile thành Private trong Windows Settings trước khi cấu hình firewall. Không đổi profile thành Private trên Wi-Fi công cộng hoặc mạng không tin cậy.

Thông tin môi trường hiện tại và IP Windows (địa chỉ động) được ghi trong [Documents/DevelopmentEnvironment.md](./Documents/DevelopmentEnvironment.md). Hai thiết bị phải ở cùng mạng; nếu mạng chặn multicast mDNS, dùng IP thủ công.
