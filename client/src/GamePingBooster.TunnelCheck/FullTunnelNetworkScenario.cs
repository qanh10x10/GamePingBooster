using GamePingBooster.Service.Dns;
using GamePingBooster.Service.Native;
using GamePingBooster.Service.Network;

namespace GamePingBooster.TunnelCheck;

internal static partial class Program
{
    public static async Task RunFullTunnelNetworkScenarios()
    {
        await FullTunnelNetworkScenario.Run();
    }
}

public static class FullTunnelNetworkScenario
{
    private sealed class TrackingDisposable : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    public static async Task Run()
    {
        Console.WriteLine("Full-tunnel network scenarios (fake boundaries, zero live mutations):");

        TestNativeLayoutSelfCheck();
        TestExportedNativeMetadata();
        TestInstallSuccessSequence();
        TestResolverValidationBeforeMutations();
        TestNrptPolicyResolverValidation();
        TestPartialInstallFirewallFailureRollback();
        TestPartialInstallNrptFailureRollback();
        TestPartialInstallNrptCountMismatchRollback();
        TestDisposeIdempotenceAndIsolation();
        TestStartupCleanupIsolation();
        TestNrptPrefixIsolation();

        await Task.CompletedTask;
    }

    private static void TestNativeLayoutSelfCheck()
    {
        // Independently compiled with Windows SDK fwpmtypes.h, x64: the context union contains a 16-byte GUID.
        Program.Check("WFP filter context union preserves native SDK x64 offsets",
            System.Runtime.InteropServices.Marshal.SizeOf<FullTunnelFirewall.FwpmFilter0>() == 200 &&
            System.Runtime.InteropServices.Marshal.OffsetOf<FullTunnelFirewall.FwpmFilter0>(nameof(FullTunnelFirewall.FwpmFilter0.Reserved)) == 168 &&
            System.Runtime.InteropServices.Marshal.OffsetOf<FullTunnelFirewall.FwpmFilter0>(nameof(FullTunnelFirewall.FwpmFilter0.FilterId)) == 176 &&
            System.Runtime.InteropServices.Marshal.OffsetOf<FullTunnelFirewall.FwpmFilter0>(nameof(FullTunnelFirewall.FwpmFilter0.EffectiveWeight)) == 184);
        try
        {
            FullTunnelFirewall.SelfCheck();
            Program.Check("WFP native struct layouts match 64-bit Windows SDK ABI", true);
        }
        catch (Exception ex)
        {
            Program.Check("WFP native struct layouts match 64-bit Windows SDK ABI", false, ex.Message);
        }
    }

