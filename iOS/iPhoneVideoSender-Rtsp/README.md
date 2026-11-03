# iPhone RTSP Video Sender

This Xcode project publishes the iPhone camera as an H.264 RTSP stream for
`OptitrackRtspReceiver`. It is separate from the WebRTC sender project.

## Run and connect

1. Build and run `iPhoneVideoSender.xcodeproj` on an iPhone using Xcode.
2. Allow camera and local-network access. Keep the app open and both devices on
   the same LAN.
3. The app starts the RTSP server and shows its URL, normally
   `rtsp://<iPhone-LAN-IP>:554/`. It announces server availability using UDP
   broadcast on port `8555`.
4. In `OptitrackRtspReceiver`, discover and select the iPhone RTSP server, or
   enter the displayed URL manually. The receiver supports RTSP over UDP and
   interleaved TCP.

RTSP control and stream setup use TCP port `554`. Discovery sends the fixed
announcement `IPHONE_RTSP|1|554` to the Wi-Fi interface's subnet broadcast
address on UDP port `8555`; Windows uses the packet's source IP to build the
RTSP URL. Both devices must be on the same LAN, and Windows must allow inbound
UDP `8555` on its Private/Domain profile. Guest Wi-Fi, VPN routing, or wireless
client isolation can still prevent broadcast discovery.

The RTSP listener also accepts the HTTP camera-control requests used by
`OptitrackCameraDLL` on that same TCP port. It implements `/ping`,
`/camera-toggle`, and `/camera-settings`; supported frame-rate updates are
applied to the active iPhone camera. Speed, ISO, zoom, audio, bandwidth, and
app-exit controls are not supported. Use the same `rtsp://<iPhone-LAN-IP>:554/`
URL in `RtspCamera` and the Windows receiver.

The sender provides live capture/encode FPS and transmitted bitrate through a
statistics request/reply endpoint on UDP port `8556`. RTCP sender reports are
still used for the RTSP stream, but packet-loss, jitter, and RTT metrics are not
included in statistics responses. If UDP setup fails because port `5004` is
unavailable, select TCP in the Windows receiver.

The default video output is `960 × 1080`, center-cropped from the camera's
`1920 × 1080` capture format. Other available resolutions depend on the
selected camera and frame rate.

## Logs

Expand **Application logs** in the sender to see camera, RTSP control, encoder,
and RTP packet events. The same log is written to the app's Documents directory
as `iPhoneVideoSender.log`.

To continuously copy app logs from an iPhone to a Mac with Xcode, run this from
the repository root and replace `iPhone` with the device name or UDID:

```sh
bash ./iOS/iPhoneVideoSender-Rtsp/capture-app-logs.sh "iPhone"
```

The script resolves the requested name to a connected iPhone. If that name is
unavailable and exactly one iPhone is connected, it selects that device and
records the selection in the log. It appends the same `AppLogger` events shown
in Xcode's console, plus capture errors and status, to
`iOS/iPhoneVideoSender-Rtsp/log/capture-app-logs.log`. Keep the sender app open
while capturing and press Ctrl+C to stop. System-generated AVFoundation/Fig
messages are not written by the app's `AppLogger`; those remain available in
Xcode's device console.
