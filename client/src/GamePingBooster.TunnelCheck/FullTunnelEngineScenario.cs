using System.Text.Json;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Profiles;
using GamePingBooster.Service;
using GamePingBooster.Service.Dns;
using GamePingBooster.Service.Network;
using GamePingBooster.Service.Tunnel;

namespace GamePingBooster.TunnelCheck;

internal static partial class Program
{
    public static async Task RunFullTunnelEngineScenarios()
    {
        Console.WriteLine("Running FullTunnelEngineScenarios (pure policy/load, zero native network)...");
        TestServiceConfigDefaults();
        TestServiceConfigComputedFullTunnelEnabled();
        TestServiceConfigLoadLegacyDocument();
        TestServiceConfigLoadExplicitFullTunnel();
        TestServiceConfigLoadNewInstall();
        await TestLoadProfileAsyncFullTunnelSynthesis();
        await TestLoadProfileAsyncNoRelaysFails();
        TestSnapshotAndSupportedGamesInFullMode();
        TestUnblockPolicyEmptyInFullMode();
        TestChooseGameForConnectReturnsSynthetic();
        TestActiveSessionCaptureImmutableUnderMidConnectConfigMutation();
        TestActiveSessionFullTunnelImmutableUnderMidConnectConfigMutation();
        TestReconnectRollbackCleansUpFullTunnelNetwork();
    }

    private static void TestServiceConfigDefaults()
    {
        var config = new ServiceConfig();
        Check("Default FullTunnel is true", config.FullTunnel == true);
        Check("Default FullTunnelEnabled is true", config.FullTunnelEnabled == true);
        Check("Default RelayEndpoints has ping.hollow-agent.xyz:51820",
            config.RelayEndpoints.Contains("ping.hollow-agent.xyz:51820"));
    }

    private static void TestServiceConfigComputedFullTunnelEnabled()
    {
        var config = new ServiceConfig();

        config.FullTunnel = true;
        config.LicenceUrl = "https://licence.example.com";
        Check("FullTunnelEnabled is false when LicenceUrl is present", !config.FullTunnelEnabled);

        config.FullTunnel = false;
        config.LicenceUrl = "";
        Check("FullTunnelEnabled is false when FullTunnel is false", !config.FullTunnelEnabled);

        config.FullTunnel = true;
        config.LicenceUrl = null;
        Check("FullTunnelEnabled is true when FullTunnel is true and LicenceUrl is null", config.FullTunnelEnabled);

        config.LicenceUrl = "   ";
        Check("FullTunnelEnabled is true when FullTunnel is true and LicenceUrl is whitespace", config.FullTunnelEnabled);
    }

    private static void TestServiceConfigLoadLegacyDocument()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gpb_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var tempFile = Path.Combine(tempDir, "config.json");
            // Legacy self-hosted config with no fullTunnel and no relayEndpoints
            File.WriteAllText(tempFile, """{"psk": "my-secret", "licenceUrl": ""}""");

            var loaded = ServiceConfig.Load(tempFile);
            Check("Legacy self-host document naturally defaults FullTunnel to true", loaded.FullTunnel == true);
            Check("Legacy self-host document naturally defaults FullTunnelEnabled to true", loaded.FullTunnelEnabled == true);
            Check("Legacy document preserves empty relayEndpoints without vendor injection", loaded.RelayEndpoints.Count == 0);

            // Legacy licensed config with no fullTunnel
            var tempLicensedFile = Path.Combine(tempDir, "config_licensed.json");
            File.WriteAllText(tempLicensedFile, """{"psk": "my-secret", "licenceUrl": "https://gamepingbooster.com"}""");
            var loadedLicensed = ServiceConfig.Load(tempLicensedFile);
            Check("Legacy licensed document has FullTunnel true", loadedLicensed.FullTunnel == true);
            Check("Legacy licensed document preserves effective game mode (FullTunnelEnabled false)", loadedLicensed.FullTunnelEnabled == false);