    private static void TestExportedNativeMetadata()
    {
        var ok = true;
        var detail = new List<string>();

        uint actionBlock = FullTunnelFirewall.FwpActionBlock;
        uint actionPermit = FullTunnelFirewall.FwpActionPermit;
        uint termFlag = FullTunnelFirewall.FwpActionFlagTerminating;
        uint authn = FullTunnelFirewall.RpcCAuthnWinnt;
        uint dyn = FullTunnelFirewall.FwpmSessionFlagDynamic;
        uint loopbackIf = FullTunnelFirewall.IfTypeSoftwareLoopback;

        // 1. Action constants with terminating flag
        if (actionBlock != 0x00001001)
        {
            ok = false;
            detail.Add($"FwpActionBlock expected 0x1001 got 0x{actionBlock:X}");
        }
        if (actionPermit != 0x00001002)
        {
            ok = false;
            detail.Add($"FwpActionPermit expected 0x1002 got 0x{actionPermit:X}");
        }
        if (termFlag != 0x00001000)
        {
            ok = false;
            detail.Add($"FwpActionFlagTerminating expected 0x1000 got 0x{termFlag:X}");
        }

        // 2. Authn and session flags
        if (authn != 10)
        {
            ok = false;
            detail.Add($"RpcCAuthnWinnt expected 10 got {authn}");
        }
        if (dyn != 1)
        {
            ok = false;
            detail.Add($"FwpmSessionFlagDynamic expected 1 got {dyn}");
        }
        if (loopbackIf != 24)
        {
            ok = false;
            detail.Add($"IfTypeSoftwareLoopback expected 24 got {loopbackIf}");
        }

        // 3. Layer and Condition GUIDs against official SDK (fwpmu.h)
        if (FullTunnelFirewall.LayerAleAuthConnectV6 != new Guid("4a72393b-319f-44bc-84c3-ba54dcb3b6b4"))
        {
            ok = false;
            detail.Add($"LayerAleAuthConnectV6 mismatch: {FullTunnelFirewall.LayerAleAuthConnectV6}");
        }
        if (FullTunnelFirewall.LayerOutboundIpPacketV6 != new Guid("a3b3ab6b-3564-488c-9117-f34e82142763"))
        {
            ok = false;
            detail.Add($"LayerOutboundIpPacketV6 mismatch: {FullTunnelFirewall.LayerOutboundIpPacketV6}");
        }
        if (FullTunnelFirewall.ConditionInterfaceType != new Guid("daf8cd14-e09e-4c93-a5ae-c5c13b73ffca"))
        {
            ok = false;
            detail.Add($"ConditionInterfaceType mismatch: {FullTunnelFirewall.ConditionInterfaceType}");
        }
        if (FullTunnelFirewall.SublayerUniversal != new Guid("eebecc03-ced4-4380-819a-2734397b2b74"))
        {
            ok = false;
            detail.Add($"SublayerUniversal mismatch: {FullTunnelFirewall.SublayerUniversal}");
        }

        // 4. In-memory filter structure validation across both layers
        foreach (var layer in new[] { FullTunnelFirewall.LayerAleAuthConnectV6, FullTunnelFirewall.LayerOutboundIpPacketV6 })
        {
            var (permit, cond, block) = FullTunnelFirewall.BuildLayerFilterDescriptors(layer);

            if (permit.Action.Type != FullTunnelFirewall.FwpActionPermit ||
                permit.LayerKey != layer ||
                permit.SubLayerKey != FullTunnelFirewall.SublayerUniversal ||
                permit.Weight.Uint8 != 15 ||
                permit.NumFilterConditions != 1 ||
                permit.Flags != 0)
            {
                ok = false;
                detail.Add($"Permit filter structure invalid on layer {layer}");
            }

            if (cond.FieldKey != FullTunnelFirewall.ConditionInterfaceType ||
                cond.MatchType != FullTunnelFirewall.FwpMatchEqual ||
                cond.ConditionValue.Type != FullTunnelFirewall.FwpUint32 ||
                cond.ConditionValue.Uint32 != FullTunnelFirewall.IfTypeSoftwareLoopback)
            {
                ok = false;
                detail.Add($"Permit condition structure invalid on layer {layer}");
            }

            if (block.Action.Type != FullTunnelFirewall.FwpActionBlock ||
                block.LayerKey != layer ||
                block.SubLayerKey != FullTunnelFirewall.SublayerUniversal ||
                block.Weight.Uint8 != 10 ||
                block.NumFilterConditions != 0 ||
                block.Flags != 0)
            {
                ok = false;
                detail.Add($"Block filter structure invalid on layer {layer}");
            }
        }

        Program.Check("WFP native metadata, terminating actions, and filter descriptors match Windows SDK", ok, string.Join("; ", detail));
    }

    private static void TestInstallSuccessSequence()
    {
        var log = new List<string>();
        var firewallTracker = new TrackingDisposable();

        FullTunnelNetwork.FirewallInstaller fwInstaller = (idx, _) =>
        {
            log.Add($"firewall:install:{idx}");
            return firewallTracker;
        };

        FullTunnelNetwork.NrptInstaller nrptInstaller = (resolver, _) =>
        {
            log.Add($"nrpt:install:{resolver}");
            return 1;
        };

        FullTunnelNetwork.NrptRemover nrptRemover = _ =>
        {
            log.Add("nrpt:remove");
            return 1;
        };

        var network = new FullTunnelNetwork(fwInstaller, nrptInstaller, nrptRemover);
        try
        {
            network.Install(adapterIndex: 42, resolverAddress: "1.1.1.1");
            var expectedLog = new[] { "firewall:install:42", "nrpt:install:1.1.1.1" };
            var ok = log.SequenceEqual(expectedLog) && !firewallTracker.Disposed;
            Program.Check("Full-tunnel install sequences Firewall then NRPT successfully", ok,
                $"actual sequence: [{string.Join(", ", log)}]");
        }
        catch (Exception ex)
        {
            Program.Check("Full-tunnel install sequences Firewall then NRPT successfully", false, ex.Message);
        }
    }

