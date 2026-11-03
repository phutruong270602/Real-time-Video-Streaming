using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WindowsRtspReceiver;

internal sealed record RtspServer(string Name, IPAddress Address, ushort Port)
{
    public string DisplayName => $"{Name} - {Address}:{Port}";

    public string Url => new UriBuilder("rtsp", Address.ToString(), Port, "/").Uri.AbsoluteUri;
}

internal sealed record RtspDiscoveryResult(
    IReadOnlyList<RtspServer> Servers,
    int MessagesReceived,
    int AnnouncementsIgnored);

internal sealed class RtspServerDiscovery
{
    private const ushort DiscoveryPort = 8555;
    private const string AnnouncementPrefix = "IPHONE_RTSP|1|";

    public async Task<RtspDiscoveryResult> DiscoverAsync(
        TimeSpan scanDuration,
        CancellationToken cancellationToken)
    {
        using var udpClient = new UdpClient(AddressFamily.InterNetwork);
        udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

        var servers = new Dictionary<IPAddress, RtspServer>();
        var messagesReceived = 0;
        var announcementsIgnored = 0;
        using var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        scanCancellation.CancelAfter(scanDuration);
        var startedAt = DateTime.UtcNow;

        ReceiverLog.Write(
            "RTSP_DISCOVERY",
            $"scan_started protocol=UDP_subnet_broadcast discovery_udp_port={DiscoveryPort} rtsp_transport=TCP expected_rtsp_port=8554 duration_seconds={scanDuration.TotalSeconds:0.##}");
        ReceiverLog.Write(
            "RTSP_DISCOVERY",
            $"listener_started local_endpoint={udpClient.Client.LocalEndPoint}");

        try
        {
            while (DateTime.UtcNow - startedAt < scanDuration)
            {
                var received = await udpClient.ReceiveAsync(scanCancellation.Token).ConfigureAwait(false);
                messagesReceived++;
                var message = Encoding.UTF8.GetString(received.Buffer);
                if (!TryParseAnnouncement(message, out var port))
                {
                    announcementsIgnored++;
                    ReceiverLog.Write(
                        "RTSP_DISCOVERY",
                        $"announcement_ignored source={received.RemoteEndPoint} payload_length={received.Buffer.Length}");
                    continue;
                }

                servers[received.RemoteEndPoint.Address] =
                    new RtspServer("iPhone RTSP", received.RemoteEndPoint.Address, port);
                ReceiverLog.Write(
                    "RTSP_DISCOVERY",
                    $"announcement_received source={received.RemoteEndPoint} rtsp_port={port}");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        var results = servers.Values
            .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(server => server.Address.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ReceiverLog.Write(
            "RTSP_DISCOVERY",
            $"scan_summary udp_messages_received={messagesReceived} announcements_ignored={announcementsIgnored} servers_found={results.Length}");
        return new RtspDiscoveryResult(results, messagesReceived, announcementsIgnored);
    }

    private static bool TryParseAnnouncement(string message, out ushort port)
    {
        port = 0;
        return message.StartsWith(AnnouncementPrefix, StringComparison.Ordinal) &&
            ushort.TryParse(message.AsSpan(AnnouncementPrefix.Length), out port) &&
            port > 0;
    }
}