            loadedLicensed.LicenceUrl = "";
            Check("Licensed document becomes full tunnel when licence is blanked", loadedLicensed.FullTunnelEnabled == true);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static void TestServiceConfigLoadExplicitFullTunnel()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gpb_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var tempFileTrue = Path.Combine(tempDir, "config_true.json");
            File.WriteAllText(tempFileTrue, """{"fullTunnel": true, "licenceUrl": ""}""");
            var loadedTrue = ServiceConfig.Load(tempFileTrue);
            Check("Explicit fullTunnel: true is loaded", loadedTrue.FullTunnel == true && loadedTrue.FullTunnelEnabled == true);

            var tempFileFalse = Path.Combine(tempDir, "config_false.json");
            File.WriteAllText(tempFileFalse, """{"fullTunnel": false, "licenceUrl": ""}""");
            var loadedFalse = ServiceConfig.Load(tempFileFalse);
            Check("Explicit fullTunnel: false is loaded", loadedFalse.FullTunnel == false && loadedFalse.FullTunnelEnabled == false);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static void TestServiceConfigLoadNewInstall()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), "non_existent_" + Guid.NewGuid().ToString("N") + ".json");
        var loaded = ServiceConfig.Load(nonExistentPath);
        Check("New install defaults LicenceUrl blank", string.IsNullOrWhiteSpace(loaded.LicenceUrl));
        Check("New install defaults RelayEndpoints to [ping.hollow-agent.xyz:51820]",
            loaded.RelayEndpoints.Count == 1 && loaded.RelayEndpoints[0] == "ping.hollow-agent.xyz:51820");
        Check("New install defaults FullTunnelEnabled true", loaded.FullTunnelEnabled == true);
    }

    private static async Task TestLoadProfileAsyncFullTunnelSynthesis()
    {
        var log = new List<string>();
        var config = new ServiceConfig
        {
            FullTunnel = true,
            LicenceUrl = "",
            // Use loopback numeric IP so DNS resolution doesn't touch network
            RelayEndpoints = ["127.0.0.1:51820"]
        };

        var engine = new TunnelEngine(config, log.Add, isolatedForTest: true);
        await engine.LoadProfileAsync(CancellationToken.None);

        var clientId = typeof(TunnelEngine).GetField("_clientId",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Check("Isolated engine uses fixture client identity, not machine storage", (ulong?)clientId?.GetValue(engine) == 1UL);

        var snap = engine.Snapshot();
        Check("LoadProfileAsync synthesized profile with source 'full-tunnel'", snap.ProfileSource == "full-tunnel");
        Check("Snapshot GameCount is 0 in full mode", snap.GameCount == 0);
        Check("Relays list populated with selfhost relay", engine.Relays.Count == 1 && engine.Relays[0].Endpoint == "127.0.0.1:51820");
    }

    private static async Task TestLoadProfileAsyncNoRelaysFails()
    {
        var log = new List<string>();
        var config = new ServiceConfig
        {
            FullTunnel = true,
            LicenceUrl = "",
            RelayEndpoints = []
        };

        var engine = new TunnelEngine(config, log.Add, isolatedForTest: true);
        var threw = false;
        try
        {
            await engine.LoadProfileAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Check("Full tunnel with no explicit relays fails without fallback", threw);
    }

    private static void TestSnapshotAndSupportedGamesInFullMode()
    {
        var log = new List<string>();
        var config = new ServiceConfig
        {
            FullTunnel = true,
            LicenceUrl = "",
            LastGameId = "pubg",
            DefaultGameId = "cs2",
            RelayEndpoints = ["127.0.0.1:51820"]
        };

        var engine = new TunnelEngine(config, log.Add, isolatedForTest: true);
        var supported = engine.SupportedGames();
        Check("SupportedGames is empty in full tunnel mode", supported.Count == 0);

        var snap = engine.Snapshot();
        Check("Snapshot FullTunnel property is true in full mode", snap.FullTunnel == true);
        Check("Snapshot GameRunning is false in full mode", !snap.GameRunning);
        Check("Snapshot GameName is null in full mode", snap.GameName is null);
    }

    private static void TestUnblockPolicyEmptyInFullMode()
    {
        var log = new List<string>();
        var config = new ServiceConfig
        {
            FullTunnel = true,
            LicenceUrl = "",
            RelayEndpoints = ["127.0.0.1:51820"]
        };

        var engine = new TunnelEngine(config, log.Add, isolatedForTest: true);
        var policy = engine.UnblockPolicy;
        Check("UnblockPolicy is Empty in full tunnel mode", policy.Apps.Count == 0 && policy == UnblockPolicy.Empty);
    }

    private static void TestChooseGameForConnectReturnsSynthetic()
    {
        var log = new List<string>();
        var config = new ServiceConfig
        {
            FullTunnel = true,
            LicenceUrl = "",
            LastGameId = "cs2",
            DefaultGameId = "pubg",
            RelayEndpoints = ["127.0.0.1:51820"]
        };

        var engine = new TunnelEngine(config, log.Add, isolatedForTest: true);
        engine.LoadProfileAsync(CancellationToken.None).GetAwaiter().GetResult();

        // Check internal ChooseGameForConnect
        var method = typeof(TunnelEngine).GetMethod("ChooseGameForConnect",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var chosen = (GameEntry?)method?.Invoke(engine, ["valorant"]);
        Check("ChooseGameForConnect returns synthetic game 'full-tunnel' irrespective of requested/last game",
            chosen is not null && chosen.Id == "full-tunnel" && chosen.Name == "Full tunnel");
    }

    private static void TestActiveSessionCaptureImmutableUnderMidConnectConfigMutation()
    {
        var log = new List<string>();
        var config = new ServiceConfig
        {
            FullTunnel = true,
            LicenceUrl = "https://gamepingbooster.com", // Licensed => FullTunnelEnabled is false (legacy game mode)
            RelayEndpoints = ["127.0.0.1:51820"]
        };

        var engine = new TunnelEngine(config, log.Add, isolatedForTest: true);
        var dummyProfile = new ProfileBundle
        {
            Games = [new GameEntry { Id = "test-game", Name = "Test Game", ProcessNames = ["test.exe"] }],
            Relays = [new RelayEntry { Id = "r-1", Name = "Relay 1", Endpoint = "127.0.0.1:51820" }],
            Unblock = [new UnblockEntry { Id = "steam", Name = "Steam", Scope = ["steamcommunity.com"], Canary = "steamcommunity.com" }]
        };
        engine.SetProfileForTest(dummyProfile);

        // Disconnected state
        Check("Disconnected legacy mode ActiveFullTunnel is false", !engine.ActiveFullTunnel);
        Check("Disconnected legacy mode UnblockPolicy is not Empty", engine.UnblockPolicy != UnblockPolicy.Empty);
        Check("Disconnected legacy mode SupportedGames has games", engine.SupportedGames().Count == 1);
        Check("Disconnected legacy mode Snapshot FullTunnel is false", !engine.Snapshot().FullTunnel);

        // Simulate active legacy connected session
        engine.SimulateSessionStateForTest(TunnelState.Connected, isFullTunnel: false);

        // Mutate config mid-session: user clears licence in settings!
        config.LicenceUrl = "";
        Check("Mutated config FullTunnelEnabled became true", config.FullTunnelEnabled);

        // Active session MUST still reflect captured legacy mode
        Check("Active legacy session ignores mid-connection config change: ActiveFullTunnel remains false", !engine.ActiveFullTunnel);
        Check("Active legacy session ignores mid-connection config change: UnblockPolicy remains profile unblock", engine.UnblockPolicy != UnblockPolicy.Empty);
        Check("Active legacy session ignores mid-connection config change: SupportedGames remains populated", engine.SupportedGames().Count == 1);
        Check("Active legacy session ignores mid-connection config change: Snapshot FullTunnel remains false", !engine.Snapshot().FullTunnel);

        // Now disconnect: post-session, the new full mode applies
        engine.SimulateSessionStateForTest(TunnelState.Disconnected, isFullTunnel: false);
        Check("Disconnected session adopts mutated full mode: ActiveFullTunnel becomes true", engine.ActiveFullTunnel);
        Check("Disconnected session adopts mutated full mode: UnblockPolicy becomes Empty", engine.UnblockPolicy == UnblockPolicy.Empty);
        Check("Disconnected session adopts mutated full mode: SupportedGames becomes empty", engine.SupportedGames().Count == 0);
        Check("Disconnected session adopts mutated full mode: Snapshot FullTunnel becomes true", engine.Snapshot().FullTunnel);
    }

    private static void TestActiveSessionFullTunnelImmutableUnderMidConnectConfigMutation()
    {
        var log = new List<string>();
        var config = new ServiceConfig
        {
            FullTunnel = true,
            LicenceUrl = "", // Self-host => FullTunnelEnabled is true
            RelayEndpoints = ["127.0.0.1:51820"]
        };

        var engine = new TunnelEngine(config, log.Add, isolatedForTest: true);
        var dummyProfile = new ProfileBundle
        {
            Games = [new GameEntry { Id = "test-game", Name = "Test Game", ProcessNames = ["test.exe"] }],
            Relays = [new RelayEntry { Id = "r-1", Name = "Relay 1", Endpoint = "127.0.0.1:51820" }],
            Unblock = [new UnblockEntry { Id = "steam", Name = "Steam", Scope = ["steamcommunity.com"], Canary = "steamcommunity.com" }]
        };
        engine.SetProfileForTest(dummyProfile);

        // Simulate active full-tunnel connected session
        engine.SimulateSessionStateForTest(TunnelState.Connected, isFullTunnel: true);

        // Mutate config mid-session: user enters a licence URL
        config.LicenceUrl = "https://licence.example.com";
        Check("Mutated config FullTunnelEnabled became false", !config.FullTunnelEnabled);

        // Active session MUST still reflect captured full-tunnel mode
        Check("Active full session ignores mid-connection config change: ActiveFullTunnel remains true", engine.ActiveFullTunnel);
        Check("Active full session ignores mid-connection config change: UnblockPolicy remains Empty", engine.UnblockPolicy == UnblockPolicy.Empty);
        Check("Active full session ignores mid-connection config change: SupportedGames remains empty", engine.SupportedGames().Count == 0);
        Check("Active full session ignores mid-connection config change: Snapshot FullTunnel remains true", engine.Snapshot().FullTunnel);

        // Now disconnect: post-session, the licensed game mode applies
        engine.SimulateSessionStateForTest(TunnelState.Disconnected, isFullTunnel: false);
        Check("Disconnected session adopts licensed mode: ActiveFullTunnel becomes false", !engine.ActiveFullTunnel);
        Check("Disconnected session adopts licensed mode: UnblockPolicy restored", engine.UnblockPolicy != UnblockPolicy.Empty);
        Check("Disconnected session adopts licensed mode: SupportedGames restored", engine.SupportedGames().Count == 1);
        Check("Disconnected session adopts licensed mode: Snapshot FullTunnel becomes false", !engine.Snapshot().FullTunnel);
    }

    private sealed class CallbackDisposable(Action onDispose) : IDisposable
    {
        private readonly Action _onDispose = onDispose;
        public void Dispose() => _onDispose();
    }

    private static void TestReconnectRollbackCleansUpFullTunnelNetwork()
    {
        var log = new List<string>();
        var config = new ServiceConfig
        {
            FullTunnel = true,
            LicenceUrl = "",
            RelayEndpoints = ["127.0.0.1:51820"]
        };

        var engine = new TunnelEngine(config, log.Add, isolatedForTest: true);

        var fwDisposed = false;
        var nrptRemoved = false;
        var network = new FullTunnelNetwork(
            (idx, l) => new CallbackDisposable(() => fwDisposed = true),
            (res, l) => 1,
            l => { nrptRemoved = true; return 1; },
            log.Add);

        network.Install(42, "1.1.1.1");
        engine.SetFullTunnelNetworkForTest(network);
        engine.SimulateSessionStateForTest(TunnelState.Reconnecting, isFullTunnel: true);

        // Trigger rollback (e.g. after simulate network primitive failure during reconnect)
        engine.RollbackReconnectAttempt(adapterIndex: 42, routes: null);

        Check("RollbackReconnectAttempt sets FullTunnelNetwork to null", engine.FullTunnelNetworkForTest is null);
        Check("RollbackReconnectAttempt disposes firewall guard", fwDisposed);
        Check("RollbackReconnectAttempt removes NRPT policy", nrptRemoved);
    }
}
