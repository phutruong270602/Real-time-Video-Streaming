using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsRtspReceiver;

internal sealed class RtspStatisticsClient : IDisposable
{
    private const ushort StatisticsPort = 8556;
    private static readonly byte[] StatisticsRequest = Encoding.ASCII.GetBytes("GET_STATS");
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;

    public event Action<RtspSenderStatistics, double>? StatisticsReceived;
    public event Action<string>? StatusChanged;

    public void Start(Uri streamUri)
    {
        Stop();
        var cancellation = new CancellationTokenSource();
        lock (_sync)
        {
            _cancellation = cancellation;
        }

        _ = PollAsync(streamUri, cancellation.Token);
    }

    public void Stop()
    {
        lock (_sync)
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    public void Dispose() => Stop();

    private async Task PollAsync(Uri streamUri, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(streamUri.DnsSafeHost, cancellationToken)
                .ConfigureAwait(false);
            var address = addresses.FirstOrDefault(candidate =>
                candidate.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses.FirstOrDefault();
            if (address is null)
            {
                StatusChanged?.Invoke("Could not resolve the RTSP server address for statistics.");
                return;
            }

            using var client = new UdpClient(address.AddressFamily);
            client.Connect(new IPEndPoint(address, StatisticsPort));
            var missedResponses = 0;
            StatusChanged?.Invoke($"Waiting for RTSP source statistics from {address}:{StatisticsPort}.");

            while (!cancellationToken.IsCancellationRequested)
            {
                var elapsed = Stopwatch.StartNew();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    await client.SendAsync(StatisticsRequest, timeout.Token).ConfigureAwait(false);
                    var response = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    var statistics = JsonSerializer.Deserialize<RtspSenderStatistics>(response.Buffer);
                    if (statistics is null ||
                        !string.Equals(statistics.Type, "STATS", StringComparison.Ordinal) ||
                        !statistics.HasValidValues)
                    {
                        throw new JsonException("The RTSP server returned an invalid statistics message.");
                    }

                    missedResponses = 0;
                    StatisticsReceived?.Invoke(statistics, elapsed.Elapsed.TotalMilliseconds);
                    StatusChanged?.Invoke($"Receiving live RTSP statistics from {address}.");
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    missedResponses++;
                    if (missedResponses == 1 || missedResponses % 5 == 0)
                    {
                        StatusChanged?.Invoke(
                            $"No RTSP statistics response from {address}:{StatisticsPort} ({missedResponses} missed).");
                    }
                }
                catch (JsonException exception)
                {
                    StatusChanged?.Invoke($"Invalid RTSP statistics response: {exception.Message}");
                }
                catch (SocketException exception)
                {
                    StatusChanged?.Invoke($"Could not receive RTSP statistics: {exception.Message}");
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is SocketException or ArgumentException or InvalidOperationException)
        {
            StatusChanged?.Invoke($"Could not connect to the RTSP statistics endpoint: {exception.Message}");
        }
    }
}

internal sealed record RtspSenderStatistics(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("captureFps")] double CaptureFps,
    [property: JsonPropertyName("encodeFps")] double EncodeFps,
    [property: JsonPropertyName("senderBitrateBps")] double SenderBitrateBps,
    [property: JsonPropertyName("packetLossPercent")] double? PacketLossPercent,
    [property: JsonPropertyName("senderPacketsLost")] int? SenderPacketsLost,
    [property: JsonPropertyName("jitterMs")] double? JitterMs,
    [property: JsonPropertyName("senderRttMs")] double? SenderRttMs)
{
    public bool HasValidValues =>
        double.IsFinite(CaptureFps) &&
        double.IsFinite(EncodeFps) &&
        double.IsFinite(SenderBitrateBps) &&
        (PacketLossPercent is null || double.IsFinite(PacketLossPercent.Value)) &&
        (JitterMs is null || double.IsFinite(JitterMs.Value)) &&
        (SenderRttMs is null || double.IsFinite(SenderRttMs.Value));
}
