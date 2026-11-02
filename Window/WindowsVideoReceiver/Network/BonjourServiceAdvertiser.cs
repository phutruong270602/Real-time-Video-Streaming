using Makaretu.Dns;

namespace WindowsVideoReceiver.Network;

internal sealed class BonjourServiceAdvertiser : IDisposable
{
    private readonly ServiceDiscovery _serviceDiscovery;
    private readonly ServiceProfile _serviceProfile;
    private bool _isDisposed;
    private bool _isAdvertising;

    public BonjourServiceAdvertiser()
    {
        _serviceDiscovery = new ServiceDiscovery();
        _serviceProfile = new ServiceProfile(
            Environment.MachineName,
            "_wvr-signal._tcp",
            8080);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_isAdvertising)
        {
            return;
        }

        _serviceDiscovery.Advertise(_serviceProfile);
        _isAdvertising = true;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_isAdvertising)
        {
            _serviceDiscovery.Unadvertise(_serviceProfile);
        }

        _serviceDiscovery.Dispose();
        _isDisposed = true;
    }
}
