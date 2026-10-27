using System.Net;
using System.Net.Sockets;
using GamePingBooster.Service.Dns;
using GamePingBooster.Service.Native;

namespace GamePingBooster.Service.Network;

/// <summary>
/// Full-tunnel network orchestration for Windows:
/// Sets up IPv6 anti-bypass via dynamic WFP session and binds Windows DNS resolution
/// to the full-tunnel IPv4 resolver via NRPT ('.').
/// Guarantees strict rollback on partial install failure and isolated cleanup that
/// never removes unblock or third-party NRPT rules.
/// </summary>
internal sealed class FullTunnelNetwork : IDisposable
{
    internal delegate IDisposable FirewallInstaller(uint adapterIndex, Action<string>? log);
    internal delegate int NrptInstaller(string resolverAddress, Action<string>? log);
    internal delegate int NrptRemover(Action<string>? log);

    private readonly FirewallInstaller _installFirewall;
    private readonly NrptInstaller _installNrpt;
    private readonly NrptRemover _removeNrpt;
    private readonly Action<string>? _log;

    private IDisposable? _firewallGuard;
    private bool _nrptInstalled;
    private bool _disposed;

    public FullTunnelNetwork(Action<string>? log = null)
        : this(
            (idx, l) => FullTunnelFirewall.Install(idx, l),
            (res, l) => NrptPolicy.InstallFullTunnel(res, l),
            l => NrptPolicy.RemoveFullTunnel(l),
            log)
    {
    }

    internal FullTunnelNetwork(
        FirewallInstaller installFirewall,
        NrptInstaller installNrpt,
        NrptRemover removeNrpt,
        Action<string>? log = null)
    {
        _installFirewall = installFirewall ?? throw new ArgumentNullException(nameof(installFirewall));
        _installNrpt = installNrpt ?? throw new ArgumentNullException(nameof(installNrpt));
        _removeNrpt = removeNrpt ?? throw new ArgumentNullException(nameof(removeNrpt));
        _log = log;
    }

    /// <summary>
    /// Installs IPv6 anti-bypass guard and NRPT full-tunnel DNS rule for '.' pointing to resolverAddress.
    /// Rolls back completely if any step fails or if NRPT does not write the expected rule count (1).
    /// </summary>
    public void Install(uint adapterIndex, string resolverAddress = "1.1.1.1")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_firewallGuard is not null || _nrptInstalled)
        {
            throw new InvalidOperationException("FullTunnelNetwork is already installed.");
        }

        if (string.IsNullOrWhiteSpace(resolverAddress) ||
            !IPAddress.TryParse(resolverAddress, out var ip) ||
            ip.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException(
                $"Resolver address must be a valid IPv4 address, got '{resolverAddress}'.",
                nameof(resolverAddress));
        }

        IDisposable? firewall = null;
        var nrptAttempted = false;
        try
        {
            _log?.Invoke($"FullTunnel: installing IPv6 guard on adapter {adapterIndex}...");
            firewall = _installFirewall(adapterIndex, _log);

            nrptAttempted = true;
            _log?.Invoke($"FullTunnel: installing NRPT '.' -> {resolverAddress}...");
            var written = _installNrpt(resolverAddress, _log);
            if (written != 1)
            {
                throw new InvalidOperationException(
                    $"NRPT write verification failed: expected 1 rule, but {written} were reported written.");
            }

            _firewallGuard = firewall;
            _nrptInstalled = true;
            _log?.Invoke("FullTunnel: network primitives installed successfully.");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"FullTunnel: installation failed ({ex.Message}), rolling back...");
            if (nrptAttempted || _nrptInstalled)
            {
                try
                {
                    _removeNrpt(_log);
                }
                catch (Exception rollbackEx)
                {
                    _log?.Invoke($"FullTunnel: NRPT rollback warning: {rollbackEx.Message}");
                }
                _nrptInstalled = false;
            }

            try
            {
                firewall?.Dispose();
            }
            catch (Exception rollbackEx)
            {
                _log?.Invoke($"FullTunnel: Firewall rollback warning: {rollbackEx.Message}");
            }

            throw;
        }
    }

    /// <summary>
    /// Resets only own full-tunnel NRPT rule and closes the dynamic WFP firewall session.
    /// Safe and idempotent to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_nrptInstalled)
        {
            try
            {
                _removeNrpt(_log);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"FullTunnel: error removing NRPT during Dispose: {ex.Message}");
            }
            _nrptInstalled = false;
        }

        if (_firewallGuard is not null)
        {
            try
            {
                _firewallGuard.Dispose();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"FullTunnel: error disposing Firewall during Dispose: {ex.Message}");
            }
            _firewallGuard = null;
        }
    }

    /// <summary>
    /// Startup cleanup method: removes ONLY full-tunnel NRPT own prefix (HollowPingBooster-FullTunnel-).
    /// Leaves unblock and third-party rules untouched.
    /// </summary>
    public static int CleanupStale(Action<string>? log = null) => NrptPolicy.RemoveFullTunnel(log);

    public static int CleanupLeftovers(Action<string>? log = null) => CleanupStale(log);
}
