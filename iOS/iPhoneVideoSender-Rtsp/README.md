# iPhone RTSP Video Sender

This Xcode project publishes the iPhone camera as an H.264 RTSP stream for
`OptitrackRtspReceiver`. It is separate from the WebRTC sender project.

## Run and connect

1. Build and run `iPhoneVideoSender.xcodeproj` on an iPhone using Xcode.
2. Allow camera and local-network access. Keep the app open and both devices on
   the same LAN.
3. The app starts the RTSP server and shows its URL, normally
   `rtsp://<iPhone-LAN-IP>:8554/`. It advertises `_rtsp._tcp` with Bonjour.
4. In `OptitrackRtspReceiver`, discover and select the iPhone RTSP server, or
   enter the displayed URL manually. The receiver supports RTSP over UDP and
   interleaved TCP.

RTSP control and stream setup use TCP port `8554`; the iPhone advertises this
same port in its Bonjour `_rtsp._tcp` SRV record, and the Windows receiver uses
the advertised port when it builds the URL. Bonjour itself uses the standard
multicast UDP port `5353` for discovery; this is separate from the RTSP port.
UDP RTP uses server port `5004` (with adjacent RTCP port `5005` advertised);
the sender does not consume RTCP reports. If UDP setup fails because port
`5004` is unavailable, select TCP in the Windows receiver.

The default video output is `960 × 1080`, center-cropped from the camera's
`1920 × 1080` capture format. Other available resolutions depend on the
selected camera and frame rate.

## Logs

Expand **Application logs** in the sender to see camera, RTSP control, encoder,
and RTP packet events. The same log is written to the app's Documents directory
as `iPhoneVideoSender.log`.

To continuously copy the log from an iPhone to a Mac with Xcode, run this from
the repository root and replace `iPhone` with the device name or UDID:

```sh
bash ./iOS/iPhoneVideoSender-RTSP/capture-app-logs.sh "iPhone"
```

Press Ctrl+C to stop. The captured log is kept separate at
`iOS/iPhoneVideoSender-RTSP/log/capture-app-logs.log`.
