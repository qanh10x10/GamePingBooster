using System.Net;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Profiles;
using GamePingBooster.Service.Discovery;
using GamePingBooster.Service.Network;


namespace GamePingBooster.Service.Tunnel
{
    internal sealed partial class TunnelEngine
    {
        private FullTunnelNetwork? _fullTunnelNetwork;
        private bool _isFullTunnel;

        /// <summary>Routes installed in full-tunnel mode to cover all IPv4 Internet space without deleting default gateway.</summary>
        internal static readonly string[] FullTunnelRoutes = ["0.0.0.0/1", "128.0.0.0/1"];

        internal bool IsActiveSession => _state is TunnelState.Connected or TunnelState.Connecting or TunnelState.Reconnecting;
        internal bool ActiveFullTunnel => IsActiveSession ? _isFullTunnel : _config.FullTunnelEnabled;

        internal void SimulateSessionStateForTest(TunnelState state, bool isFullTunnel)
        {
            _state = state;
            _isFullTunnel = isFullTunnel;
        }

        internal void SetFullTunnelNetworkForTest(FullTunnelNetwork? network)
        {
            _fullTunnelNetwork = network;
        }

        internal FullTunnelNetwork? FullTunnelNetworkForTest => _fullTunnelNetwork;

        internal void SetProfileForTest(ProfileBundle profile)
        {
            _profile = profile;
        }

        /// <summary>
        /// Test hook avoiding reading %ProgramData% secrets or touching native network during tests.
        /// </summary>
        internal TunnelEngine(ServiceConfig config, Action<string> log, bool isolatedForTest)
        {
            _config = config;
            _log = log;
            if (isolatedForTest)
            {
                _clientId = 1;
                var ecdsa = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
                var ctor = typeof(DeviceIdentity).GetConstructor(
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                    [typeof(System.Security.Cryptography.ECDsa)])!;
                _device = (DeviceIdentity)ctor.Invoke([ecdsa]);
                _token = null;
                _presence = new PresenceReporter(() => _config.LicenceUrl, () => null, _device.Key, () => false, log);
            }
            else
            {
                _clientId = ClientIdentity.Load();
                _device = DeviceIdentity.LoadOrCreate(log);
                _token = TokenStore.Load(log);
                QualityOutbox.Enabled = config.ShareQuality;
                if (config.DiscoverDestinations != false)
                {
                    _discoveryUploader = new DiscoveryUploader(
                        () => _config.LicenceUrl, () => _token, _device.Key, () => _config.ShareQuality, log);
                    _discovery = new GameDestinationRecorder(
                        () => _tunnel is { } home ? AllGameUdpPackets(home) : null, _discoveryUploader.WhyNotSend, _discoveryUploader.Report, log);
                }
                _presence = new PresenceReporter(() => _config.LicenceUrl, () => _token, _device.Key, () => _config.ShareQuality, log);
            }
        }
    }
}
