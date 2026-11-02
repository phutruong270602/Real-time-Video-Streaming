# Real-time Video Streaming

Repository gồm hai cặp ứng dụng gửi/nhận video độc lập qua WebRTC hoặc RTSP.
Xem thêm [tổng quan hệ thống](./overview.md).

## WebRTC

- Windows receiver: [README](./Window/WindowsVideoReceiver/README.md), [thiết kế](./Window/WindowsVideoReceiver/WindowsVideoReceiver_Design.md), [quy trình triển khai](./Window/WindowsVideoReceiver/WindowsVideoReceiver_Implementation.md).
- iPhone sender: [README](./iOS/iPhoneVideoSender/README.md).

Windows receiver mở WebSocket signaling trên cổng `8080`; chạy ứng dụng này
trước, sau đó kết nối iPhone sender từ cùng mạng LAN.

Từ PowerShell tại thư mục gốc repository, build và chạy Windows receiver:

```powershell
dotnet build .\Window\WindowsVideoReceiver\WindowsVideoReceiver.csproj
dotnet run --project .\Window\WindowsVideoReceiver\WindowsVideoReceiver.csproj
```

Yêu cầu: .NET 10 SDK và Microsoft Edge WebView2 Runtime.

## RTSP

- Windows receiver: [README](./Window/OptitrackRtspReceiver-RTSP/README.md).
- iPhone sender: [README](./iOS/iPhoneVideoSender-RTSP/README.md).

Từ PowerShell tại thư mục gốc repository, chạy Windows RTSP receiver:

```powershell
dotnet run --project .\Window\OptitrackRtspReceiver-RTSP\WindowsRtspReceiver\WindowsRtspReceiver.csproj -c Release
```

Ứng dụng RTSP cần CMake 3.21+, Visual Studio C++ build tools và FFmpeg SDK
x64. Nếu FFmpeg SDK không nằm ở đường dẫn mặc định của project, truyền thêm
`-p:FFmpegRoot="D:\path\to\ffmpeg-sdk"` như hướng dẫn trong README của receiver.

Xcode cần được cài trên macOS để build/run các ứng dụng iOS. Các ứng dụng iPhone
cần quyền Camera và Local Network; hai thiết bị phải ở cùng mạng LAN.