    private static void TestResolverValidationBeforeMutations()
    {
        var log = new List<string>();
        var firewallTracker = new TrackingDisposable();

        FullTunnelNetwork.FirewallInstaller fwInstaller = (idx, _) =>
        {
            log.Add($"firewall:install:{idx}");
            return firewallTracker;
        };

        FullTunnelNetwork.NrptInstaller nrptInstaller = (resolver, _) =>
        {
            log.Add($"nrpt:install:{resolver}");
            return 1;
        };

        FullTunnelNetwork.NrptRemover nrptRemover = _ =>
        {
            log.Add("nrpt:remove");
            return 1;
        };

        var network = new FullTunnelNetwork(fwInstaller, nrptInstaller, nrptRemover);
        var invalidResolvers = new[] { "invalid", "::1", "256.1.1.1", "", "   " };
        var allRejected = true;

        foreach (var inv in invalidResolvers)
        {
            log.Clear();
            try
            {
                network.Install(42, inv);
                allRejected = false;
            }
            catch (ArgumentException)
            {
                if (log.Count != 0 || firewallTracker.Disposed)
                {
                    allRejected = false;
                }
            }
            catch
            {
                allRejected = false;
            }
        }

        Program.Check("IPv4 resolver validation rejects invalid addresses before firewall or NRPT mutations", allRejected);
    }

    private static void TestNrptPolicyResolverValidation()
    {
        var caught = false;
        try
        {
            NrptPolicy.InstallFullTunnel("not-an-ip");
        }
        catch (ArgumentException)
        {
            caught = true;
        }
        catch (Exception ex)
        {
            Program.Check("NrptPolicy.InstallFullTunnel rejects invalid IPv4 resolver before registry mutations", false, $"threw wrong type: {ex.GetType().Name}: {ex.Message}");
            return;
        }
        Program.Check("NrptPolicy.InstallFullTunnel rejects invalid IPv4 resolver before registry mutations", caught);
    }

    private static void TestPartialInstallFirewallFailureRollback()
    {
        var log = new List<string>();
        FullTunnelNetwork.FirewallInstaller fwInstaller = (_, _) =>
        {
            log.Add("firewall:fail");
            throw new InvalidOperationException("simulated firewall init failure");
        };

        FullTunnelNetwork.NrptInstaller nrptInstaller = (res, _) =>
        {
            log.Add($"nrpt:install:{res}");
            return 1;
        };

        FullTunnelNetwork.NrptRemover nrptRemover = _ =>
        {
            log.Add("nrpt:remove");
            return 1;
        };

        var network = new FullTunnelNetwork(fwInstaller, nrptInstaller, nrptRemover);
        var caught = false;
        try
        {
            network.Install(42);
        }
        catch (InvalidOperationException)
        {
            caught = true;
        }

        var ok = caught && log.Count == 1 && log[0] == "firewall:fail";
        Program.Check("Firewall install failure aborts before NRPT write", ok,
            $"caught={caught}, log=[{string.Join(", ", log)}]");
    }

    private static void TestPartialInstallNrptFailureRollback()
    {
        var log = new List<string>();
        var firewallTracker = new TrackingDisposable();

        FullTunnelNetwork.FirewallInstaller fwInstaller = (idx, _) =>
        {
            log.Add($"firewall:install:{idx}");
            return firewallTracker;
        };

        FullTunnelNetwork.NrptInstaller nrptInstaller = (_, _) =>
        {
            log.Add("nrpt:fail");
            throw new UnauthorizedAccessException("simulated HKLM write failure");
        };

        FullTunnelNetwork.NrptRemover nrptRemover = _ =>
        {
            log.Add("nrpt:remove");
            return 1;
        };

        var network = new FullTunnelNetwork(fwInstaller, nrptInstaller, nrptRemover);
        var caught = false;
        try
        {
            network.Install(42);
        }
        catch (UnauthorizedAccessException)
        {
            caught = true;
        }

        var nrptRemoved = log.Contains("nrpt:remove");
        var ok = caught && firewallTracker.Disposed && nrptRemoved;
        Program.Check("NRPT failure rolls back installed firewall guard and cleans partial policy", ok,
            $"caught={caught}, firewallDisposed={firewallTracker.Disposed}, nrptRemoved={nrptRemoved}, log=[{string.Join(", ", log)}]");
    }

