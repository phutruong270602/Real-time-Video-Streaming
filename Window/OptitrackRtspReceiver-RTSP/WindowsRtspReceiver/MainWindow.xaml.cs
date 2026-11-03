using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using WindowsRtspReceiver.Controls;
using WindowsRtspReceiver.Interop;
using WindowsVideoReceiver.Network;

namespace WindowsRtspReceiver;

public partial class MainWindow : Window
{
    private readonly ReceiverViewModel _viewModel = new();
    private readonly SignalingServer _signalingServer = SignalingServer.Instance;
    private readonly Dictionary<(ResolutionOption Resolution, string Camera), HashSet<int>> _frameRatesByMode = new();
    private readonly ObservableCollection<RtspServer> _discoveredRtspServers = [];
    private readonly RtspServerDiscovery _rtspServerDiscovery = new();
    private NativeRtspReceiver? _receiver;
    private CancellationTokenSource? _discoveryCancellation;
    private CancellationTokenSource? _bitrateDebounce;
    private readonly DispatcherTimer _statisticsTimer;
    private readonly DispatcherTimer _videoTimer;
    private readonly Stopwatch _renderClock = Stopwatch.StartNew();
    private readonly Stopwatch _sessionClock = new();
    private WriteableBitmap? _bitmap;
    private ulong _lastFrameSequence;
    private ulong _previousPackets;
    private ulong _previousBytes;
    private ulong _previousDropped;
    private ulong _previousDecodedFrames;
    private ulong _displayPacketBase;
    private ulong _displayBytesBase;
    private ulong _displayDroppedBase;
    private long _previousStatsTimestamp;
    private long _previousRenderTimestamp;
    private long _lastRtspLogTimestamp;
    private long _lastWebRtcLogTimestamp;
    private long _lastPhoneStatsLogTimestamp;
    private int _renderFrames;
    private ulong _lastLoggedFrameSequence;
    private string _lastLoggedRtspError = string.Empty;
    private ResolutionOption? _appliedResolution;
    private string? _appliedCamera;
    private int? _appliedFrameRate;
    private bool _updatingSourceControls;
    private bool _webRtcPageLoaded;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        ReconnectDelayComboBox.IsEnabled = ReconnectCheckBox.IsChecked == true;
        VideoImage.Stretch = Stretch.Uniform;
        RtspServersComboBox.ItemsSource = _discoveredRtspServers;
        ApplyReceiverMode();
        _viewModel.ControlStatus = "RTSP-only mode. Enter a URL from an RTSP server.";

