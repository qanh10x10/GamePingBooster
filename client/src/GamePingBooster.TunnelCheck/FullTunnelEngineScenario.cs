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
        TestLegacyHollowSingletonMigration();
        TestServiceConfigHollowPskSerialization();
        await TestLoadProfileAsyncFullTunnelSynthesis();
        await TestLoadProfileAsyncBuiltinRelayAndCustomRelays();
        await TestSnapshotReadinessAndTrustBoundaryIsolation();

        await TestFailoverOrderPreventsCrossDomainFallback();
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
        Check("Default RelayEndpoints is empty list", config.RelayEndpoints.Count == 0);
        Check("BuiltInRelayEndpoint constant is ping.hollow-agent.xyz:51820",
            ServiceConfig.BuiltInRelayEndpoint == "ping.hollow-agent.xyz:51820");
        Check("BuiltInRelayId constant is hollow-vps",
            ServiceConfig.BuiltInRelayId == "hollow-vps");
        Check("BuiltInRelayName constant is Singapore West",
            ServiceConfig.BuiltInRelayName == "Singapore West");
        Check("Default HollowPsk is empty string", config.HollowPsk == "");
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
        Check("New install defaults RelayEndpoints to empty list", loaded.RelayEndpoints.Count == 0);
        Check("New install defaults FullTunnelEnabled true", loaded.FullTunnelEnabled == true);
        Check("New install defaults HollowPsk empty", loaded.HollowPsk == "");
    }

    private static void TestLegacyHollowSingletonMigration()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gpb_test_mig_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var tempFile = Path.Combine(tempDir, "config.json");
            const string initialJson = """{"relayEndpoints": ["ping.hollow-agent.xyz:51820"], "psk": "my-legacy-psk", "defaultGameId": "pubg", "defaultRelayId": "self-1"}""";
            File.WriteAllText(tempFile, initialJson);

            var loaded = ServiceConfig.Load(tempFile);
            Check("Legacy Singapore West alias is removed from custom endpoints", loaded.RelayEndpoints.Count == 0);
            Check("Legacy custom key is never promoted to HollowPsk", loaded.HollowPsk == "");
            Check("Legacy custom key is preserved", loaded.Psk == "my-legacy-psk");
            Check("Legacy Singapore West selection maps to built-in ID", loaded.DefaultRelayId == ServiceConfig.BuiltInRelayId);
            Check("Legacy custom key does not configure the built-in server", !new TunnelEngine(
                new ServiceConfig { DefaultRelayId = ServiceConfig.BuiltInRelayId, Psk = loaded.Psk },
                _ => { }, isolatedForTest: true).Snapshot().Configured);
            Check("Legacy singleton Hollow endpoint preserves defaultGameId", loaded.DefaultGameId == "pubg");
            Check("Legacy migration does not write to disk at load", File.ReadAllText(tempFile) == initialJson);

            foreach (var choice in new[] { "self-1", "self-2", "self-3", "" })
            {
                var mixedFile = Path.Combine(tempDir, "config_mixed.json");
                File.WriteAllText(mixedFile, JsonSerializer.Serialize(new ServiceConfig
                {
                    RelayEndpoints = ["127.0.0.1:51820", " PING.HOLLOW-AGENT.XYZ:51820 ", "127.0.0.2:51820"],
                    DefaultRelayId = choice,
                    Psk = "custom-key-stays-custom",
                    HollowPsk = "builtin-key-stays-builtin"
                }, ServiceConfigJsonContext.Default.ServiceConfig));
                var mixed = ServiceConfig.Load(mixedFile);
                var expectedChoice = choice == "self-2" ? ServiceConfig.BuiltInRelayId : choice == "self-3" ? "self-2" : choice;
                Check($"Mixed config preserves custom relay order ({choice})",
                    mixed.RelayEndpoints.SequenceEqual(new[] { "127.0.0.1:51820", "127.0.0.2:51820" }));
                Check($"Mixed config preserves selected physical server ({choice})", mixed.DefaultRelayId == expectedChoice);
                Check($"Mixed config keeps credential domains separate ({choice})",
                    mixed.Psk == "custom-key-stays-custom" && mixed.HollowPsk == "builtin-key-stays-builtin");
            }

            var gameModeFile = Path.Combine(tempDir, "config_game_mode.json");
            const string gameModeJson = """{"fullTunnel":false,"relayEndpoints":["ping.hollow-agent.xyz:51820"],"defaultRelayId":"self-1"}""";
            File.WriteAllText(gameModeFile, gameModeJson);
            var gameMode = ServiceConfig.Load(gameModeFile);
            Check("Game mode custom relay config is not migrated",
                gameMode.RelayEndpoints.SequenceEqual(new[] { ServiceConfig.BuiltInRelayEndpoint }) && gameMode.DefaultRelayId == "self-1");

            // Unrelated custom endpoint remains unchanged
            var customFile = Path.Combine(tempDir, "config_custom.json");
            const string customJson = """{"relayEndpoints": ["relay.example.com:51820"], "psk": "custom-secret", "defaultRelayId": "self-1"}""";
            File.WriteAllText(customFile, customJson);

            var loadedCustom = ServiceConfig.Load(customFile);
            Check("Unrelated custom endpoint is preserved in RelayEndpoints",
                loadedCustom.RelayEndpoints.Count == 1 && loadedCustom.RelayEndpoints[0] == "relay.example.com:51820");
            Check("Unrelated custom endpoint preserves psk", loadedCustom.Psk == "custom-secret");
            Check("Unrelated custom endpoint leaves HollowPsk empty", loadedCustom.HollowPsk == "");
            Check("Unrelated custom endpoint preserves defaultRelayId", loadedCustom.DefaultRelayId == "self-1");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static void TestServiceConfigHollowPskSerialization()
    {
        var config = new ServiceConfig
        {
            HollowPsk = "test-hollow-psk-value",
            Psk = "test-custom-psk-value",
            RelayEndpoints = ["custom.relay:51820"]
        };
        var json = JsonSerializer.Serialize(config, ServiceConfigJsonContext.Default.ServiceConfig);
        Check("ServiceConfig serialized JSON contains hollowPsk", json.Contains("\"hollowPsk\":\"test-hollow-psk-value\""));
        Check("ServiceConfig serialized JSON contains psk", json.Contains("\"psk\":\"test-custom-psk-value\""));

        var deserialized = JsonSerializer.Deserialize(json, ServiceConfigJsonContext.Default.ServiceConfig);
        Check("Deserialized ServiceConfig round-trips hollowPsk", deserialized?.HollowPsk == "test-hollow-psk-value");
        Check("Deserialized ServiceConfig round-trips psk", deserialized?.Psk == "test-custom-psk-value");
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
        Check("Relays list populated with builtin and selfhost relay",
            engine.Relays.Count == 2 && engine.Relays.Any(r => r.Endpoint == "127.0.0.1:51820"));
    }

    private static async Task TestLoadProfileAsyncBuiltinRelayAndCustomRelays()
    {
        var log = new List<string>();
        // Case 1: No custom relays - builtin 'hollow-vps' must be present and named 'Singapore West'
        var configNoCustom = new ServiceConfig
        {
            FullTunnel = true,
            LicenceUrl = "",
            RelayEndpoints = []
        };
        var engineNoCustom = new TunnelEngine(configNoCustom, log.Add, isolatedForTest: true);
        await engineNoCustom.LoadProfileAsync(CancellationToken.None);

        Check("LoadProfileAsync with empty RelayEndpoints succeeds with builtin server",
            engineNoCustom.Relays.Count == 1);
        Check("Builtin server has stable ID hollow-vps",
            engineNoCustom.Relays[0].Id == ServiceConfig.BuiltInRelayId);
        Check("Builtin server has displayed name Singapore West",
            engineNoCustom.Relays[0].Name == ServiceConfig.BuiltInRelayName);
        Check("Builtin server has endpoint ping.hollow-agent.xyz:51820",
            engineNoCustom.Relays[0].Endpoint == ServiceConfig.BuiltInRelayEndpoint);

        // Case 2: Custom relay configured - both builtin and custom relay present
        var configWithCustom = new ServiceConfig
        {
            FullTunnel = true,
            LicenceUrl = "",
            RelayEndpoints = ["127.0.0.1:51820"]
        };
        var engineWithCustom = new TunnelEngine(configWithCustom, log.Add, isolatedForTest: true);
        await engineWithCustom.LoadProfileAsync(CancellationToken.None);

        Check("LoadProfileAsync with custom relay includes both builtin and custom relays",
            engineWithCustom.Relays.Count == 2);
        Check("Relays contains builtin hollow-vps",
            engineWithCustom.Relays.Any(r => r.Id == ServiceConfig.BuiltInRelayId && r.Name == "Singapore West"));
        Check("Relays contains custom relay self-1",
            engineWithCustom.Relays.Any(r => r.Id == "self-1" && r.Endpoint == "127.0.0.1:51820"));

        var tempDir = Path.Combine(Path.GetTempPath(), "hollow_picker_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            foreach (var withCustom in new[] { false, true })
            {
                var path = Path.Combine(tempDir, "config.json");
                File.WriteAllText(path, JsonSerializer.Serialize(new ServiceConfig
                {
                    RelayEndpoints = withCustom
                        ? [ServiceConfig.BuiltInRelayEndpoint, "127.0.0.1:51820"]
                        : [ServiceConfig.BuiltInRelayEndpoint],
                    DefaultRelayId = "self-1",
                    HollowPsk = "fixture-builtin-key"
                }, ServiceConfigJsonContext.Default.ServiceConfig));
                var engine = new TunnelEngine(ServiceConfig.Load(path), log.Add, isolatedForTest: true);
                await engine.LoadProfileAsync(CancellationToken.None);
                var options = engine.RelayOptions();
                Check($"Legacy picker lists Singapore West exactly once (custom={withCustom})",
                    options.Count == (withCustom ? 2 : 1) &&
                    options.Count(r => r.Id == ServiceConfig.BuiltInRelayId && r.Name == "Singapore West") == 1 &&
                    options.All(r => r.Name != ServiceConfig.BuiltInRelayEndpoint));
                Check($"Legacy picker keeps built-in selection ready (custom={withCustom})", engine.Snapshot().Configured);
            }
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static async Task TestSnapshotReadinessAndTrustBoundaryIsolation()
    {
        var log = new List<string>();

        // Built-in selected (or default when no custom):
        var configBuiltin = new ServiceConfig
        {
            FullTunnel = true,
            LicenceUrl = "",
            RelayEndpoints = [],
            DefaultRelayId = ServiceConfig.BuiltInRelayId,
            HollowPsk = "",
            Psk = "some-custom-key-that-must-not-qualify"
        };
        var engineBuiltin = new TunnelEngine(configBuiltin, log.Add, isolatedForTest: true);
        await engineBuiltin.LoadProfileAsync(CancellationToken.None);

        var snap1 = engineBuiltin.Snapshot();
        Check("Snapshot reports custom endpoints only (empty)", snap1.RelayEndpoints.Count == 0);
        Check("Snapshot Configured is false when HollowPsk is empty, even if Psk has a value", !snap1.Configured);

        configBuiltin.HollowPsk = "valid-hollow-key-0123456789";
        var snap2 = engineBuiltin.Snapshot();
        Check("Snapshot Configured is true when HollowPsk is set for builtin server", snap2.Configured);

        // Custom selected:
        var configCustom = new ServiceConfig
        {
            FullTunnel = true,
            LicenceUrl = "",
            RelayEndpoints = ["127.0.0.1:51820"],
            DefaultRelayId = "self-1",
            HollowPsk = "valid-hollow-key-0123456789",
            Psk = ""
        };
        var engineCustom = new TunnelEngine(configCustom, log.Add, isolatedForTest: true);
        await engineCustom.LoadProfileAsync(CancellationToken.None);

        var snap3 = engineCustom.Snapshot();
        Check("Snapshot reports custom endpoints [127.0.0.1:51820]",
            snap3.RelayEndpoints.Count == 1 && snap3.RelayEndpoints[0] == "127.0.0.1:51820");
        Check("Snapshot Configured is false for custom server when Psk is empty, even if HollowPsk is set", !snap3.Configured);

        configCustom.Psk = "valid-custom-key-0123456789";
        var snap4 = engineCustom.Snapshot();
        Check("Snapshot Configured is true for custom server when Psk is set", snap4.Configured);

        // Token from unrelated domain does NOT configure full tunnel
        var tokenField = typeof(TunnelEngine).GetField("_token",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var unconfiguredEngine = new TunnelEngine(new ServiceConfig { FullTunnel = true, LicenceUrl = "", RelayEndpoints = [] }, log.Add, isolatedForTest: true);
        await unconfiguredEngine.LoadProfileAsync(CancellationToken.None);
        tokenField?.SetValue(unconfiguredEngine, new byte[150]);
        var snapToken = unconfiguredEngine.Snapshot();
        Check("Licence token from unrelated domain does not make full tunnel Configured", !snapToken.Configured);
    }

    private static async Task TestFailoverOrderPreventsCrossDomainFallback()
    {
        var log = new List<string>();
        var config = new ServiceConfig
        {
            FullTunnel = true,
            LicenceUrl = "",
            RelayEndpoints = ["127.0.0.1:51820", "127.0.0.2:51820"]
        };
        var engine = new TunnelEngine(config, log.Add, isolatedForTest: true);
        await engine.LoadProfileAsync(CancellationToken.None);

        var failoverMethod = typeof(TunnelEngine).GetMethod("FailoverOrder",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Check("FailoverOrder method exists", failoverMethod is not null);

        var builtinRelay = engine.Relays.First(r => r.Id == ServiceConfig.BuiltInRelayId);
        var customRelay = engine.Relays.First(r => r.Id == "self-1");

        // When current is builtin: failover candidates must NOT contain custom relays
        var builtinOrder = (List<RelayEntry>?)failoverMethod?.Invoke(engine, [builtinRelay]);
        Check("FailoverOrder for builtin contains only builtin relay",
            builtinOrder is not null && builtinOrder.Count == 1 && builtinOrder[0].Id == ServiceConfig.BuiltInRelayId);

        // When current is custom: failover candidates must NOT contain builtin relay
        var customOrder = (List<RelayEntry>?)failoverMethod?.Invoke(engine, [customRelay]);
        Check("FailoverOrder for custom does not contain builtin relay",
            customOrder is not null && customOrder.All(r => r.Id != ServiceConfig.BuiltInRelayId));
        Check("FailoverOrder for custom includes other custom relays",
            customOrder is not null && customOrder.Any(r => r.Id == "self-2"));
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
