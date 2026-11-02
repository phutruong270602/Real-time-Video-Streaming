using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace WindowsRtspReceiver.Controls;

public sealed class ReceiverViewModel : INotifyPropertyChanged
{
    private string _connectionStatus = "Not connected";
    private string _codec = "--";
    private string _videoSize = "--";
    private string _receiveFps = "--";
    private string _decodeFps = "--";
    private string _renderFps = "--";
    private string _bitrate = "--";
    private string _captureFps = "--";
    private string _encodeFps = "--";
    private string _senderBitrate = "--";
    private string _packetLoss = "--";
    private string _senderPacketLoss = "--";
    private string _jitter = "--";
    private string _roundTripTime = "--";
    private string _senderRoundTripTime = "--";
    private string _packetsReceived = "0";
    private string _bytesReceived = "0 B";
    private string _packetsDropped = "0";
    private string _queueDropRate = "0.0 packets/s";
    private string _sessionDuration = "00:00:00";
    private string _diagnostic = "Enter an RTSP URL to start receiving video.";
    private string _lastError = string.Empty;
    private string _controlStatus = "Waiting for iPhone control connection.";
    private string _manualAddress = "Detecting network address...";
    private string _selectedCamera = string.Empty;
    private ResolutionOption? _selectedResolution;
    private int? _selectedFrameRate;
    private double _targetBitrateMbps = 15;
    private bool _isControlConnected;
    private bool _hasCapabilities;

    public ObservableCollection<ResolutionOption> Resolutions { get; } = new();
    public ObservableCollection<int> FrameRates { get; } = new();
    public ObservableCollection<string> Cameras { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ConnectionStatus { get => _connectionStatus; set => SetProperty(ref _connectionStatus, value); }
    public string Codec { get => _codec; set => SetProperty(ref _codec, value); }
    public string VideoSize { get => _videoSize; set => SetProperty(ref _videoSize, value); }
    public string ReceiveFps { get => _receiveFps; set => SetProperty(ref _receiveFps, value); }
    public string DecodeFps { get => _decodeFps; set => SetProperty(ref _decodeFps, value); }
    public string RenderFps { get => _renderFps; set => SetProperty(ref _renderFps, value); }
    public string Bitrate { get => _bitrate; set => SetProperty(ref _bitrate, value); }
    public string CaptureFps { get => _captureFps; set => SetProperty(ref _captureFps, value); }
    public string EncodeFps { get => _encodeFps; set => SetProperty(ref _encodeFps, value); }
    public string SenderBitrate { get => _senderBitrate; set => SetProperty(ref _senderBitrate, value); }
    public string PacketLoss { get => _packetLoss; set => SetProperty(ref _packetLoss, value); }
    public string SenderPacketLoss { get => _senderPacketLoss; set => SetProperty(ref _senderPacketLoss, value); }
    public string Jitter { get => _jitter; set => SetProperty(ref _jitter, value); }
    public string RoundTripTime { get => _roundTripTime; set => SetProperty(ref _roundTripTime, value); }
    public string SenderRoundTripTime { get => _senderRoundTripTime; set => SetProperty(ref _senderRoundTripTime, value); }
    public string PacketsReceived { get => _packetsReceived; set => SetProperty(ref _packetsReceived, value); }
    public string BytesReceived { get => _bytesReceived; set => SetProperty(ref _bytesReceived, value); }
    public string PacketsDropped { get => _packetsDropped; set => SetProperty(ref _packetsDropped, value); }
    public string QueueDropRate { get => _queueDropRate; set => SetProperty(ref _queueDropRate, value); }
    public string SessionDuration { get => _sessionDuration; set => SetProperty(ref _sessionDuration, value); }
    public string Diagnostic { get => _diagnostic; set => SetProperty(ref _diagnostic, value); }
    public string LastError { get => _lastError; set => SetProperty(ref _lastError, value); }
    public string ControlStatus
    {
        get => _controlStatus;
        set => SetProperty(ref _controlStatus, value);
    }
    public string ManualAddress { get => _manualAddress; set => SetProperty(ref _manualAddress, value); }
    public string SelectedCamera { get => _selectedCamera; set => SetProperty(ref _selectedCamera, value); }
    public ResolutionOption? SelectedResolution { get => _selectedResolution; set => SetProperty(ref _selectedResolution, value); }
    public int? SelectedFrameRate { get => _selectedFrameRate; set => SetProperty(ref _selectedFrameRate, value); }
    public double TargetBitrateMbps
    {
        get => _targetBitrateMbps;
        set
        {
            if (SetProperty(ref _targetBitrateMbps, value))
            {
                OnPropertyChanged(nameof(TargetBitrateLabel));
            }
        }
    }
    public string TargetBitrateLabel => $"{TargetBitrateMbps:0.#} Mbps";
    public bool IsControlConnected
    {
        get => _isControlConnected;
        set
        {
            if (SetProperty(ref _isControlConnected, value))
            {
                OnPropertyChanged(nameof(CanConfigureSource));
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
                OnPropertyChanged(nameof(CanConfigureSource));
            }
        }
    }
    public bool CanConfigureSource => IsControlConnected && HasCapabilities;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record ResolutionOption(int Width, int Height)
{
    public override string ToString() => $"{Width} x {Height}";
}
