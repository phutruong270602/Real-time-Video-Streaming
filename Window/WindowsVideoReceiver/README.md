# Windows Video Receiver

This WPF app receives live video from the iPhone Video Sender app over WebRTC
on the same LAN. It provides a WebSocket signaling server, advertises itself
through Bonjour/mDNS, displays video using WebView2, and monitors stream status
and results.

## Requirements

- Windows 10 or 11 (x64)
- .NET 10 SDK
- Microsoft Edge WebView2 Runtime
- Both devices on the same LAN

## Build and run

Open PowerShell at the repository root:

```powershell
dotnet build .\Window\WindowsVideoReceiver\WindowsVideoReceiver.csproj
dotnet run --project .\Window\WindowsVideoReceiver\WindowsVideoReceiver.csproj
```

The app starts a WebSocket signaling server on port `8080`. On the iPhone, open
[iPhone Video Sender](../../iOS/iPhoneVideoSender/README.md), connect to the
WebSocket address shown in the Windows app (or use Bonjour), and start
streaming.

## Documentation

- [Design](./WindowsVideoReceiver_Design.md)
- [Implementation plan](./WindowsVideoReceiver_Implementation.md)
- [Repository overview](../../README.md)
