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
                StatusChanged?.Invoke(this, "Receiver page loaded");
            }
            else
            {
                StatusChanged?.Invoke(this, $"Could not load receiver page: {args.WebErrorStatus}");
            }
        };

        var pagePath = Path.Combine(AppContext.BaseDirectory, "Streaming", "receiver.html");
        if (!File.Exists(pagePath))
        {
            throw new FileNotFoundException("WebRTC receiver page not found.", pagePath);
        }

        WebView.CoreWebView2.Navigate(new Uri(pagePath).AbsoluteUri);
    }

    public void PostMessageToJavaScript(string payload)
    {
        if (WebView.CoreWebView2 is null)
        {
            StatusChanged?.Invoke(this, "WebView2 is not ready");
            return;
        }

        WebView.CoreWebView2.PostWebMessageAsJson(payload);
    }

    public void StartConnection()
    {
        PostMessageToJavaScript("""{"type":"START"}""");
    }

    public void StopConnection()
    {
        PostMessageToJavaScript("""{"type":"STOP"}""");
    }

    public void SendPing()
    {
        PostMessageToJavaScript("""{"type":"PING"}""");
        StatusChanged?.Invoke(this, "PING sent to JavaScript");
    }

    private void SendPing_Click(object sender, RoutedEventArgs e)
    {
        SendPing();
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            if (message.RootElement.ValueKind == JsonValueKind.Object &&
                message.RootElement.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                type.GetString() is { } messageType)
            {
                MessageReceived?.Invoke(this, e.WebMessageAsJson);
                if (messageType == "STATUS" && message.RootElement.TryGetProperty("state", out var state) &&
                    state.ValueKind == JsonValueKind.String)
                {
                    StatusChanged?.Invoke(this, $"Signaling state: {state.GetString()}");
                }
            }
            else
            {
                StatusChanged?.Invoke(this, "JavaScript message is missing a string type field");
            }
        }
        catch (JsonException exception)
        {
            StatusChanged?.Invoke(this, $"Invalid JavaScript message: {exception.Message}");
        }
    }
}