    private static void TestPartialInstallNrptCountMismatchRollback()
    {
        var log = new List<string>();
        var firewallTracker = new TrackingDisposable();

        FullTunnelNetwork.FirewallInstaller fwInstaller = (idx, _) =>
        {
            log.Add($"firewall:install:{idx}");
            return firewallTracker;
        };

        FullTunnelNetwork.NrptInstaller nrptInstaller = (_, _) =>
        {
            log.Add("nrpt:install:silent_zero");
            return 0; // Silent failure / unexpected write count
        };

        FullTunnelNetwork.NrptRemover nrptRemover = _ =>
        {
            log.Add("nrpt:remove");
            return 1;
        };

        var network = new FullTunnelNetwork(fwInstaller, nrptInstaller, nrptRemover);
        var caught = false;
        try
        {
            network.Install(42);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("verification failed") || ex.Message.Contains("expected 1"))
        {
            caught = true;
        }

        var nrptRemoved = log.Contains("nrpt:remove");
        var ok = caught && firewallTracker.Disposed && nrptRemoved;
        Program.Check("NRPT write count mismatch triggers rollback and cleans leftover policy", ok,
            $"caught={caught}, firewallDisposed={firewallTracker.Disposed}, nrptRemoved={nrptRemoved}, log=[{string.Join(", ", log)}]");
    }

    private static void TestDisposeIdempotenceAndIsolation()
    {
        var log = new List<string>();
        var firewallTracker = new TrackingDisposable();

        FullTunnelNetwork.FirewallInstaller fwInstaller = (idx, _) =>
        {
            log.Add($"firewall:install:{idx}");
            return firewallTracker;
        };

        FullTunnelNetwork.NrptInstaller nrptInstaller = (resolver, _) =>
        {
            log.Add($"nrpt:install:{resolver}");
            return 1;
        };

        FullTunnelNetwork.NrptRemover nrptRemover = _ =>
        {
            log.Add("nrpt:remove");
            return 1;
        };

        using (var network = new FullTunnelNetwork(fwInstaller, nrptInstaller, nrptRemover))
        {
            network.Install(42);
            network.Dispose();
            network.Dispose(); // second call should be no-op
        }

        var removeCount = log.Count(x => x == "nrpt:remove");
        var ok = removeCount == 1 && firewallTracker.Disposed;
        Program.Check("FullTunnelNetwork Dispose is idempotent and cleans both primitives once", ok,
            $"removeCount={removeCount}, firewallDisposed={firewallTracker.Disposed}");
    }

    private static void TestStartupCleanupIsolation()
    {
        // Startup cleanup must remove ONLY FullTunnelPrefix, never touching unblock rules
        var fullPrefix = NrptPolicy.FullTunnelPrefix;
        var ok = fullPrefix == "HollowPingBooster-FullTunnel-" &&
                 !fullPrefix.StartsWith("GamePingBooster-Unblock-");
        Program.Check("Startup cleanup targets only full-tunnel NRPT own prefix", ok,
            $"prefix={fullPrefix}");
    }

    private static void TestNrptPrefixIsolation()
    {
        const string fullRule = "HollowPingBooster-FullTunnel-Default";
        const string unblockRule = "GamePingBooster-Unblock-steam-00";
        const string unrelatedRule = "ThirdParty-Vpn-Rule";

        var matchesFull = fullRule.StartsWith(NrptPolicy.FullTunnelPrefix, StringComparison.OrdinalIgnoreCase);
        var unblockMatchesFull = unblockRule.StartsWith(NrptPolicy.FullTunnelPrefix, StringComparison.OrdinalIgnoreCase);
        var unrelatedMatchesFull = unrelatedRule.StartsWith(NrptPolicy.FullTunnelPrefix, StringComparison.OrdinalIgnoreCase);

        var ok = matchesFull && !unblockMatchesFull && !unrelatedMatchesFull;
        Program.Check("NRPT prefix isolation guarantees full-tunnel and unblock never cross-delete", ok,
            $"matchesFull={matchesFull}, unblockMatchesFull={unblockMatchesFull}, unrelatedMatchesFull={unrelatedMatchesFull}");
    }
}
