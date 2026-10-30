using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace WindowsVideoReceiver.Views;

public partial class VideoPlayerView : UserControl
{
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? MessageReceived;

    public VideoPlayerView()
    {
        InitializeComponent();
    }

    public async Task InitializeAsync()
    {
        await WebView.EnsureCoreWebView2Async();
        WebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
        WebView.CoreWebView2.NavigationCompleted += (_, args) =>
        {
            if (args.IsSuccess)
            {
                StatusChanged?.Invoke(this, "Trang receiver đã được nạp");
            }
            else
            {
                StatusChanged?.Invoke(this, $"Không thể nạp trang receiver: {args.WebErrorStatus}");
            }
        };

        var pagePath = Path.Combine(AppContext.BaseDirectory, "Streaming", "receiver.html");
        if (!File.Exists(pagePath))
        {
            throw new FileNotFoundException("Không tìm thấy trang WebRTC receiver.", pagePath);
        }

        WebView.CoreWebView2.Navigate(new Uri(pagePath).AbsoluteUri);
    }

    private void SendPing_Click(object sender, RoutedEventArgs e)
    {
        if (WebView.CoreWebView2 is null)
        {
            StatusChanged?.Invoke(this, "WebView2 chưa sẵn sàng");
            return;
        }

        WebView.CoreWebView2.PostWebMessageAsJson("""{"type":"PING"}""");
        StatusChanged?.Invoke(this, "Đã gửi PING tới JavaScript");
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            if (message.RootElement.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                type.GetString() is { } messageType)
            {
                MessageReceived?.Invoke(this, messageType);
            }
            else
            {
                StatusChanged?.Invoke(this, "JavaScript gửi message không có trường type dạng chuỗi");
            }
        }
        catch (JsonException exception)
        {
            StatusChanged?.Invoke(this, $"Message JavaScript không hợp lệ: {exception.Message}");
        }
    }
}
