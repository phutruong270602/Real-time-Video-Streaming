import Foundation

struct DiscoveredReceiver: Identifiable, Hashable {
    let name: String
    let host: String
    let port: Int

    var id: String { "\(name)|\(host)|\(port)" }

    var webSocketURL: URL? {
        var components = URLComponents()
        components.scheme = "ws"
        components.host = host.trimmingCharacters(in: CharacterSet(charactersIn: "."))
        components.port = port
        return components.url
    }
}

final class BonjourDiscoveryClient: NSObject, ObservableObject, NetServiceDelegate {
    @Published private(set) var receivers: [DiscoveredReceiver] = []
    @Published private(set) var status = "Searching for Windows receivers"

    private let browser = NetServiceBrowser()
    private var services: [String: NetService] = [:]

    override init() {
        super.init()
        browser.delegate = self
    }

    func startSearching() {
        guard services.isEmpty else { return }
        AppLogger.info("Searching for Windows receivers with Bonjour", category: "Bonjour")
        status = "Searching for Windows receivers"
        browser.searchForServices(ofType: "_wvr-signal._tcp.", inDomain: "local.")
    }

    func reloadSearching() {
        stopSearching()
        receivers.removeAll()
        startSearching()
    }

    func stopSearching() {
        AppLogger.info("Stopped Bonjour receiver search", category: "Bonjour")
        browser.stop()
        services.values.forEach {
            $0.delegate = nil
            $0.stop()
        }
        services.removeAll()
    }

    private func serviceKey(_ service: NetService) -> String {
        "\(service.name)|\(service.type)|\(service.domain)"
    }
}

extension BonjourDiscoveryClient: NetServiceBrowserDelegate {
    func netServiceBrowser(
        _ browser: NetServiceBrowser,
        didFind service: NetService,
        moreComing: Bool
    ) {
        let key = serviceKey(service)
        AppLogger.info("Found Bonjour service: \(service.name)", category: "Bonjour")
        services[key] = service
        service.delegate = self
        service.resolve(withTimeout: 8)
    }

    func netServiceBrowser(
        _ browser: NetServiceBrowser,
        didRemove service: NetService,
        moreComing: Bool
    ) {
        let key = serviceKey(service)
        services[key]?.stop()
        services.removeValue(forKey: key)
        receivers.removeAll {
            $0.name == service.name || $0.host == service.hostName
        }
        status = receivers.isEmpty ? "No Windows receivers found" : "Receiver found"
    }

    func netServiceBrowser(
        _ browser: NetServiceBrowser,
        didNotSearch errorDict: [String: NSNumber]
    ) {
        let code = errorDict[NetService.errorCode]?.intValue ?? -1
        AppLogger.error("Bonjour search failed with error \(code)", category: "Bonjour")
        status = "Bonjour search failed (error \(code)). Use manual IP connection."
    }

    func netServiceDidResolveAddress(_ sender: NetService) {
        guard let resolvedHost = sender.hostName else {
            status = "Receiver discovery returned no host name."
            return
        }
        let host = resolvedHost.trimmingCharacters(in: CharacterSet(charactersIn: "."))
        guard !host.isEmpty, sender.port > 0 else {
            status = "Receiver discovery returned an invalid host or port."
            return
        }
        let receiver = DiscoveredReceiver(name: sender.name, host: host, port: sender.port)
        AppLogger.info("Resolved receiver \(sender.name) on port \(sender.port)", category: "Bonjour")
        if !receivers.contains(receiver) {
            receivers.append(receiver)
            receivers.sort { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
        }
        status = "\(receivers.count) Windows receiver(s) found"
    }

    func netService(_ sender: NetService, didNotResolve errorDict: [String: NSNumber]) {
        let code = errorDict[NetService.errorCode]?.intValue ?? -1
        AppLogger.warning("Could not resolve \(sender.name), error \(code)", category: "Bonjour")
        status = "Could not resolve \(sender.name) (error \(code))."
    }
}
