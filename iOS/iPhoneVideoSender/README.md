# iPhone Video Sender

This SwiftUI app uses the iPhone camera to send H.264 video over WebRTC to the
[Windows Video Receiver](../../Window/WindowsVideoReceiver/README.md). Both
devices must be on the same LAN. This is the WebRTC project; the separate RTSP
app is documented in
[iPhoneVideoSender-RTSP](../iPhoneVideoSender-RTSP/README.md).

## Requirements

- macOS with Xcode
- An iPhone running iOS 17 or later to run the app on a physical device
- WebRTC iOS SDK M156 (downloaded through Swift Package Manager during the first build)

## Open and run in Xcode

Open the following project in Xcode:

```text
iOS/iPhoneVideoSender/iPhoneVideoSender.xcodeproj
```

Select the `iPhoneVideoSender` scheme and your iPhone, then click **Run**. Grant
Camera and Local Network permissions on the first launch. To install the app on
a physical device, configure your Team and provisioning profile in Xcode.

## Build from the command line

Run this command from the repository root on macOS:

```sh
xcodebuild \
  -project iOS/iPhoneVideoSender/iPhoneVideoSender.xcodeproj \
  -scheme iPhoneVideoSender \
  -destination 'generic/platform=iOS Simulator' \
  CODE_SIGNING_ALLOWED=NO build
```

To stream video, start the Windows Video Receiver first, then connect the iPhone
to the receiver using Bonjour or by entering the receiver's WebSocket address.
Allow TCP port `8080` through the Windows firewall if prompted.

## Capture iPhone logs on a Mac

On a Mac with Xcode, run the following script and replace `iPhone` with the
device name or UDID:

```sh
bash ./iOS/iPhoneVideoSender/capture-app-logs.sh "iPhone"
```

Run the script from the repository root and press **Ctrl+C** to stop. The
captured log is saved separately at
`iOS/iPhoneVideoSender/log/capture-app-logs.log`.
