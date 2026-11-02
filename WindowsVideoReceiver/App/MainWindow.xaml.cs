using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using WindowsVideoReceiver.Controls;
using WindowsVideoReceiver.Network;
using Microsoft.Web.WebView2.Core;

namespace WindowsVideoReceiver;

public partial class MainWindow : Window
{
    private readonly StreamingControlViewModel _viewModel = new();
    private readonly SignalingServer _signalingServer = SignalingServer.Instance;
    private readonly Dictionary<(ResolutionOption Resolution, string Camera), HashSet<int>> _frameRatesByMode = new();
    private BonjourServiceAdvertiser? _bonjourAdvertiser;
    private CancellationTokenSource? _bitrateDebounce;
    private ResolutionOption? _appliedResolution;
    private string? _appliedCamera;
    private int? _appliedFrameRate;
    private bool _updatingControls;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        VideoPlayer.StatusChanged += (_, status) => _viewModel.BridgeStatus = status;
        VideoPlayer.MessageReceived += (_, message) => HandleReceiverMessage(message);

        _signalingServer.StatusChanged += (_, state) =>
        {
            Dispatcher.Invoke(() =>
            {
                _viewModel.ConnectionStatus = state;
                _viewModel.IsSignalingConnected = state.StartsWith(
                    "iPhone connected",
                    StringComparison.OrdinalIgnoreCase);
                if (!_viewModel.IsSignalingConnected)
                {
                    _viewModel.IsStreaming = false;
                    _viewModel.ResetStatistics();
                    ClearCapabilities();
                }
            });
        };

        _signalingServer.MessageReceived += (_, payload) =>
            Dispatcher.Invoke(() => HandlePhoneMessage(payload));

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await VideoPlayer.InitializeAsync();
            await _signalingServer.StartAsync();
            _viewModel.ManualAddress = $"ws://{GetLanAddress()}:8080";
            _viewModel.ConnectionStatus = "Waiting for iPhone to connect";

