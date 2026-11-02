using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace WindowsVideoReceiver.Controls;

public sealed class StreamingControlViewModel : INotifyPropertyChanged
{
    private string _connectionStatus = "iPhone not connected";
    private string _bridgeStatus = "Initializing WebView2";
    private string _discoveryStatus = "Initializing Bonjour";
    private string _manualAddress = "Detecting network address...";
    private string _receiveFps = "--";
    private string _decodeFps = "--";
    private string _renderFps = "--";
    private string _captureFps = "--";
    private string _encodeFps = "--";
    private string _bitrate = "--";
    private string _senderBitrate = "--";
    private string _packetLoss = "--";
    private string _senderPacketLoss = "--";
    private string _jitter = "--";
    private string _roundTripTime = "--";
    private string _senderRoundTripTime = "--";
    private string _lastError = string.Empty;
    private string _videoDiagnostics = string.Empty;
    private readonly Queue<string> _videoDiagnosticEntries = new();
    private string _selectedCamera = string.Empty;
    private ResolutionOption? _selectedResolution;
    private int? _selectedFrameRate;
    private double _bitrateMbps = 15;
    private bool _isSignalingConnected;
    private bool _isStreaming;
    private bool _hasCapabilities;

    public ObservableCollection<ResolutionOption> Resolutions { get; } = new();

    public ObservableCollection<int> FrameRates { get; } = new();

    public ObservableCollection<string> Cameras { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ConnectionStatus
    {
        get => _connectionStatus;
        set => SetProperty(ref _connectionStatus, value);
    }

    public string DiscoveryStatus
    {
        get => _discoveryStatus;
        set => SetProperty(ref _discoveryStatus, value);
    }

    public string ManualAddress
    {
        get => _manualAddress;
        set => SetProperty(ref _manualAddress, value);
    }

    public string BridgeStatus
    {
        get => _bridgeStatus;
        set => SetProperty(ref _bridgeStatus, value);
    }

    public string ReceiveFps
    {
        get => _receiveFps;
        set => SetProperty(ref _receiveFps, value);
    }

    public string DecodeFps
    {
        get => _decodeFps;
        set => SetProperty(ref _decodeFps, value);
    }

    public string RenderFps
    {
        get => _renderFps;
        set => SetProperty(ref _renderFps, value);
    }

    public string CaptureFps
    {
        get => _captureFps;
        set => SetProperty(ref _captureFps, value);
    }

    public string EncodeFps
    {
        get => _encodeFps;
        set => SetProperty(ref _encodeFps, value);
    }

    public string Bitrate
    {
        get => _bitrate;
        set => SetProperty(ref _bitrate, value);
    }

    public string SenderBitrate
    {
        get => _senderBitrate;
        set => SetProperty(ref _senderBitrate, value);
    }

    public string PacketLoss
    {
        get => _packetLoss;
        set => SetProperty(ref _packetLoss, value);
    }

    public string SenderPacketLoss
    {
        get => _senderPacketLoss;
        set => SetProperty(ref _senderPacketLoss, value);
    }

    public string Jitter
    {
        get => _jitter;
        set => SetProperty(ref _jitter, value);
    }

    public string RoundTripTime
    {
        get => _roundTripTime;
        set => SetProperty(ref _roundTripTime, value);
    }

    public string SenderRoundTripTime
    {
        get => _senderRoundTripTime;
        set => SetProperty(ref _senderRoundTripTime, value);
    }

    public string LastError
    {
        get => _lastError;
        set => SetProperty(ref _lastError, value);
    }

    public string VideoDiagnostics
    {
        get => _videoDiagnostics;
        private set => SetProperty(ref _videoDiagnostics, value);
    }

    public ResolutionOption? SelectedResolution
    {
        get => _selectedResolution;
        set => SetProperty(ref _selectedResolution, value);
    }

    public int? SelectedFrameRate
    {
        get => _selectedFrameRate;
        set => SetProperty(ref _selectedFrameRate, value);
    }

    public string SelectedCamera
    {
        get => _selectedCamera;
        set => SetProperty(ref _selectedCamera, value);
    }

    public double BitrateMbps
    {
        get => _bitrateMbps;
        set
        {
            if (SetProperty(ref _bitrateMbps, value))
            {
                OnPropertyChanged(nameof(BitrateLabel));
            }
        }
    }

    public string BitrateLabel => $"{BitrateMbps:0.#} Mbps";

    public bool IsSignalingConnected
    {
        get => _isSignalingConnected;
        set
        {
            if (SetProperty(ref _isSignalingConnected, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStop));
                OnPropertyChanged(nameof(CanConfigure));
            }
        }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            if (SetProperty(ref _isStreaming, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStop));
            }
        }
    }

    public bool HasCapabilities
    {
        get => _hasCapabilities;
        set
        {
            if (SetProperty(ref _hasCapabilities, value))
            {
                OnPropertyChanged(nameof(CanConfigure));
            }
        }
    }

    public bool CanStart => IsSignalingConnected && !IsStreaming;

    public bool CanStop => IsSignalingConnected && IsStreaming;

    public bool CanConfigure => IsSignalingConnected && HasCapabilities;

    public void ResetStatistics()
    {
        ReceiveFps = "--";
        DecodeFps = "--";
        RenderFps = "--";
        CaptureFps = "--";
        EncodeFps = "--";
        Bitrate = "--";
        SenderBitrate = "--";
        PacketLoss = "--";
        SenderPacketLoss = "--";
        Jitter = "--";
        RoundTripTime = "--";
        SenderRoundTripTime = "--";
    }

    public void AddVideoDiagnostic(string stage, string message)
    {
        var entry = $"{DateTime.Now:HH:mm:ss} [{stage}] {message.ReplaceLineEndings(" ")}";
        _videoDiagnosticEntries.Enqueue(entry);
        while (_videoDiagnosticEntries.Count > 100)
        {
            _videoDiagnosticEntries.Dequeue();
        }

        VideoDiagnostics = string.Join(Environment.NewLine, _videoDiagnosticEntries);
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record ResolutionOption(int Width, int Height)
{
    public override string ToString() => $"{Width} x {Height}";
}
