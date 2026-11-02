using Makaretu.Dns;
using System.Net;
using System.Net.Sockets;

namespace WindowsRtspReceiver;

internal sealed record RtspServer(string Name, IPAddress Address, ushort Port)
{
    public string DisplayName => $"{Name} - {Address}:{Port}";

    public string Url => new UriBuilder("rtsp", Address.ToString(), Port, "/").Uri.AbsoluteUri;
}

internal sealed class RtspServerDiscovery
{
    public async Task<IReadOnlyList<RtspServer>> DiscoverAsync(
        TimeSpan scanDuration,
        CancellationToken cancellationToken)
    {
        using var discovery = new ServiceDiscovery();
        using var resolutionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        resolutionTimeout.CancelAfter(scanDuration + TimeSpan.FromSeconds(2));
        var servers = new Dictionary<string, RtspServer>(StringComparer.OrdinalIgnoreCase);
        var pendingResolutions = new List<Task>();
        var sync = new object();
        var acceptingResults = true;

        async Task ResolveServiceAsync(DomainName instanceName)
        {
            try
            {
                var srvResponse = await ResolveAsync(
                    discovery.NameServer,
                    instanceName,
                    DnsType.SRV,
                    resolutionTimeout.Token).ConfigureAwait(false);
                var srvRecords = srvResponse.Answers
                    .Concat(srvResponse.AdditionalRecords)
                    .OfType<SRVRecord>()
                    .Where(record => record.Port > 0)
                    .ToArray();

                foreach (var srv in srvRecords)
                {
                    var records = srvResponse.Answers
                        .Concat(srvResponse.AdditionalRecords)
                        .Where(record => record.Name == srv.Target)
                        .OfType<AddressRecord>()
                        .ToList();
                    foreach (var addressType in new[] { DnsType.A, DnsType.AAAA })
                    {
                        if (records.Count > 0)
                        {
                            break;
                        }

                        var addressResponse = await ResolveAsync(
                            discovery.NameServer,
                            srv.Target,
                            addressType,
                            resolutionTimeout.Token).ConfigureAwait(false);
                        records.AddRange(addressResponse.Answers
                            .Concat(addressResponse.AdditionalRecords)
                            .OfType<AddressRecord>());
                    }

                    foreach (var address in records.Select(record => record.Address).Distinct())
                    {
                        var server = new RtspServer(
                            instanceName.Labels.FirstOrDefault() ?? instanceName.ToString().TrimEnd('.'),
                            address,
                            srv.Port);
                        var key = $"{address}:{srv.Port}";
                        lock (sync)
                        {
                            if (acceptingResults)
                            {
                                servers[key] = server;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or SocketException or ArgumentException)
            {
                ReceiverLog.Write(
                    "RTSP_DISCOVERY",
                    $"service_resolution_failed service=\"{instanceName}\" error=\"{exception.Message}\"");
            }
        }

        EventHandler<ServiceInstanceDiscoveryEventArgs> onServiceDiscovered = (_, args) =>
        {
            lock (sync)
            {
                if (!acceptingResults)
                {
                    return;
                }

                pendingResolutions.Add(Task.Run(
                    () => ResolveServiceAsync(args.ServiceInstanceName),
                    cancellationToken));
            }
        };

        discovery.ServiceInstanceDiscovered += onServiceDiscovered;
        try
        {
            discovery.QueryServiceInstances(new DomainName("_rtsp._tcp.local"));
            await Task.Delay(scanDuration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            discovery.ServiceInstanceDiscovered -= onServiceDiscovered;
            lock (sync)
            {
                acceptingResults = false;
            }
        }

        Task[] resolutions;
        lock (sync)
        {
            resolutions = pendingResolutions.ToArray();
        }

        await Task.WhenAll(resolutions).ConfigureAwait(false);
        lock (sync)
        {
            return servers.Values
                .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(server => server.Address.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private static Task<Message> ResolveAsync(
        Makaretu.Dns.Resolving.NameServer nameServer,
        DomainName name,
        DnsType recordType,
        CancellationToken cancellationToken)
    {
        var question = new Question
        {
            Name = name,
            Type = recordType
        };
        var request = new Message();
        request.Questions.Add(question);
        return nameServer.ResolveAsync(request, cancellationToken);
    }
}
