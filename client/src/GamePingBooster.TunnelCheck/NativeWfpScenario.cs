using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using GamePingBooster.Service.Native;

namespace GamePingBooster.TunnelCheck;

internal static partial class Program
{
    // Opt-in Administrator check. Nothing is committed: Windows validates the actual filters,
    // then the transaction is aborted and the dynamic session closed, including on failure.
    private static int NativeWfpMain()
    {
        if (!OperatingSystem.IsWindows() ||
            !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Console.Error.WriteLine("wfp-native requires an Administrator terminal on Windows.");
            return 2;
        }

        nint handle = 0;
        var transaction = false;
        var passed = false;
        try
        {
            FullTunnelFirewall.SelfCheck();
            var session = new FullTunnelFirewall.FwpmSession0 { Flags = FullTunnelFirewall.FwpmSessionFlagDynamic };
            WfpCheck(FwpmEngineOpen0(0, FullTunnelFirewall.RpcCAuthnWinnt, 0, in session, out handle), "open session");
            WfpCheck(FwpmTransactionBegin0(handle, 0), "begin transaction");
            transaction = true;
            var ctor = typeof(FullTunnelFirewall).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                null, [typeof(nint)], null)!;
            var firewall = ctor.Invoke([handle]);
            var add = typeof(FullTunnelFirewall).GetMethod("AddRulesForLayer", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var (layer, name) in new[] {
                (FullTunnelFirewall.LayerAleAuthConnectV6, "ALE Connect IPv6"),
                (FullTunnelFirewall.LayerOutboundIpPacketV6, "Outbound IP Packet IPv6") })
            {
                add.Invoke(firewall, [layer, name]);
                Console.WriteLine($"ok Windows accepted permit and block filters: {name}");
            }
            passed = true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine((ex.InnerException ?? ex).Message);
        }
        finally
        {
            if (transaction)
            {
                var error = FwpmTransactionAbort0(handle);
                Console.WriteLine($"Transaction abort: 0x{error:X8}; policy was NOT committed.");
                passed &= error == 0;
            }
            if (handle != 0)
            {
                var error = FwpmEngineClose0(handle);
                Console.WriteLine($"Dynamic session close: 0x{error:X8}");
                passed &= error == 0;
            }
        }
        return passed ? 0 : 1;
    }

    private static void WfpCheck(uint error, string operation)
    {
        if (error != 0) throw new InvalidOperationException($"{operation}: 0x{error:X8}");
    }

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmEngineOpen0(nint server, uint authn, nint identity,
        in FullTunnelFirewall.FwpmSession0 session, out nint handle);
    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmTransactionBegin0(nint handle, uint flags);
    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmTransactionAbort0(nint handle);
    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmEngineClose0(nint handle);
}
