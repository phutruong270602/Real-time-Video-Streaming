using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WindowsVideoReceiver.Controls;

public sealed class StreamingControlViewModel : INotifyPropertyChanged
{
    private string _connectionStatus = "Chưa kết nối iPhone";
    private string _bridgeStatus = "Đang khởi tạo WebView2";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ConnectionStatus
    {
        get => _connectionStatus;
        set => SetProperty(ref _connectionStatus, value);
    }

    public string BridgeStatus
    {
        get => _bridgeStatus;
        set => SetProperty(ref _bridgeStatus, value);
    }

    private void SetProperty(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
