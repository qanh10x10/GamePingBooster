using System.Net;
using System.Net.Sockets;

namespace GamePingBooster.Core.Profiles;

/// <summary>
/// Entries - forwarders in front of a relay - as paths the tunnel measures and connects through
/// exactly like relays, and the rule for when they are worth measuring at all.
///
/// An entry is a machine that forwards UDP, untouched, to its relay (relay/deploy/setup-entry.sh).
/// Nothing on it signs or decrypts anything, so a path through it is the relay itself reached by
/// another road: the same public key, the same session on the relay, a different endpoint. That
/// last part is why a path remembers which relay it ends at - two open paths to one relay are one
/// session, and TunnelEngine.SelectRelayAsync never holds both.
///
/// Written for a VNTT line on 2026-09-13 whose route abroad left Vietnam through Hong Kong: 68 ms to
/// the game direct, 71 through our Singapore relay, while a datacentre in Ho Chi Minh City reached
/// the same relay in 39 ms. The players on lines like that are the ones who never report anything -
/// they see no difference and leave - so the client has to find the entry by itself.
/// </summary>
public static class RelayPaths
{
    /// <summary>
    /// One path per entry of every relay, in profile order. A relay without entries adds nothing,
    /// and neither does an entry missing its id or endpoint.
    /// </summary>
    public static List<RelayEntry> Expand(IEnumerable<RelayEntry> relays)
    {
        var paths = new List<RelayEntry>();
        foreach (var relay in relays)
        {
            foreach (var entry in relay.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Endpoint)) continue;

                paths.Add(new RelayEntry
                {
                    Id = entry.Id,
                    Name = $"{relay.Name} via {(string.IsNullOrWhiteSpace(entry.Location) ? entry.Id : entry.Location)}",
                    // Where the game traffic leaves, which is the relay and not the forwarder.
                    Location = relay.Location,
                    Endpoint = entry.Endpoint,
                    // The forwarder signs nothing; the relay behind it answers the handshake.
                    PublicKey = relay.PublicKey,
                    // A setting of the relay, not of the road to it.
                    EntrySwitching = relay.EntrySwitching,
                    Games = relay.Games,
                    SecondaryGames = relay.SecondaryGames,
                    ViaRelayId = relay.Id,
                });
            }
        }
        return paths;
    }

    /// <summary>
    /// Whether a relay - or a path through an entry, which carries its relay's list - may carry
    /// <paramref name="gameId"/>. An empty list is every game, and so is no game at all: with nothing
    /// known to be playing there is nothing to rule a relay out for.
    /// </summary>
    public static bool Serves(RelayEntry relay, string? gameId) =>
        string.IsNullOrWhiteSpace(gameId) || relay.Games.Count == 0 ||
        relay.Games.Any(g => g.Equals(gameId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a relay may carry a region of <paramref name="gameId"/> through a second tunnel while never
    /// serving it otherwise - <see cref="RelayEntry.SecondaryGames"/>. False for a relay that already serves the
    /// game, and with no game known: the question is only ever asked about the game being played.
    /// </summary>
    public static bool SecondaryOnlyFor(RelayEntry relay, string? gameId) =>
        !string.IsNullOrWhiteSpace(gameId) && !Serves(relay, gameId) &&
        relay.SecondaryGames.Any(g => g.Equals(gameId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The relays that may carry <paramref name="gameId"/>, in profile order. See <see cref="Serves"/>.</summary>
    public static List<RelayEntry> ServingGame(IEnumerable<RelayEntry> relays, string? gameId) =>
        relays.Where(r => Serves(r, gameId)).ToList();

    /// <summary>
    /// The relay a path ends at: a relay's own id, or the id of the relay behind an entry. Two paths
    /// with the same answer are one relayd, and share one session there.
    /// </summary>
    public static string RelayIdOf(RelayEntry path) => path.ViaRelayId ?? path.Id;

    /// <summary>
    /// Every way into one relay - the relay itself, then each entry in front of it - or none when the
    /// profile has no such relay.
    ///
    /// These are the only paths a tunnel may move between while a game runs. The game server sees the
    /// address the relay sends from, and every one of these ends at the same relayd and the same session,
    /// so moving between them changes nothing the server can see. Moving to another RELAY changes that
    /// address, and the match drops.
    /// </summary>
    /// <summary>
    /// The path with this id - a relay, or an entry in front of one - or null. An entry is not in the profile's
    /// relay list, only inside its relay's entries: looked up in the list alone, every way a switch decision named
    /// that was an entry came back "no such way", and no tunnel ever moved onto one (the rig, 2026-09-30).
    /// </summary>
    public static RelayEntry? Find(IReadOnlyList<RelayEntry> relays, string id) =>
        relays.FirstOrDefault(r => r.ViaRelayId is null && r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ??
        Expand(relays).FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static List<RelayEntry> DoorsOf(IEnumerable<RelayEntry> relays, string relayId)
    {
        var relay = relays.FirstOrDefault(r => r.ViaRelayId is null && r.Id.Equals(relayId, StringComparison.OrdinalIgnoreCase));
        if (relay is null) return [];

        var doors = new List<RelayEntry> { relay };
        doors.AddRange(Expand([relay]));
        return doors;
    }

    /// <summary>
    /// How much faster than the player's own connection a path must be to count as helping.
    ///
    /// The same margin tools\Probe-Providers.ps1 uses to say whether a provider is worth a relay: a
    /// few milliseconds is inside what one evening differs from the next, and inside the relay's own
    /// forwarding cost.
    /// </summary>
    public static double HelpMargin(double directMs) => Math.Max(5.0, 0.1 * directMs);

    /// <summary>
    /// True when the best relay does not beat the player's own connection by <see cref="HelpMargin"/>
    /// - the only time entries are worth measuring.
    ///
    /// Scaled to the line rather than a fixed number of milliseconds. 60 ms is excellent to Seoul and
    /// poor to Singapore from Hanoi, and the client already holds the direct figure for this very
    /// line, measured seconds earlier. A line the relays already help never pays for the extra
    /// handshakes.
    /// </summary>
    public static bool WorthTryingEntries(double directMs, double bestEndToEndMs) =>
        bestEndToEndMs > directMs - HelpMargin(directMs);

    /// <summary>
    /// Parses and validates a relay endpoint in "host:port", "ipv4:port", or "[ipv6]:port" form.
    /// Returns null on success, or a human-readable error describing why the endpoint is invalid.
    /// </summary>
    public static string? ValidateEndpoint(string endpoint, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(endpoint))
            return "Endpoint cannot be empty.";

        endpoint = endpoint.Trim();

        if (endpoint.StartsWith('['))
        {
            var closing = endpoint.IndexOf(']');
            if (closing <= 1 || closing >= endpoint.Length - 2 || endpoint[closing + 1] != ':')
            {
                return $"\"{endpoint}\" needs a port, for example 203.0.113.10:51820";
            }
            if (!int.TryParse(endpoint[(closing + 2)..], out port) || port < 1 || port > 65535)
            {
                return $"\"{endpoint}\" does not end in a port between 1 and 65535.";
            }
            var ipPart = endpoint[1..closing];
            if (!IPAddress.TryParse(ipPart, out var ip6) || ip6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return $"\"{endpoint}\" does not contain a valid host name or IP address.";
            }
            host = ipPart;
            return null;
        }

        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || colon == endpoint.Length - 1)
        {
            return $"\"{endpoint}\" needs a port, for example 203.0.113.10:51820";
        }

        if (!int.TryParse(endpoint[(colon + 1)..], out port) || port < 1 || port > 65535)
        {
            return $"\"{endpoint}\" does not end in a port between 1 and 65535.";
        }

        host = endpoint[..colon];

        if (host.Contains(':'))
        {
            return $"\"{endpoint}\" does not contain a valid host name or IP address.";
        }

        var hostType = Uri.CheckHostName(host);
        if (hostType is not (UriHostNameType.Dns or UriHostNameType.IPv4))
        {
            return $"\"{endpoint}\" does not contain a valid host name or IP address.";
        }

        return null;
    }

    /// <summary>
    /// Resolves a configured relay endpoint into numeric "ip:port" form. Literal IP endpoints
    /// are returned as-is without network lookup. Hostnames are resolved to an IPv4 (AF_INET)
    /// address with a bounded timeout.
    /// </summary>
    public static async Task<string> ResolveEndpointAsync(
        string endpoint,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        var err = ValidateEndpoint(endpoint, out var host, out var port);
        if (err is not null)
            throw new FormatException(err);

        // Literal IP addresses bypass DNS entirely
        if (IPAddress.TryParse(host, out var ip))
        {
            return ip.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{ip}]:{port}"
                : $"{ip}:{port}";
        }

        // Bounded cancellable DNS lookup for IPv4 (AF_INET)
        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, linkedCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"DNS resolution for relay host '{host}' timed out.");
        }

        // ponytail: picks first resolved IPv4 address; add Happy Eyeballs/multi-IP racing if multi-homed VPS relays are supported.
        var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (ipv4 is null)
        {
            throw new InvalidOperationException($"No IPv4 address found for relay host '{host}'.");
        }

        return $"{ipv4}:{port}";
    }
}
