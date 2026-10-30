using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using WindowsVideoReceiver.Controls;
using Microsoft.Web.WebView2.Core;

namespace WindowsVideoReceiver;

public partial class MainWindow : Window
{
    private readonly StreamingControlViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        VideoPlayer.StatusChanged += (_, status) => _viewModel.BridgeStatus = status;
        VideoPlayer.MessageReceived += (_, message) =>
        {
            if (message == "PONG")
            {
                _viewModel.BridgeStatus = "JavaScript ping–pong thành công";
            }
        };
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await VideoPlayer.InitializeAsync();
        }
        catch (Exception exception) when (
            exception is WebView2RuntimeNotFoundException or
                IOException or
                UnauthorizedAccessException or
                COMException or
                InvalidOperationException)
        {
            _viewModel.BridgeStatus = $"Lỗi khởi tạo WebView2: {exception.Message}";
            MessageBox.Show(
                this,
                exception.Message,
                "Không thể khởi tạo WebView2",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}