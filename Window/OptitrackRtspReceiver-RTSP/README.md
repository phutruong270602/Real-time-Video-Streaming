# Optitrack RTSP Receiver

Native C++ RTSP/H.264 receiver with an RTSP-only C# WPF monitoring application.
The standalone receiver is intended to be tested before integration with
`OptitrackCameraDLL`; it does not include camera HTTP settings or audio. Enter
an RTSP URL from a camera or RTSP server to connect. An iPhone's discovered
Windows address or WebRTC sender does not provide an RTSP stream.

## Requirements

- CMake 3.21 or newer
- A 64-bit C++17 compiler
- The same x64 FFmpeg SDK/version used by the target DLL (headers and libraries
  for `avformat`, `avcodec`, `avutil`, and `swresample`)

## Configure and build (Windows)

Set `FFMPEG_ROOT` to an FFmpeg SDK root containing `include` and `lib` folders.
For the target project's Release FFmpeg package, an example is:

```powershell
cmake -S . -B build -A x64 `
  -DFFMPEG_ROOT="D:\project_protect\IncepIT-TECSWING-Bay-Camera-64bit\OptitrackCameraDLL\packages\ffmpeg"
cmake --build build --config Release
```

The CLI starts after the RTSP stream is opened and reports packet rate, bitrate,
queue drops, connection state, and the most recent error:

```powershell
.\build\Release\optitrack_rtsp_receiver_cli.exe "rtsp://camera/path" udp
```

Use `tcp` instead of `udp` to select interleaved RTSP/TCP. Press Ctrl+C to stop.
For dynamic FFmpeg builds, ensure the matching FFmpeg runtime DLLs are available
beside the executable or on `PATH`.

## C++ API

`optitrack::rtsp::RtspReceiver::start()` opens the stream and returns its error
instead of silently falling back. `wait_pop()` and `try_pop()` transfer an owned
`EncodedVideoPacket` to the caller. The queue is bounded and drops the oldest
packet when full. Packets include wall-clock milliseconds, PTS/DTS, dimensions,
codec ID, and key-frame status. Reconnection is enabled by default; `last_error()`
reports the most recent open/read failure and clears after a successful reconnect.

## C# WPF monitor

The WPF application is locked to **RTSP URL** mode and no longer starts the
iPhone WebSocket signaling server or WebRTC receiver. It contains the URL and
UDP/TCP controls, connection timeout, automatic reconnect and delay, and
encoded packet queue capacity. **Find RTSP servers (Bonjour)** searches the
local network for `_rtsp._tcp.local` DNS-SD advertisements and resolves the
service and its IP address through mDNS, then fills in the discovered RTSP URL.
The iPhone sender must be running on the same Wi-Fi. Guest-network isolation or
a firewall blocking multicast UDP port 5353 can prevent discovery. The monitor shows
connection/codec/resolution, receive/decode/render FPS, bitrate, packet/byte
counts, queue drops and drop rate, session duration, and diagnostics. WebRTC
and iPhone source controls are not available in this RTSP-only client. Its build
automatically configures and builds the native DLL against the x64 FFmpeg SDK.
The project has a default SDK path; if your SDK is elsewhere, provide its path
with the `FFmpegRoot` MSBuild property as shown below.

The application always appends startup, control, configuration, and sampled
statistics to `log/receiver_ui.log`. Native RTSP sessions additionally append
connection, packet, decode, and error events to `log/rtsp_receiver.log`. RTSP
start parameters include the selected transport, timeouts, reconnect settings,
and queue capacity; credentials and URL query strings are omitted from logged
URLs. The native H.264 decoder is initialized with codec configuration data
(including SPS/PPS) discovered while opening the RTSP stream. Packet and
decoded-frame details are sampled on the first item and every 30 items.

```powershell
dotnet build .\Window\OptitrackRtspReceiver-RTSP\WindowsRtspReceiver\WindowsRtspReceiver.csproj -c Release
dotnet run --project .\Window\OptitrackRtspReceiver-RTSP\WindowsRtspReceiver\WindowsRtspReceiver.csproj -c Release
```

If the target repository is at a different location, pass its FFmpeg SDK root as
an MSBuild property. From the repository root, for example:

```powershell
dotnet run --project .\Window\OptitrackRtspReceiver-RTSP\WindowsRtspReceiver\WindowsRtspReceiver.csproj -c Release -p:FFmpegRoot="D:\path\to\ffmpeg-sdk"
```

Open PowerShell at the repository root before using these commands. The project
also requires CMake 3.21+, Visual Studio C++ build tools, and an x64 FFmpeg SDK.
See [iPhone RTSP Video Sender](../../iOS/iPhoneVideoSender-RTSP/README.md) for
the matching iPhone RTSP application.

## Integration boundary

The DLL currently places each FFmpeg video packet into a `RawH264Frame` and marks
it `AV_PIX_FMT_YUVJ420P` for its existing GPU/CPU H.264 decoder path. The receiver
does not depend on DLL headers, OpenCV, WPF, or WebView2; an adapter can map the
packet payload and timestamp into the existing frame queue. Verify the camera's
H.264 packetization and decoder behavior before replacing the old path.