        _statisticsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statisticsTimer.Tick += StatisticsTimer_Tick;
        _videoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _videoTimer.Tick += VideoTimer_Tick;
        ReceiverLog.Write("APP", $"window_created mode={(IsWebRtcMode ? "WebRTC" : "RTSP")} log_file=\"{ReceiverLog.FilePath}\"");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ReceiverLog.Write("APP", $"window_loaded mode={(IsWebRtcMode ? "WebRTC" : "RTSP")} base_directory=\"{AppContext.BaseDirectory}\" current_directory=\"{Environment.CurrentDirectory}\"");
        ReceiverLog.Write("APP", "receiver_mode_locked mode=RTSP");
    }

    private async void DiscoverRtspServersButton_Click(object sender, RoutedEventArgs e)
    {
        _discoveryCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _discoveryCancellation = cancellation;
        DiscoverRtspServersButton.IsEnabled = false;
        _discoveredRtspServers.Clear();
        RtspServersComboBox.SelectedItem = null;
        _viewModel.ControlStatus = "Searching for iPhone RTSP servers on the local network...";
        ReceiverLog.Write(
            "RTSP_DISCOVERY",
            "scan_started service_type=_rtsp._tcp.local protocol=mDNS multicast_udp_port=5353 rtsp_transport=TCP expected_rtsp_port=8554 duration_seconds=7");

        try
        {
            var servers = await _rtspServerDiscovery.DiscoverAsync(
                TimeSpan.FromSeconds(7),
                cancellation.Token);
            foreach (var server in servers)
            {
                _discoveredRtspServers.Add(server);
            }

            if (servers.Count == 0)
            {
                _viewModel.ControlStatus = "No RTSP server found. Check the iPhone sender is running, both devices share the same LAN, and Windows allows Bonjour/mDNS UDP 5353.";
                ReceiverLog.Write(
                    "RTSP_DISCOVERY",
                    "scan_completed servers_found=0 next_check=sender_running_same_lan_multicast_udp_5353_firewall");
            }
            else
            {
                _viewModel.ControlStatus = $"Found {servers.Count} RTSP server(s). Select the iPhone to fill in its RTSP URL.";
                ReceiverLog.Write(
                    "RTSP_DISCOVERY",
                    $"scan_completed servers_found={servers.Count} endpoints=\"{string.Join(",", servers.Select(server => $"{server.Address}:{server.Port}"))}\"");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _viewModel.ControlStatus = "RTSP server search cancelled.";
            ReceiverLog.Write("RTSP_DISCOVERY", "scan_cancelled");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or SocketException or IOException or ArgumentException)
        {
            _viewModel.ControlStatus = "RTSP server search failed.";
            _viewModel.LastError = $"Could not search for RTSP servers: {exception.Message}";
            ReceiverLog.Write(
                "ERROR",
                $"RTSP discovery failed: error=\"{exception}\" mDNS_port=UDP/5353 rtsp_port=TCP/8554");
        }
        finally
        {
            DiscoverRtspServersButton.IsEnabled = true;
            if (ReferenceEquals(_discoveryCancellation, cancellation))
            {
                _discoveryCancellation = null;
            }
        }
    }

    private void RtspServersComboBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RtspServersComboBox.SelectedItem is not RtspServer server)
        {
            return;
        }

        RtspUrlTextBox.Text = server.Url;
        _viewModel.ControlStatus = $"Selected {server.DisplayName}. The RTSP URL has been filled in.";
        _viewModel.Diagnostic = $"The discovered iPhone RTSP URL is ready to connect on TCP port {server.Port}.";
        ReceiverLog.Write("RTSP_DISCOVERY", $"server_selected name=\"{server.Name}\" address={server.Address} port={server.Port} url=\"{server.Url}\"");
    }

    private void SignalingServer_StatusChanged(object? sender, string state)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() => ApplySignalingStatus(state));
    }

    private void SignalingServer_MessageReceived(object? sender, string payload)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() => HandlePhoneControlMessage(payload));
    }

    private void ApplySignalingStatus(string state)
    {
        ReceiverLog.Write("CONTROL_CONNECTION", state);
        _viewModel.ControlStatus = state;
        _viewModel.IsControlConnected = state.StartsWith(
            "iPhone connected",
            StringComparison.OrdinalIgnoreCase);
        if (!_viewModel.IsControlConnected)
        {
            ClearSourceCapabilities();
            if (IsWebRtcMode && WebRtcVideoView.CoreWebView2 is not null)
            {
                WebRtcVideoView.CoreWebView2.PostWebMessageAsJson("""{"type":"STOP"}""");
                SetControls(isStarting: false, isRunning: false);
            }
        }
    }

    private void HandlePhoneControlMessage(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetString(root, "type", out var messageType))
            {
                _viewModel.LastError = "Invalid message received from the iPhone control channel.";
                ReceiverLog.Write("ERROR", _viewModel.LastError);
                return;
            }

            if (messageType is "CAPABILITIES" or "CONFIG_APPLIED" or "ERROR")
            {
                ReceiverLog.Write("CONTROL_RX", root.GetRawText());
            }

            switch (messageType)
            {
                case "CAPABILITIES":
                    ApplySourceCapabilities(root);
                    break;
                case "CONFIG_APPLIED":
                    ApplySourceConfiguration(root);
                    break;
                case "STATS":
                    UpdatePhoneStatistics(root);
                    break;
                case "ERROR":
                    _viewModel.LastError = TryGetString(root, "message", out var message)
                        ? message
                        : "The iPhone reported an unspecified control error.";
                    break;
                case "START_REQUEST":
                    if (IsWebRtcMode && StartButton.IsEnabled)
                    {
                        _ = StartWebRtcConnectionAsync(sendStartCommand: false);
                    }
                    break;
            }

            if (messageType is "ANSWER" or "ICE_CANDIDATE")
            {
                WebRtcVideoView.CoreWebView2?.PostWebMessageAsJson(payload);
            }
        }
        catch (JsonException exception)
        {
            _viewModel.LastError = $"Invalid JSON from iPhone: {exception.Message}";
            ReceiverLog.Write("ERROR", _viewModel.LastError);
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsWebRtcMode)
        {
            ReceiverLog.Write("CONTROL", "start_requested mode=WebRTC");
            await StartWebRtcConnectionAsync(sendStartCommand: true);
            return;
        }

        var url = RtspUrlTextBox.Text.Trim();
        if (!url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase))
        {
            _viewModel.LastError = "Enter a valid rtsp:// or rtsps:// URL.";
            ReceiverLog.Write("ERROR", _viewModel.LastError);
            return;
        }

        SetControls(isStarting: true, isRunning: false);
        _viewModel.ConnectionStatus = "Connecting...";
        _viewModel.Diagnostic = "Opening RTSP stream and waiting for video packets.";
        _viewModel.LastError = string.Empty;

        try
        {
            var useTcp = TransportComboBox.SelectedIndex == 1;
            var receiver = _receiver ??= new NativeRtspReceiver();
            var timeoutMs = GetSelectedTag(TimeoutComboBox, 5000);
            var reconnect = ReconnectCheckBox.IsChecked == true;
            var reconnectDelayMs = GetSelectedTag(ReconnectDelayComboBox, 1000);
            var queueCapacity = GetSelectedTag(QueueCapacityComboBox, 30);
            ReceiverLog.Write("CONTROL", $"start_requested mode=RTSP url=\"{RedactRtspUrl(url)}\" transport={(useTcp ? "TCP" : "UDP")} timeout_ms={timeoutMs} reconnect={reconnect} reconnect_delay_ms={reconnectDelayMs} queue_capacity={queueCapacity}");
            var result = await Task.Run(() =>
            {
                var started = receiver.Start(
                    url,
                    useTcp,
                    timeoutMs,
                    reconnect,
                    reconnectDelayMs,
                    queueCapacity,
                    out var error);
                return (started, error);
            });

            if (!result.started)
            {
                _viewModel.ConnectionStatus = "Connection failed";
                _viewModel.LastError = result.error;
                _viewModel.Diagnostic = "Could not open the RTSP video stream.";
                ReceiverLog.Write("ERROR", $"RTSP start_failed error=\"{result.error}\"");
                SetControls(isStarting: false, isRunning: false);
                return;
            }

            _lastFrameSequence = 0;
            _previousPackets = 0;
            _previousBytes = 0;
            _previousDropped = 0;
            _previousDecodedFrames = 0;
            _displayPacketBase = 0;
            _displayBytesBase = 0;
            _displayDroppedBase = 0;
            _previousStatsTimestamp = _renderClock.ElapsedMilliseconds;
            _previousRenderTimestamp = _previousStatsTimestamp;
            _renderFrames = 0;
            _sessionClock.Restart();
            _bitmap = null;
            VideoImage.Source = null;
            VideoPlaceholder.Visibility = Visibility.Visible;
            _viewModel.ConnectionStatus = "Connected; waiting for first decoded frame";
            _viewModel.Diagnostic = $"RTSP connected over {(useTcp ? "TCP" : "UDP")}.";
            _viewModel.Codec = "H.264";
            ReceiverLog.Write("CONTROL", $"RTSP start_succeeded transport={(useTcp ? "TCP" : "UDP")}");
            SetControls(isStarting: false, isRunning: true);
            _statisticsTimer.Start();
            _videoTimer.Start();
        }
        catch (DllNotFoundException exception)
        {
            HandleStartupException($"Native receiver DLL was not found: {exception.Message}");
        }
        catch (EntryPointNotFoundException exception)
        {
            HandleStartupException($"Native receiver API is incomplete: {exception.Message}");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ArgumentException or
                ExternalException or TaskCanceledException)
        {
            HandleStartupException($"Could not start receiver: {exception.Message}");
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        ReceiverLog.Write("CONTROL", $"stop_requested mode={(IsWebRtcMode ? "WebRTC" : "RTSP")}");
        SetControls(isStarting: true, isRunning: true);
        _statisticsTimer.Stop();
        _videoTimer.Stop();
        _sessionClock.Stop();
        if (IsWebRtcMode)
        {
            WebRtcVideoView.CoreWebView2?.PostWebMessageAsJson("""{"type":"STOP"}""");
            await SendControlMessageAsync(new { type = "STOP" });
            _viewModel.ConnectionStatus = "Stopped";
            _viewModel.Diagnostic = "WebRTC receiver stopped.";
            ReceiverLog.Write("CONTROL", "stop_completed mode=WebRTC");
            SetControls(isStarting: false, isRunning: false);
            return;
        }

        try
        {
            if (_receiver is not null)
            {
                await Task.Run(_receiver.Stop);
            }

            _viewModel.ConnectionStatus = "Stopped";
            _viewModel.Diagnostic = "RTSP receiver stopped.";
            ReceiverLog.Write("CONTROL", "stop_completed mode=RTSP");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ExternalException or TaskCanceledException)
        {
            _viewModel.LastError = $"Could not stop receiver: {exception.Message}";
            ReceiverLog.Write("ERROR", _viewModel.LastError);
        }

        SetControls(isStarting: false, isRunning: false);
    }

    private void StatisticsTimer_Tick(object? sender, EventArgs e)
    {
        if (_receiver is null || !_receiver.TryGetStats(out var stats))
        {
            return;
        }

        var now = _renderClock.ElapsedMilliseconds;
        var elapsedSeconds = Math.Max(0.001, (now - _previousStatsTimestamp) / 1000.0);
        var receiveRate = (stats.PacketsReceived - _previousPackets) / elapsedSeconds;
        var queueDropRate = (stats.PacketsDropped - _previousDropped) / elapsedSeconds;
        var bitrateMbps = (stats.BytesReceived - _previousBytes) * 8.0 / elapsedSeconds / 1_000_000.0;
        var decodeFps = (stats.FramesDecoded - _previousDecodedFrames) / elapsedSeconds;

        _viewModel.ReceiveFps = $"{receiveRate:0.0} packets/s";
        _viewModel.DecodeFps = $"{decodeFps:0.0} FPS";
        _viewModel.Bitrate = $"{bitrateMbps:0.00} Mbps";
        _viewModel.PacketsReceived = (stats.PacketsReceived - _displayPacketBase).ToString("N0");
        _viewModel.BytesReceived = FormatBytes(stats.BytesReceived - _displayBytesBase);
        _viewModel.PacketsDropped = (stats.PacketsDropped - _displayDroppedBase).ToString("N0");
        _viewModel.QueueDropRate = $"{queueDropRate:0.0} packets/s";
        _viewModel.SessionDuration = _sessionClock.Elapsed.ToString(@"hh\:mm\:ss");
        _viewModel.VideoSize = stats.Width > 0 && stats.Height > 0
            ? $"{stats.Width} x {stats.Height}"
            : "--";
        _viewModel.ConnectionStatus = stats.Connected != 0
            ? "Connected"
            : stats.Running != 0 ? "Reconnecting..." : "Stream stopped";
        if (now - _lastRtspLogTimestamp >= 5000)
        {
            ReceiverLog.Write("RTSP_STATS", $"running={stats.Running != 0} connected={stats.Connected != 0} resolution={stats.Width}x{stats.Height} packets={stats.PacketsReceived} bytes={stats.BytesReceived} dropped={stats.PacketsDropped} decoded_frames={stats.FramesDecoded} receive_rate={receiveRate:0.0}_packets_per_s decode_fps={decodeFps:0.0} bitrate={bitrateMbps:0.00}_Mbps");
            _lastRtspLogTimestamp = now;
        }
        if (stats.Running == 0)
        {
            _statisticsTimer.Stop();
            _videoTimer.Stop();
            _sessionClock.Stop();
            SetControls(isStarting: false, isRunning: false);
        }

        var error = _receiver.GetLastError();
        _viewModel.LastError = error;
        if (!string.IsNullOrWhiteSpace(error))
        {
            _viewModel.Diagnostic = error;
            if (!string.Equals(error, _lastLoggedRtspError, StringComparison.Ordinal))
            {
                ReceiverLog.Write("ERROR", error);
                _lastLoggedRtspError = error;
            }
        }
        else
        {
            _lastLoggedRtspError = string.Empty;
        }

        _previousPackets = stats.PacketsReceived;
        _previousBytes = stats.BytesReceived;
        _previousDropped = stats.PacketsDropped;
        _previousDecodedFrames = stats.FramesDecoded;
        _previousStatsTimestamp = now;
    }

    private void VideoTimer_Tick(object? sender, EventArgs e)
    {
        if (_receiver is null ||
            !_receiver.TryGetFrame(out var pixels, out var width, out var height, out var stride, out var sequence) ||
            sequence == _lastFrameSequence)
        {
            return;
        }

        _lastFrameSequence = sequence;
        if (_lastLoggedFrameSequence == 0 || sequence - _lastLoggedFrameSequence >= 30)
        {
            ReceiverLog.Write("VIDEO_RENDER", $"decoded_sequence={sequence} resolution={width}x{height} stride={stride}");
            _lastLoggedFrameSequence = sequence;
        }
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            VideoImage.Source = _bitmap;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        VideoPlaceholder.Visibility = Visibility.Collapsed;
        _viewModel.ConnectionStatus = "Receiving and rendering video";
        _renderFrames++;

        var now = _renderClock.ElapsedMilliseconds;
        var elapsed = now - _previousRenderTimestamp;
        if (elapsed >= 1000)
        {
            _viewModel.RenderFps = $"{_renderFrames * 1000.0 / elapsed:0.0} FPS";
            _renderFrames = 0;
            _previousRenderTimestamp = now;
        }
    }

    private async Task InitializeWebRtcViewAsync()
    {
        await WebRtcVideoView.EnsureCoreWebView2Async();
        WebRtcVideoView.CoreWebView2.WebMessageReceived += WebRtcVideoView_WebMessageReceived;
        WebRtcVideoView.CoreWebView2.NavigationCompleted += (_, args) =>
        {
            if (args.IsSuccess)
            {
                _webRtcPageLoaded = true;
                _viewModel.Diagnostic = "iPhone WebRTC receiver is ready.";
            }
            else
            {
                _viewModel.LastError = $"Could not load WebRTC receiver page: {args.WebErrorStatus}";
            }
        };

        var pagePath = Path.Combine(AppContext.BaseDirectory, "Streaming", "receiver.html");
        if (!File.Exists(pagePath))
        {
            throw new FileNotFoundException(
                "WebRTC receiver page was not copied to the application output.",
                pagePath);
        }

        WebRtcVideoView.CoreWebView2.Navigate(new Uri(pagePath).AbsoluteUri);
    }

    private void WebRtcVideoView_WebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetString(root, "type", out var messageType))
            {
                _viewModel.LastError = "Invalid message from the iPhone WebRTC receiver.";
                ReceiverLog.Write("ERROR", _viewModel.LastError);
                return;
            }

            if (messageType is "DIAGNOSTIC" or "ERROR")
            {
                ReceiverLog.Write("WEBRTC_RX", root.GetRawText());
            }

            switch (messageType)
            {
                case "STATS":
                    UpdateWebRtcStatistics(root);
                    break;
                case "STATUS":
                    UpdateWebRtcStatus(root);
                    break;
                case "DIAGNOSTIC":
                    var stage = TryGetString(root, "stage", out var diagnosticStage)
                        ? diagnosticStage
                        : "WebRTC";
                    var detail = TryGetString(root, "message", out var diagnosticMessage)
                        ? diagnosticMessage
                        : "No diagnostic details were provided.";
                    _viewModel.Diagnostic = $"{stage}: {detail}";
                    if (stage == "video-element" &&
                        detail.StartsWith("Video metadata loaded (", StringComparison.Ordinal))
                    {
                        var dimensions = detail
                            .Split(['(', 'x', ')'], StringSplitOptions.RemoveEmptyEntries);
                        if (dimensions.Length >= 3 &&
                            int.TryParse(dimensions[1], out var width) &&
                            int.TryParse(dimensions[2], out var height))
                        {
                            _viewModel.VideoSize = $"{width} x {height}";
                        }
                    }
                    break;
                case "ERROR":
                    _viewModel.LastError = TryGetString(root, "message", out var errorMessage)
                        ? errorMessage
                        : "The WebRTC receiver reported an unspecified error.";
                    break;
            }

            if (messageType is "OFFER" or "ICE_CANDIDATE" or "STATUS" or "ERROR")
            {
                _ = SendSignalingMessageAsync(e.WebMessageAsJson, messageType);
            }
        }
        catch (JsonException exception)
        {
            _viewModel.LastError = $"Invalid WebRTC message: {exception.Message}";
            ReceiverLog.Write("ERROR", _viewModel.LastError);
        }
    }

    private async Task StartWebRtcConnectionAsync(bool sendStartCommand)
    {
        ReceiverLog.Write("CONTROL", $"webrtc_start_requested send_start_command={sendStartCommand}");
        if (!_viewModel.IsControlConnected)
        {
            _viewModel.LastError = "Connect the iPhone to the WebSocket server before starting WebRTC.";
            ReceiverLog.Write("ERROR", _viewModel.LastError);
            return;
        }
        if (WebRtcVideoView.CoreWebView2 is null || !_webRtcPageLoaded)
        {
            _viewModel.LastError = "The WebRTC receiver page is not ready. Check the WebView2 Runtime and receiver page.";
            ReceiverLog.Write("ERROR", _viewModel.LastError);
            return;
        }

        SetControls(isStarting: true, isRunning: false);
        _viewModel.LastError = string.Empty;
        _viewModel.ConnectionStatus = "Connecting to iPhone...";
        _viewModel.Diagnostic = "Starting WebRTC negotiation with the iPhone.";
        _sessionClock.Restart();
        _viewModel.ReceiveFps = "--";
        _viewModel.DecodeFps = "--";
        _viewModel.RenderFps = "--";
        _viewModel.Bitrate = "--";
        _viewModel.PacketLoss = "--";
        _viewModel.Jitter = "--";
        _viewModel.RoundTripTime = "--";
        _viewModel.PacketsReceived = "0";
        _viewModel.BytesReceived = "0 B";
        _viewModel.VideoSize = "--";

        if (sendStartCommand)
        {
            await SendControlMessageAsync(new { type = "START" });
            if (!string.IsNullOrEmpty(_viewModel.LastError))
            {
                SetControls(isStarting: false, isRunning: false);
                _sessionClock.Stop();
                return;
            }
        }

        WebRtcVideoView.CoreWebView2.PostWebMessageAsJson("""{"type":"START"}""");
        _viewModel.ConnectionStatus = "WebRTC offer sent; waiting for iPhone.";
        ReceiverLog.Write("WEBRTC", "start_sent_to_receiver_page");
        SetControls(isStarting: false, isRunning: true);
    }

    private void UpdateWebRtcStatus(JsonElement root)
    {
        if (!TryGetString(root, "state", out var state))
        {
            return;
        }

        ReceiverLog.Write("WEBRTC_STATUS", state);
        _viewModel.ConnectionStatus = $"WebRTC: {state}";
        if (state.Equals("connected", StringComparison.OrdinalIgnoreCase))
        {
            _viewModel.Codec = "WebRTC";
            _viewModel.LastError = string.Empty;
        }
        else if (state.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
                 state.Equals("closed", StringComparison.OrdinalIgnoreCase) ||
                 state.Equals("stopped", StringComparison.OrdinalIgnoreCase))
        {
            _sessionClock.Stop();
            SetControls(isStarting: false, isRunning: false);
        }
    }

    private void UpdateWebRtcStatistics(JsonElement root)
    {
        var now = _renderClock.ElapsedMilliseconds;
        if (now - _lastWebRtcLogTimestamp >= 5000)
        {
            ReceiverLog.Write("WEBRTC_STATS", root.GetRawText());
            _lastWebRtcLogTimestamp = now;
        }

        if (TryGetNumber(root, "receiveFps", out var receiveFps))
        {
            _viewModel.ReceiveFps = $"{receiveFps:0.0} FPS";
        }
        if (TryGetNumber(root, "decodeFps", out var decodeFps))
        {
            _viewModel.DecodeFps = $"{decodeFps:0.0} FPS";
        }
        if (TryGetNumber(root, "renderFps", out var renderFps))
        {
            _viewModel.RenderFps = $"{renderFps:0.0} FPS";
        }
        if (TryGetNumber(root, "bitrateBps", out var bitrate))
        {
            _viewModel.Bitrate = $"{bitrate / 1_000_000:0.00} Mbps";
        }
        if (TryGetNumber(root, "packetLossPercent", out var packetLoss))
        {
            _viewModel.PacketLoss = $"{packetLoss:0.00}%";
        }
        if (TryGetNumber(root, "jitterMs", out var jitter))
        {
            _viewModel.Jitter = $"{jitter:0.0} ms";
        }
        if (TryGetNumber(root, "rttMs", out var rtt))
        {
            _viewModel.RoundTripTime = $"{rtt:0.0} ms";
        }
        if (TryGetNumber(root, "packetsReceived", out var packets))
        {
            _viewModel.PacketsReceived = packets.ToString("N0");
        }
        if (TryGetNumber(root, "bytesReceived", out var bytes) && bytes >= 0)
        {
            _viewModel.BytesReceived = FormatBytes((ulong)bytes);
        }
        if (TryGetNumber(root, "videoWidth", out var width) &&
            TryGetNumber(root, "videoHeight", out var height) &&
            width > 0 && height > 0)
        {
            _viewModel.VideoSize = $"{(int)width} x {(int)height}";
        }

        _viewModel.SessionDuration = _sessionClock.Elapsed.ToString(@"hh\:mm\:ss");
    }

    private void UpdatePhoneStatistics(JsonElement root)
    {
        var now = _renderClock.ElapsedMilliseconds;
        if (now - _lastPhoneStatsLogTimestamp >= 5000)
        {
            ReceiverLog.Write("PHONE_STATS", root.GetRawText());
            _lastPhoneStatsLogTimestamp = now;
        }

        if (TryGetNumber(root, "captureFps", out var captureFps) ||
            TryGetNumber(root, "captureFPS", out captureFps))
        {
            _viewModel.CaptureFps = $"{captureFps:0.0} FPS";
        }
        if (TryGetNumber(root, "encodeFps", out var encodeFps) ||
            TryGetNumber(root, "encodeFPS", out encodeFps))
        {
            _viewModel.EncodeFps = $"{encodeFps:0.0} FPS";
        }
        if (TryGetNumber(root, "bitrate", out var bitrate) ||
            TryGetNumber(root, "bitrateBps", out bitrate))
        {
            _viewModel.SenderBitrate = $"{bitrate / 1_000_000:0.00} Mbps";
        }
        if (TryGetNumber(root, "packetsLost", out var packetsLost))
        {
            _viewModel.SenderPacketLoss = $"{packetsLost:0} packets";
        }
        if (TryGetNumber(root, "rtt", out var rtt))
        {
            _viewModel.SenderRoundTripTime = $"{rtt:0.0} ms";
        }
    }

    private async Task SendSignalingMessageAsync(string payload, string messageType)
    {
        try
        {
            await _signalingServer.SendAsync(payload);
            ReceiverLog.Write("SIGNALING_TX", $"type={messageType}");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or SocketException or ObjectDisposedException)
        {
            _viewModel.LastError = $"Could not send {messageType} to the iPhone: {exception.Message}";
            ReceiverLog.Write("ERROR", _viewModel.LastError);
        }
    }

    private async Task SendControlMessageAsync(object message)
    {
        try
        {
            var payload = JsonSerializer.Serialize(message);
            ReceiverLog.Write("CONTROL_TX", payload);
            await _signalingServer.SendAsync(payload);
            _viewModel.LastError = string.Empty;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or SocketException or ObjectDisposedException)
        {
            _viewModel.LastError = $"Could not send command to iPhone: {exception.Message}";
            ReceiverLog.Write("ERROR", _viewModel.LastError);
        }
    }

    private bool IsWebRtcMode => false;

    private void ReceiverModeComboBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ReceiverModeComboBox is not null)
        {
            ApplyReceiverMode();
            ReceiverLog.Write("CONTROL", $"receiver_mode_selected mode={(IsWebRtcMode ? "WebRTC" : "RTSP")}");
        }
    }

    private void ApplyReceiverMode()
    {
        var useWebRtc = IsWebRtcMode;
        RtspSettingsPanel.Visibility = useWebRtc ? Visibility.Collapsed : Visibility.Visible;
        WebRtcVideoView.Visibility = useWebRtc ? Visibility.Visible : Visibility.Collapsed;
        VideoImage.Visibility = useWebRtc ? Visibility.Collapsed : Visibility.Visible;
        VideoPlaceholder.Visibility = useWebRtc ? Visibility.Collapsed : Visibility.Visible;
        VideoPlaceholderTitle.Text = useWebRtc ? "Waiting for iPhone video" : "Waiting for RTSP video";
        VideoPlaceholderMessage.Text = useWebRtc
            ? "Connect the iPhone, then press Start"
            : "Enter a camera URL and press Start";
        ReceiverTypeText.Text = useWebRtc ? "WebRTC / WebView2" : "Native C++ / FFmpeg";
        _viewModel.Codec = "--";
        MetricsAvailabilityText.Text = useWebRtc
            ? "WebRTC and iPhone source statistics are shown when reported."
            : "Source capture and WebRTC RTP statistics are unavailable for standalone RTSP.";
        if (!useWebRtc)
        {
            _viewModel.CaptureFps = "N/A (RTSP source stats unavailable)";
            _viewModel.EncodeFps = "N/A (RTSP source stats unavailable)";
            _viewModel.SenderBitrate = "N/A (RTSP sender stats unavailable)";
            _viewModel.SenderPacketLoss = "N/A (RTSP sender stats unavailable)";
            _viewModel.SenderRoundTripTime = "N/A (RTSP sender stats unavailable)";
            _viewModel.PacketLoss = "N/A (RTSP loss stats unavailable)";
            _viewModel.Jitter = "N/A (RTSP jitter stats unavailable)";
            _viewModel.RoundTripTime = "N/A (RTSP RTT stats unavailable)";
        }
        else
        {
            _viewModel.CaptureFps = "--";
            _viewModel.EncodeFps = "--";
            _viewModel.SenderBitrate = "--";
            _viewModel.SenderPacketLoss = "--";
            _viewModel.SenderRoundTripTime = "--";
            _viewModel.PacketLoss = "--";
            _viewModel.Jitter = "--";
            _viewModel.RoundTripTime = "--";
        }
    }

    private void ApplySourceCapabilities(JsonElement root)
    {
        _updatingSourceControls = true;
        try
        {
            _frameRatesByMode.Clear();
            _viewModel.Resolutions.Clear();
            _viewModel.FrameRates.Clear();
            _viewModel.Cameras.Clear();

            if (TryGetArray(root, "resolutions", "resolution", out var resolutions))
            {
                foreach (var item in resolutions.EnumerateArray())
                {
                    var resolution = ParseResolution(item);
                    if (resolution is null)
                    {
                        continue;
                    }

                    if (!_viewModel.Resolutions.Contains(resolution))
                    {
                        _viewModel.Resolutions.Add(resolution);
                    }

                    var rates = new HashSet<int>();
                    if (TryGetArray(item, "fps", "frameRates", out var supportedRates))
                    {
                        foreach (var rate in supportedRates.EnumerateArray())
                        {
                            if (TryGetNumber(rate, out var value) &&
                                value is > 0 and <= 240 &&
                                value == Math.Truncate(value))
                            {
                                rates.Add((int)value);
                            }
                        }
                    }

                    if (TryGetArray(item, "cameras", "camera", out var modeCameras))
                    {
                        foreach (var camera in modeCameras.EnumerateArray())
                        {
                            if (camera.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(camera.GetString()))
                            {
                                AddFrameRates(resolution, camera.GetString()!, rates);
                            }
                        }
                    }
                }
            }

            if (TryGetArray(root, "cameras", "camera", out var cameras))
            {
                foreach (var item in cameras.EnumerateArray())
                {
                    var camera = item.ValueKind == JsonValueKind.String
                        ? item.GetString()
                        : item.ValueKind == JsonValueKind.Object &&
                          TryGetString(item, "value", out var cameraValue)
                            ? cameraValue
                            : null;
                    if (!string.IsNullOrWhiteSpace(camera) &&
                        !_viewModel.Cameras.Contains(camera, StringComparer.OrdinalIgnoreCase))
                    {
                        _viewModel.Cameras.Add(camera);
                    }
                }
            }

            if (_frameRatesByMode.Count == 0 &&
                TryGetArray(root, "fps", "frameRates", out var legacyRates))
            {
                var rates = legacyRates.EnumerateArray()
                    .Where(item => TryGetNumber(item, out var value) &&
                        value is > 0 and <= 240 &&
                        value == Math.Truncate(value))
                    .Select(item =>
                    {
                        TryGetNumber(item, out var value);
                        return (int)value;
                    })
                    .ToHashSet();
                foreach (var resolution in _viewModel.Resolutions)
                {
                    foreach (var camera in _viewModel.Cameras)
                    {
                        AddFrameRates(resolution, camera, rates);
                    }
                }
            }

            _viewModel.SelectedResolution ??= _viewModel.Resolutions.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(_viewModel.SelectedCamera))
            {
                _viewModel.SelectedCamera = _viewModel.Cameras.FirstOrDefault() ?? string.Empty;
            }

            RefreshFrameRates(preferredFrameRate: null);
            _viewModel.HasCapabilities =
                _viewModel.Resolutions.Count > 0 &&
                _viewModel.Cameras.Count > 0;
            if (_viewModel.HasCapabilities)
            {
                _viewModel.ControlStatus = "iPhone connected; source capabilities received.";
                _viewModel.LastError = string.Empty;
            }
            else
            {
                _viewModel.ControlStatus = "iPhone connected, but it did not report usable camera capabilities.";
                _viewModel.LastError = "The iPhone CAPABILITIES message did not include resolution and camera options.";
            }
        }
        finally
        {
            _updatingSourceControls = false;
        }
    }

    private void ApplySourceConfiguration(JsonElement root)
    {
        _updatingSourceControls = true;
        try
        {
            if (TryGetNumber(root, "width", out var width) &&
                TryGetNumber(root, "height", out var height))
            {
                var resolution = new ResolutionOption((int)width, (int)height);
                if (_viewModel.Resolutions.Contains(resolution))
                {
                    _viewModel.SelectedResolution = resolution;
                }
            }

            if (TryGetString(root, "camera", out var camera) &&
                _viewModel.Cameras.Contains(camera, StringComparer.OrdinalIgnoreCase))
            {
                _viewModel.SelectedCamera = camera;
            }

            RefreshFrameRates(TryGetNumber(root, "fps", out var currentFps)
                ? (int)currentFps
                : _viewModel.SelectedFrameRate);
            if (TryGetNumber(root, "fps", out var fps) &&
                _viewModel.FrameRates.Contains((int)fps))
            {
                _viewModel.SelectedFrameRate = (int)fps;
            }

            if (TryGetNumber(root, "bitrate", out var bitrate))
            {
                _viewModel.TargetBitrateMbps = Math.Clamp(bitrate / 1_000_000, 2, 30);
            }

            _appliedResolution = _viewModel.SelectedResolution;
            _appliedCamera = _viewModel.SelectedCamera;
            _appliedFrameRate = _viewModel.SelectedFrameRate;
            _viewModel.LastError = string.Empty;
        }
        finally
        {
            _updatingSourceControls = false;
        }
    }

    private void ClearSourceCapabilities()
    {
        _updatingSourceControls = true;
        try
        {
            _frameRatesByMode.Clear();
            _viewModel.Resolutions.Clear();
            _viewModel.FrameRates.Clear();
            _viewModel.Cameras.Clear();
            _viewModel.SelectedResolution = null;
            _viewModel.SelectedFrameRate = null;
            _viewModel.SelectedCamera = string.Empty;
            _appliedResolution = null;
            _appliedCamera = null;
            _appliedFrameRate = null;
            _viewModel.HasCapabilities = false;
        }
        finally
        {
            _updatingSourceControls = false;
        }
    }

    private void AddFrameRates(
        ResolutionOption resolution,
        string camera,
        IEnumerable<int> rates)
    {
        var key = _frameRatesByMode.Keys.FirstOrDefault(candidate =>
            candidate.Resolution == resolution &&
            candidate.Camera.Equals(camera, StringComparison.OrdinalIgnoreCase));
        if (key == default)
        {
            key = (resolution, camera);
            _frameRatesByMode[key] = [];
        }

        _frameRatesByMode[key].UnionWith(rates);
    }

    private HashSet<int> GetFrameRates(ResolutionOption? resolution, string? camera)
    {
        if (resolution is null || string.IsNullOrWhiteSpace(camera))
        {
            return [];
        }

        return _frameRatesByMode
            .Where(entry =>
                entry.Key.Resolution == resolution &&
                entry.Key.Camera.Equals(camera, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Value)
            .FirstOrDefault() ?? [];
    }

    private void RefreshFrameRates(int? preferredFrameRate)
    {
        var availableRates = GetFrameRates(_viewModel.SelectedResolution, _viewModel.SelectedCamera)
            .Order()
            .ToArray();
        _viewModel.FrameRates.Clear();
        foreach (var frameRate in availableRates)
        {
            _viewModel.FrameRates.Add(frameRate);
        }

        if (availableRates.Length == 0)
        {
            _viewModel.SelectedFrameRate = null;
            return;
        }

        var selectedRate = preferredFrameRate is { } preferred && availableRates.Contains(preferred)
            ? preferred
            : availableRates.Contains(60) ? 60
            : availableRates.Contains(30) ? 30
            : availableRates[^1];
        _viewModel.SelectedFrameRate = selectedRate;
    }

    private static ResolutionOption? ParseResolution(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object &&
            TryGetNumber(item, "width", out var width) &&
            TryGetNumber(item, "height", out var height) &&
            width is > 0 and <= 16384 &&
            height is > 0 and <= 16384)
        {
            return new ResolutionOption((int)width, (int)height);
        }

        if (item.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var parts = item.GetString()?.Split(['x', 'X', '×'], StringSplitOptions.TrimEntries);
        return parts is { Length: 2 } &&
            int.TryParse(parts[0], out var parsedWidth) &&
            int.TryParse(parts[1], out var parsedHeight) &&
            parsedWidth > 0 &&
            parsedHeight > 0
                ? new ResolutionOption(parsedWidth, parsedHeight)
                : null;
    }

    private async void ResolutionComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingSourceControls || !_viewModel.CanConfigureSource ||
            _viewModel.SelectedResolution is not { } resolution)
        {
            return;
        }

        var priorRates = GetFrameRates(_appliedResolution, _appliedCamera);
        _updatingSourceControls = true;
        try
        {
            RefreshFrameRates(_appliedFrameRate);
        }
        finally
        {
            _updatingSourceControls = false;
        }

        var frameRate = _viewModel.SelectedFrameRate;
        var setRateFirst = frameRate is not null && priorRates.Contains(frameRate.Value);
        if (setRateFirst && frameRate != _appliedFrameRate)
        {
            await SendSourceControlAsync(new { type = "SET_FPS", value = frameRate });
        }

        await SendSourceControlAsync(new
        {
            type = "SET_RESOLUTION",
            width = resolution.Width,
            height = resolution.Height
        });

        if (!setRateFirst && frameRate != _appliedFrameRate && frameRate is not null)
        {
            await SendSourceControlAsync(new { type = "SET_FPS", value = frameRate });
        }
    }

    private async void FrameRateComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_updatingSourceControls && _viewModel.CanConfigureSource &&
            _viewModel.SelectedFrameRate is { } frameRate)
        {
            await SendSourceControlAsync(new { type = "SET_FPS", value = frameRate });
        }
    }

    private async void CameraComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingSourceControls || !_viewModel.CanConfigureSource ||
            string.IsNullOrWhiteSpace(_viewModel.SelectedCamera))
        {
            return;
        }

        var priorRates = GetFrameRates(_appliedResolution, _appliedCamera);
        _updatingSourceControls = true;
        try
        {
            RefreshFrameRates(_appliedFrameRate);
        }
        finally
        {
            _updatingSourceControls = false;
        }

        var frameRate = _viewModel.SelectedFrameRate;
        var setRateFirst = frameRate is not null && priorRates.Contains(frameRate.Value);
        if (setRateFirst && frameRate != _appliedFrameRate)
        {
            await SendSourceControlAsync(new { type = "SET_FPS", value = frameRate });
        }

        await SendSourceControlAsync(new { type = "SET_CAMERA", value = _viewModel.SelectedCamera });
        if (!setRateFirst && frameRate != _appliedFrameRate && frameRate is not null)
        {
            await SendSourceControlAsync(new { type = "SET_FPS", value = frameRate });
        }
    }

    private async void TargetBitrateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSourceControls || !_viewModel.CanConfigureSource)
        {
            return;
        }

        _bitrateDebounce?.Cancel();
        _bitrateDebounce?.Dispose();
        _bitrateDebounce = new CancellationTokenSource();
        var cancellationToken = _bitrateDebounce.Token;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            await SendSourceControlAsync(new
            {
                type = "SET_BITRATE",
                value = (int)Math.Round(_viewModel.TargetBitrateMbps * 1_000_000)
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendSourceControlAsync(object message)
    {
        try
        {
            var payload = JsonSerializer.Serialize(message);
            ReceiverLog.Write("CONTROL_TX", payload);
            await _signalingServer.SendAsync(payload);
            _viewModel.LastError = string.Empty;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or SocketException or ObjectDisposedException)
        {
            _viewModel.LastError = $"Could not send camera control to iPhone: {exception.Message}";
            ReceiverLog.Write("ERROR", _viewModel.LastError);
        }
    }

    private void ConfigureFirewallButton_Click(object sender, RoutedEventArgs e)
    {
        var scriptPath = System.IO.Path.Combine(AppContext.BaseDirectory, "SetupFirewall.ps1");
        if (!System.IO.File.Exists(scriptPath))
        {
            _viewModel.LastError = $"Firewall setup script not found: {scriptPath}";
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            Process.Start(startInfo);
            _viewModel.LastError = string.Empty;
            _viewModel.Diagnostic = "Firewall setup requested. Rules apply only to Private/Domain networks.";
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            _viewModel.LastError = "Firewall setup was canceled before administrator approval.";
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _viewModel.LastError = $"Could not configure the LAN firewall: {exception.Message}";
        }
    }

    private static bool TryGetArray(
        JsonElement element,
        string firstName,
        string secondName,
        out JsonElement value)
    {
        if (element.TryGetProperty(firstName, out value) && value.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        return element.TryGetProperty(secondName, out value) && value.ValueKind == JsonValueKind.Array;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryGetNumber(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) &&
            TryGetNumber(property, out value);
    }

    private static bool TryGetNumber(JsonElement element, out double value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number &&
            element.TryGetDouble(out value) &&
            double.IsFinite(value);
    }

    private static IPAddress GetLanAddress()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .Select(adapter => adapter.GetIPProperties())
            .ToList();
        foreach (var properties in interfaces.OrderByDescending(item =>
                     item.GatewayAddresses.Any(gateway =>
                         gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                         !IPAddress.IsLoopback(gateway.Address))))
        {
            var address = properties.UnicastAddresses
                .Select(unicast => unicast.Address)
                .FirstOrDefault(candidate =>
                    candidate.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(candidate) &&
                    !candidate.ToString().StartsWith("169.254.", StringComparison.Ordinal));
            if (address is not null)
            {
                return address;
            }
        }

        return IPAddress.Loopback;
    }

    private void ReconnectCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (ReconnectDelayComboBox is not null)
        {
            ReconnectDelayComboBox.IsEnabled = ReconnectCheckBox.IsChecked == true &&
                StartButton?.IsEnabled == true;
        }
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        ReceiverLog.Write("APP", "window_closing");
        _discoveryCancellation?.Cancel();
        _statisticsTimer.Stop();
        _videoTimer.Stop();
        _sessionClock.Stop();
        WebRtcVideoView.CoreWebView2?.PostWebMessageAsJson("""{"type":"STOP"}""");
        _bitrateDebounce?.Cancel();
        _bitrateDebounce?.Dispose();
        _receiver?.Dispose();
        ReceiverLog.Write("APP", "window_closed");
    }

    private void HandleStartupException(string error)
    {
        _viewModel.ConnectionStatus = "Connection failed";
        _viewModel.LastError = error;
        _viewModel.Diagnostic = "Check the native runtime and RTSP configuration.";
        ReceiverLog.Write("ERROR", error);
        SetControls(isStarting: false, isRunning: false);
    }

    private void SetControls(bool isStarting, bool isRunning)
    {
        StartButton.IsEnabled = !isStarting && !isRunning;
        StopButton.IsEnabled = !isStarting && isRunning;
        ReceiverModeComboBox.IsEnabled = false;
        RtspSettingsPanel.IsEnabled = !isStarting && !isRunning;
        RtspUrlTextBox.IsEnabled = !isStarting && !isRunning;
        TransportComboBox.IsEnabled = !isStarting && !isRunning;
        TimeoutComboBox.IsEnabled = !isStarting && !isRunning;
        ReconnectCheckBox.IsEnabled = !isStarting && !isRunning;
        ReconnectDelayComboBox.IsEnabled = !isStarting && !isRunning &&
            ReconnectCheckBox.IsChecked == true;
        QueueCapacityComboBox.IsEnabled = !isStarting && !isRunning;
    }

    private static int GetSelectedTag(System.Windows.Controls.ComboBox comboBox, int fallback)
    {
        return comboBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string value } &&
            int.TryParse(value, out var parsed)
                ? parsed
                : fallback;
    }

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private static string RedactRtspUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "<invalid-url>";
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.GetLeftPart(UriPartial.Path);
    }
}
