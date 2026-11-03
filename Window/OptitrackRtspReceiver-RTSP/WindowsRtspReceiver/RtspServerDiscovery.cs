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
    private const ushort RtspPort = 8554;

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
        var loggedRecords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sync = new object();
        var messageCount = 0;
        var recordCount = 0;
        var serviceInstanceCount = 0;

        void ProcessAnswer(Message message, IPEndPoint source)
        {
            var records = message.Answers.Concat(message.AdditionalRecords).ToArray();
            var srvRecords = records
                .OfType<SRVRecord>()
                .Where(record => record.Port > 0 && IsRtspServiceInstance(record.Name))
                .ToArray();
            var addressRecords = records.OfType<AddressRecord>().ToArray();
            var targetsToResolve = new HashSet<DomainName>();
            var newRecordDetails = new List<string>();

            lock (sync)
            {
                messageCount++;
                recordCount += records.Length;

                foreach (var address in addressRecords)
                {
                    var target = address.Name.ToString().TrimEnd('.');
                    if (!addresses.TryGetValue(target, out var knownAddresses))
                    {
                        knownAddresses = [];
                        addresses.Add(target, knownAddresses);
                    }

                    knownAddresses.Add(address.Address);
                    var detail = $"address name=\"{target}\" ip={address.Address}";
                    if (loggedRecords.Add(detail))
                    {
                        newRecordDetails.Add(detail);
                    }
                }

                foreach (var srv in srvRecords)
                {
                    var instance = srv.Name.ToString();
                    var target = srv.Target.ToString().TrimEnd('.');
                    var name = srv.Name.Labels.FirstOrDefault()
                        ?? srv.Name.ToString().TrimEnd('.');
                    services[instance] = (name, target, srv.Port);
                    var detail = $"srv instance=\"{instance}\" target=\"{target}\" tcp_port={srv.Port} expected_tcp_port={RtspPort} port_matches_expected={srv.Port == RtspPort}";
                    if (loggedRecords.Add(detail))
                    {
                        newRecordDetails.Add(detail);
                    }

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

            if (records.Length > 0)
            {
                ReceiverLog.Write(
                    "RTSP_DISCOVERY",
                    $"mdns_answer_received source={source} answer_records={message.Answers.Count} additional_records={message.AdditionalRecords.Count}");
            }

            foreach (var detail in newRecordDetails)
            {
                ReceiverLog.Write("RTSP_DISCOVERY", $"mdns_record {detail}");
            }

            foreach (var target in targetsToResolve)
            {
                ReceiverLog.Write(
                    "RTSP_DISCOVERY",
                    $"resolve_host_query target=\"{target}\" types=A,AAAA protocol=mDNS udp_port=5353");
                multicast.SendQuery(target, type: DnsType.A);
                multicast.SendQuery(target, type: DnsType.AAAA);
            }
        }

        void OnServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs args)
        {
            lock (sync)
            {
                serviceInstanceCount++;
            }

            ReceiverLog.Write(
                "RTSP_DISCOVERY",
                $"service_instance_found name=\"{args.ServiceInstanceName}\" source={args.RemoteEndPoint}");
            ProcessAnswer(args.Message, args.RemoteEndPoint);
            ReceiverLog.Write(
                "RTSP_DISCOVERY",
                $"resolve_srv_query name=\"{args.ServiceInstanceName}\" protocol=mDNS udp_port=5353");
            multicast.SendQuery(args.ServiceInstanceName, type: DnsType.SRV);
        }

        EventHandler<MessageEventArgs> onAnswerReceived =
            (_, args) => ProcessAnswer(args.Message, args.RemoteEndPoint);
        EventHandler<ServiceInstanceDiscoveryEventArgs> onServiceInstanceDiscovered =
            OnServiceInstanceDiscovered;

        multicast.AnswerReceived += onAnswerReceived;
        discovery.ServiceInstanceDiscovered += onServiceInstanceDiscovered;
        try
        {
            var startedAt = DateTime.UtcNow;
            var queryCount = 0;
            ReceiverLog.Write(
                "RTSP_DISCOVERY",
                $"scan_configuration service_type=_rtsp._tcp.local ptr_query_name=_rtsp._tcp.local protocol=mDNS multicast_udp_port=5353 rtsp_transport=TCP expected_rtsp_port={RtspPort} duration_seconds={scanDuration.TotalSeconds:0.##}");
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                queryCount++;
                if (queryCount == 1)
                {
                    ReceiverLog.Write(
                        "RTSP_DISCOVERY",
                        "query_sent name=_rtsp._tcp.local type=PTR transport=mDNS multicast_udp_port=5353");
                }

                discovery.QueryServiceInstances(new DomainName("_rtsp._tcp"));

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
            var results = servers.Values
                .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(server => server.Address.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ReceiverLog.Write(
                "RTSP_DISCOVERY",
                $"scan_summary queries_sent={queryCount} mdns_messages_received={messageCount} dns_records_received={recordCount} service_instances={serviceInstanceCount} unique_records_logged={loggedRecords.Count} servers_found={results.Length}");
            return results;
        }
    }

    private static bool IsRtspServiceInstance(DomainName name) =>
        name.ToString().TrimEnd('.').EndsWith(
            "._rtsp._tcp.local",
            StringComparison.OrdinalIgnoreCase);
}