            try
            {
                _bonjourAdvertiser = new BonjourServiceAdvertiser();
                _bonjourAdvertiser.Start();
                _viewModel.DiscoveryStatus = "Advertising _wvr-signal._tcp on the local network.";
            }
            catch (Exception exception)
            {
                _viewModel.DiscoveryStatus = $"Could not advertise Bonjour: {exception.Message}";
            }
        }
        catch (Exception exception) when (
            exception is WebView2RuntimeNotFoundException or
                IOException or
                UnauthorizedAccessException or
                COMException or
                InvalidOperationException or
                SocketException)
        {
            _viewModel.BridgeStatus = $"Initialization error: {exception.Message}";
            MessageBox.Show(
                this,
                exception.Message,
                "Could not initialize receiver",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _bitrateDebounce?.Cancel();
        _bonjourAdvertiser?.Dispose();
        await _signalingServer.StopAsync();
    }

    private void HandleReceiverMessage(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                _viewModel.LastError = "WebRTC message must be a JSON object.";
                return;
            }

            if (!TryGetString(root, "type", out var messageType))
            {
                return;
            }

            if (messageType == "PONG")
            {
                _viewModel.BridgeStatus = "JavaScript ping-pong succeeded";
                return;
            }

            if (messageType == "DIAGNOSTIC")
            {
                var stage = TryGetString(root, "stage", out var diagnosticStage)
                    ? diagnosticStage
                    : "receiver";
                var message = TryGetString(root, "message", out var diagnosticMessage)
                    ? diagnosticMessage
                    : "Diagnostic has no content.";
                _viewModel.AddVideoDiagnostic(stage, message);
                return;
            }

            if (messageType == "STATS")
            {
                UpdateReceiverStatistics(root);
                return;
            }

            if (messageType == "STATUS")
            {
                UpdateStreamingStatus(root, "WebRTC");
            }
            else if (messageType == "ERROR")
            {
                var error = GetErrorMessage(root);
                _viewModel.LastError = error;
                _viewModel.AddVideoDiagnostic("receiver-error", error);
            }

            if (messageType is "OFFER" or "ICE_CANDIDATE" or "STATUS" or "ERROR")
            {
                _ = SendSignalingMessageAsync(payload, messageType);
            }
        }
        catch (JsonException exception)
        {
            _viewModel.LastError = $"Invalid WebRTC message: {exception.Message}";
        }
    }

    private void HandlePhoneMessage(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                _viewModel.LastError = "Message from iPhone must be a JSON object.";
                return;
            }

            if (!TryGetString(root, "type", out var messageType))
            {
                return;
            }

            if (messageType != "STATS")
            {
                _viewModel.AddVideoDiagnostic("iPhone-signaling", $"Received {messageType} from iPhone.");
            }

            switch (messageType)
            {
                case "CAPABILITIES":
                    ApplyCapabilities(root);
                    break;
                case "CONFIG_APPLIED":
                    ApplyConfiguration(root);
                    break;
                case "STATS":
                    UpdatePhoneStatistics(root);
                    break;
                case "ERROR":
                    _viewModel.LastError = GetErrorMessage(root);
                    break;
                case "STATUS":
                    UpdateStreamingStatus(root, "iPhone");
                    break;
            }

            if (messageType is "ANSWER" or "ICE_CANDIDATE")
            {
                VideoPlayer.PostMessageToJavaScript(payload);
            }
        }
        catch (JsonException exception)
        {
            _viewModel.LastError = $"Invalid message from iPhone: {exception.Message}";
        }
    }

    private void ApplyCapabilities(JsonElement root)
    {
        _updatingControls = true;
        try
        {
            _frameRatesByMode.Clear();
            _viewModel.Resolutions.Clear();
            if (root.TryGetProperty("resolutions", out var resolutions) &&
                resolutions.ValueKind == JsonValueKind.Array)
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

                    var modeCameras = new List<string>();
                    if (TryGetArray(item, "cameras", "camera", out var camerasForMode))
                    {
                        foreach (var camera in camerasForMode.EnumerateArray())
                        {
                            if (camera.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(camera.GetString()))
                            {
                                modeCameras.Add(camera.GetString()!);
                            }
                        }
                    }

                    foreach (var modeCamera in modeCameras)
                    {
                        AddFrameRates(resolution, modeCamera, rates);
                    }
                }
            }

            _viewModel.Cameras.Clear();
            if (root.TryGetProperty("cameras", out var cameras) &&
                cameras.ValueKind == JsonValueKind.Array)
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
                TryGetArray(root, "fps", "frameRates", out var legacyFrameRates))
            {
                var rates = legacyFrameRates.EnumerateArray()
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
            if (string.IsNullOrEmpty(_viewModel.SelectedCamera))
            {
                _viewModel.SelectedCamera = _viewModel.Cameras.FirstOrDefault() ?? string.Empty;
            }

            RefreshFrameRates(_viewModel.SelectedFrameRate);
            _viewModel.HasCapabilities = true;
            _viewModel.LastError = string.Empty;
        }
        finally
        {
            _updatingControls = false;
        }
    }

    private void ApplyConfiguration(JsonElement root)
    {
        _updatingControls = true;
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

            if (TryGetNumber(root, "fps", out var fps))
            {
                var frameRate = (int)fps;
                if (_viewModel.FrameRates.Contains(frameRate))
                {
                    _viewModel.SelectedFrameRate = frameRate;
                }
            }

            if (TryGetNumber(root, "bitrate", out var bitrate))
            {
                _viewModel.BitrateMbps = Math.Clamp(bitrate / 1_000_000, 2, 30);
            }

            _appliedResolution = _viewModel.SelectedResolution;
            _appliedCamera = _viewModel.SelectedCamera;
            _appliedFrameRate = _viewModel.SelectedFrameRate;

            _viewModel.LastError = string.Empty;
        }
        finally
        {
            _updatingControls = false;
        }
    }

    private void UpdateReceiverStatistics(JsonElement root)
    {
        _viewModel.ReceiveFps = FormatMetric(root, "receiveFps", " fps");
        _viewModel.DecodeFps = FormatMetric(root, "decodeFps", " fps");
        _viewModel.RenderFps = FormatMetric(root, "renderFps", " fps");
        _viewModel.Bitrate = FormatMetric(
            root,
            "bitrateBps",
            " bps",
            divideBy: 1_000_000,
            suffixOverride: " Mbps");
        _viewModel.PacketLoss = FormatMetric(root, "packetLossPercent", "%");
        _viewModel.Jitter = FormatMetric(root, "jitterMs", " ms");
        _viewModel.RoundTripTime = FormatMetric(root, "rttMs", " ms");
    }

    private void UpdatePhoneStatistics(JsonElement root)
    {
        _viewModel.CaptureFps = FormatMetric(root, "captureFps", " fps", alternateName: "captureFPS");
        _viewModel.EncodeFps = FormatMetric(root, "encodeFps", " fps", alternateName: "encodeFPS");
        if (TryGetNumber(root, "bitrate", out var bitrate) ||
            TryGetNumber(root, "bitrateBps", out bitrate))
        {
            _viewModel.SenderBitrate = $"{bitrate / 1_000_000:0.00} Mbps";
        }

        if (TryGetNumber(root, "packetLoss", out var packetLoss))
        {
            _viewModel.SenderPacketLoss = $"{packetLoss:0.00}%";
        }

        if (TryGetNumber(root, "rtt", out var roundTripTime))
        {
            _viewModel.SenderRoundTripTime = $"{roundTripTime:0.0} ms";
        }
    }

    private void ClearCapabilities()
    {
        _updatingControls = true;
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
            _updatingControls = false;
        }
    }

    private void UpdateStreamingStatus(JsonElement root, string source)
    {
        if (!TryGetString(root, "state", out var state))
        {
            return;
        }

        _viewModel.ConnectionStatus = $"{source}: {state}";
        _viewModel.AddVideoDiagnostic(source, $"Connection state: {state}.");
        if (state.Equals("connected", StringComparison.OrdinalIgnoreCase))
        {
            _viewModel.IsStreaming = true;
            if (source == "WebRTC")
            {
                _viewModel.LastError = string.Empty;
            }
        }
        else if (state.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
                 state.Equals("closed", StringComparison.OrdinalIgnoreCase) ||
                 state.Equals("stopped", StringComparison.OrdinalIgnoreCase))
        {
            _viewModel.IsStreaming = false;
            if (state is "closed" or "stopped")
            {
                _viewModel.ResetStatistics();
            }
        }
    }

    private void AddFrameRates(
        ResolutionOption resolution,
        string camera,
        IEnumerable<int> frameRates)
    {
        var key = _frameRatesByMode.Keys.FirstOrDefault(candidate =>
            candidate.Resolution == resolution &&
            candidate.Camera.Equals(camera, StringComparison.OrdinalIgnoreCase));
        if (key == default)
        {
            key = (resolution, camera);
            _frameRatesByMode[key] = [];
        }

        _frameRatesByMode[key].UnionWith(frameRates);
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

        if (preferredFrameRate is { } preferred && availableRates.Contains(preferred))
        {
            _viewModel.SelectedFrameRate = preferred;
            return;
        }

        var oldRates = GetFrameRates(_appliedResolution, _appliedCamera);
        var transitionRates = availableRates.Intersect(oldRates).ToArray();
        var selectedRate = transitionRates.Contains(60)
            ? 60
            : transitionRates.Contains(30)
                ? 30
                : transitionRates.OrderDescending().FirstOrDefault();
        if (selectedRate == 0)
        {
            selectedRate = availableRates.Contains(60)
                ? 60
                : availableRates[^1];
        }

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

    private static bool TryGetArray(JsonElement root, string firstName, string secondName, out JsonElement value)
    {
        if (root.TryGetProperty(firstName, out value) && value.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        return root.TryGetProperty(secondName, out value) && value.ValueKind == JsonValueKind.Array;
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryGetNumber(JsonElement root, string propertyName, out double value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out var element) &&
               TryGetNumber(element, out value);
    }

    private static bool TryGetNumber(JsonElement element, out double value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number &&
               element.TryGetDouble(out value) &&
               double.IsFinite(value);
    }

    private static string FormatMetric(
        JsonElement root,
        string propertyName,
        string suffix,
        double divideBy = 1,
        string? suffixOverride = null,
        string? alternateName = null)
    {
        if (!TryGetNumber(root, propertyName, out var value) &&
            (alternateName is null || !TryGetNumber(root, alternateName, out value)))
        {
            return "--";
        }

        return $"{value / divideBy:0.##}{suffixOverride ?? suffix}";
    }

    private static string GetErrorMessage(JsonElement root)
    {
        var code = TryGetString(root, "code", out var errorCode) ? $"{errorCode}: " : string.Empty;
        var message = TryGetString(root, "message", out var errorMessage)
            ? errorMessage
            : "Error with no details.";
        return code + message;
    }

    private static IPAddress GetLanAddress()
    {
        var networks = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network =>
                network.OperationalStatus == OperationalStatus.Up &&
                network.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .Select(network => network.GetIPProperties())
            .ToList();

        foreach (var properties in networks.OrderByDescending(properties =>
                     properties.GatewayAddresses.Any(gateway =>
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

    private async Task SendSignalingMessageAsync(string payload, string messageType)
    {
        try
        {
            await _signalingServer.SendAsync(payload);
            _viewModel.AddVideoDiagnostic("signaling", $"Sent {messageType} from WebView to iPhone.");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or SocketException)
        {
            _viewModel.LastError = $"Could not send signaling message: {exception.Message}";
            _viewModel.AddVideoDiagnostic("signaling-error", _viewModel.LastError);
        }
    }

    private async Task SendControlMessageAsync(object message)
    {
        try
        {
            await _signalingServer.SendAsync(JsonSerializer.Serialize(message));
            _viewModel.LastError = string.Empty;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or SocketException)
        {
            _viewModel.LastError = $"Could not send command to iPhone: {exception.Message}";
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsSignalingConnected)
        {
            return;
        }

        await SendControlMessageAsync(new { type = "START" });
        VideoPlayer.StartConnection();
        _viewModel.IsStreaming = true;
        _viewModel.ConnectionStatus = "Stream start request sent";
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await SendControlMessageAsync(new { type = "STOP" });
        VideoPlayer.StopConnection();
        _viewModel.IsStreaming = false;
        _viewModel.ResetStatistics();
    }

    private async void ResolutionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls || !_viewModel.CanConfigure ||
            _viewModel.SelectedResolution is not { } resolution)
        {
            return;
        }

        var oldRates = GetFrameRates(_appliedResolution, _appliedCamera);
        _updatingControls = true;
        try
        {
            RefreshFrameRates(_appliedFrameRate);
        }
        finally
        {
            _updatingControls = false;
        }

        var frameRate = _viewModel.SelectedFrameRate;
        var setFrameRateFirst = frameRate is not null && oldRates.Contains(frameRate.Value);
        if (setFrameRateFirst && frameRate != _appliedFrameRate)
        {
            await SendControlMessageAsync(new { type = "SET_FPS", value = frameRate });
        }

        await SendControlMessageAsync(new
        {
            type = "SET_RESOLUTION",
            width = resolution.Width,
            height = resolution.Height
        });

        if (!setFrameRateFirst && frameRate != _appliedFrameRate && frameRate is not null)
        {
            await SendControlMessageAsync(new { type = "SET_FPS", value = frameRate });
        }
    }

    private async void FrameRateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingControls && _viewModel.CanConfigure &&
            _viewModel.SelectedFrameRate is { } frameRate)
        {
            await SendControlMessageAsync(new { type = "SET_FPS", value = frameRate });
        }
    }

    private async void CameraComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls || !_viewModel.CanConfigure ||
            string.IsNullOrWhiteSpace(_viewModel.SelectedCamera))
        {
            return;
        }

        var oldRates = GetFrameRates(_appliedResolution, _appliedCamera);
        _updatingControls = true;
        try
        {
            RefreshFrameRates(_appliedFrameRate);
        }
        finally
        {
            _updatingControls = false;
        }

        var frameRate = _viewModel.SelectedFrameRate;
        var setFrameRateFirst = frameRate is not null && oldRates.Contains(frameRate.Value);
        if (setFrameRateFirst && frameRate != _appliedFrameRate)
        {
            await SendControlMessageAsync(new { type = "SET_FPS", value = frameRate });
        }

        await SendControlMessageAsync(new
        {
            type = "SET_CAMERA",
            value = _viewModel.SelectedCamera
        });

        if (!setFrameRateFirst && frameRate != _appliedFrameRate && frameRate is not null)
        {
            await SendControlMessageAsync(new { type = "SET_FPS", value = frameRate });
        }
    }

    private async void BitrateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingControls || !_viewModel.CanConfigure)
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
            await SendControlMessageAsync(new
            {
                type = "SET_BITRATE",
                value = (int)Math.Round(_viewModel.BitrateMbps * 1_000_000)
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ConfigureFirewallButton_Click(object sender, RoutedEventArgs e)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "SetupFirewall.ps1");
        if (!File.Exists(scriptPath))
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
            _viewModel.LastError = "Administrator permission requested. Rules apply only to Private/Domain networks.";
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            _viewModel.LastError = "You canceled the Administrator permission request; the firewall was not changed.";
        }
        catch (Win32Exception exception)
        {
            _viewModel.LastError = $"Could not request Administrator permission: {exception.Message}";
        }
    }

    private void SendPing_Click(object sender, RoutedEventArgs e)
    {
        VideoPlayer.SendPing();
    }
}
