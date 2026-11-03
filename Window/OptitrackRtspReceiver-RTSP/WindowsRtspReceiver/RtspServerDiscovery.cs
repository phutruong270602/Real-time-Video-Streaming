using Makaretu.Dns;
using System.Net;

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
        var multicast = discovery.Mdns;
        var servers = new Dictionary<string, RtspServer>(StringComparer.OrdinalIgnoreCase);
        var services = new Dictionary<string, (string Name, string Target, ushort Port)>(
            StringComparer.OrdinalIgnoreCase);
        var addresses = new Dictionary<string, HashSet<IPAddress>>(StringComparer.OrdinalIgnoreCase);
        var sync = new object();

        void ProcessAnswer(Message message)
        {
            var records = message.Answers.Concat(message.AdditionalRecords).ToArray();
            var srvRecords = records
                .OfType<SRVRecord>()
                .Where(record => record.Port > 0 && IsRtspServiceInstance(record.Name))
                .ToArray();
            var addressRecords = records.OfType<AddressRecord>().ToArray();
            var targetsToResolve = new HashSet<DomainName>();

            lock (sync)
            {
                foreach (var address in addressRecords)
                {
                    var target = address.Name.ToString().TrimEnd('.');
                    if (!addresses.TryGetValue(target, out var knownAddresses))
                    {
                        knownAddresses = [];
                        addresses.Add(target, knownAddresses);
                    }

                    knownAddresses.Add(address.Address);
                }

                foreach (var srv in srvRecords)
                {
                    var instance = srv.Name.ToString();
                    var target = srv.Target.ToString().TrimEnd('.');
                    var name = srv.Name.Labels.FirstOrDefault()
                        ?? srv.Name.ToString().TrimEnd('.');
                    services[instance] = (name, target, srv.Port);

                    if (!addresses.TryGetValue(target, out var knownAddresses) ||
                        knownAddresses.Count == 0)
                    {
                        targetsToResolve.Add(srv.Target);
                    }
                }

                foreach (var service in services.Values)
                {
                    if (!addresses.TryGetValue(service.Target, out var knownAddresses))
                    {
                        continue;
                    }

                    foreach (var address in knownAddresses)
                    {
                        var server = new RtspServer(service.Name, address, service.Port);
                        servers[$"{address}:{service.Port}"] = server;
                    }
                }
            }

            foreach (var target in targetsToResolve)
            {
                multicast.SendQuery(target, type: DnsType.A);
                multicast.SendQuery(target, type: DnsType.AAAA);
            }
        }

        void OnServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs args)
        {
            ReceiverLog.Write(
                "RTSP_DISCOVERY",
                $"service_instance_found name=\"{args.ServiceInstanceName}\"");
            ProcessAnswer(args.Message);
            multicast.SendQuery(args.ServiceInstanceName, type: DnsType.SRV);
        }

        EventHandler<MessageEventArgs> onAnswerReceived = (_, args) => ProcessAnswer(args.Message);
        EventHandler<ServiceInstanceDiscoveryEventArgs> onServiceInstanceDiscovered =
            OnServiceInstanceDiscovered;

        multicast.AnswerReceived += onAnswerReceived;
        discovery.ServiceInstanceDiscovered += onServiceInstanceDiscovered;
        try
        {
            var startedAt = DateTime.UtcNow;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                discovery.QueryServiceInstances(new DomainName("_rtsp._tcp.local"));

                var remaining = scanDuration - (DateTime.UtcNow - startedAt);
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(
                    remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1),
                    cancellationToken).ConfigureAwait(false);
            }
            while (DateTime.UtcNow - startedAt < scanDuration);
        }
        finally
        {
            multicast.AnswerReceived -= onAnswerReceived;
            discovery.ServiceInstanceDiscovered -= onServiceInstanceDiscovered;
        }

        lock (sync)
        {
            return servers.Values
                .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(server => server.Address.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private static bool IsRtspServiceInstance(DomainName name) =>
        name.ToString().TrimEnd('.').EndsWith(
            "._rtsp._tcp.local",
            StringComparison.OrdinalIgnoreCase);
}
