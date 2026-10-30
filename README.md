# Windows Video Receiver

Ứng dụng WPF nhận video từ iPhone qua WebRTC trên mạng LAN. Tài liệu thiết kế và checklist triển khai:

- [WindowsVideoReceiver_Design.md](./WindowsVideoReceiver_Design.md)
- [WindowsVideoReceiver_Implementation.md](./WindowsVideoReceiver_Implementation.md)
- [Tổng quan hệ thống](./overview.md)

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

Ứng dụng hiện có giao diện Video Display, Streaming Configuration và Performance Monitoring; WebView2 nạp trang receiver nội bộ và nút kiểm tra ping–pong xác nhận cầu nối C# ↔ JavaScript. Nhận video WebRTC, signaling và các điều khiển streaming sẽ được triển khai ở các giai đoạn tiếp theo.

Để kiểm tra cầu nối, chạy ứng dụng rồi chọn **Test JavaScript ping–pong**; trạng thái bên dưới sẽ xác nhận khi nhận được phản hồi `PONG`.

## Mạng phát triển

Port signaling: **8080**. iPhone có thể kết nối theo hai cách: tự động tìm Windows Receiver bằng Bonjour/mDNS (Windows công bố dịch vụ, iPhone duyệt tìm), hoặc nhập IP và port của Windows thủ công. Cả hai cách đều kết nối tới cùng WebSocket signaling server trước khi thiết lập WebRTC. Hai thiết bị cần ở cùng mạng LAN; nếu mạng không chuyển tiếp multicast mDNS, dùng nhập IP thủ công.

Khi triển khai signaling server, cấu hình Windows Firewall cho WebSocket TCP `8080` và mDNS UDP `5353` trong mạng LAN. Thông tin môi trường hiện tại và IP Windows (chỉ là địa chỉ động để dùng thủ công) được ghi trong [Documents/DevelopmentEnvironment.md](./Documents/DevelopmentEnvironment.md).
