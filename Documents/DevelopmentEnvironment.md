# Development Environment

Thông tin được ghi nhận khi bắt đầu Giai đoạn 0. IP và trạng thái Wi-Fi là thông tin động; kiểm tra lại trước mỗi lần kết nối iPhone.

| Item | Observed value | Status |
|---|---|---|
| Windows | Windows 11 Home | Available |
| .NET SDK | 10.0.403 | Installed |
| WebView2 Runtime | 156.0.4314.50 | Installed |
| Visual Studio | VS 2019 Community 16.11.60 with .NET desktop workload; VS 2022 Community 17.14.41 without that workload | VS 2022 workload pending for .NET 10 development |
| Git | Available on PATH | Installed |
| Wireshark / VLC | Not installed | Pending |
| FFmpeg | 9.0.2 portable, installed for current user | Installed; executable verified |
| GPU | NVIDIA GeForce RTX 4070 Laptop GPU; Intel Arc Graphics | Hardware H.264 decode capable; verify WebView2 decode during testing |
| Monitor refresh rate | 240 Hz at 2560 × 1600 (dxdiag) | Confirmed |
| Windows Wi-Fi | `IncepIT-2_5G-1`, 5 GHz, 802.11ax | Connected; iPhone not verified on same network |
| Windows IPv4 | `192.168.50.236` | Recorded for manual connection; may change |
| Signaling port | `8080` | Selected; used by both discovery and manual connection |
| Bonjour/mDNS service | `_wvr-signal._tcp` on `local.` | Advertised by the Windows receiver while it is running |
| Windows Firewall | WebSocket TCP `8080`; mDNS UDP `5353` | Setup script available; rules are limited to Private/Domain profiles and LocalSubnet; not applied; active Public profile is disabled (Private disabled, Domain enabled) |
| Current network profile | Wi-Fi `IncepIT-2_5G-1` | Public; do not switch to Private unless this is a trusted network |
