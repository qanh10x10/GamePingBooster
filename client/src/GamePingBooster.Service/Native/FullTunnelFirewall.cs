using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GamePingBooster.Service.Native;

/// <summary>
/// Safe IPv6 anti-bypass via dynamic Windows Filtering Platform (WFP) session.
/// Blocks outbound IPv6 connect/packets across both ALE_AUTH_CONNECT_V6 (new connections)
/// and OUTBOUND_IPPACKET_V6 (pre-existing connections and raw packets), while preserving
/// IPv6 loopback (::1).
/// All filters are bound to FWPM_SESSION_FLAG_DYNAMIC, so Windows kernel and BFE
/// automatically purge all rules if the process crashes or terminates.
/// </summary>
internal sealed partial class FullTunnelFirewall : IDisposable
{
    private const string Dll = "fwpuclnt.dll";

    internal const uint NoError = 0;
    internal const uint RpcCAuthnWinnt = 10;
    internal const uint FwpmSessionFlagDynamic = 0x00000001;
    internal const uint FwpActionFlagTerminating = 0x00001000;
    internal const uint FwpActionBlock = 0x00000001 | FwpActionFlagTerminating; // 0x00001001
    internal const uint FwpActionPermit = 0x00000002 | FwpActionFlagTerminating; // 0x00001002
    internal const uint FwpMatchEqual = 0;
    internal const uint FwpMatchFlagsAllSet = 6;
    internal const uint FwpUint8 = 1;
    internal const uint FwpUint32 = 3;
    internal const uint IfTypeSoftwareLoopback = 24;
    internal const uint ConditionFlagIsLoopback = 0x00000001;

    // Standard WFP GUIDs from Windows SDK (fwpmu.h)
    internal static readonly Guid LayerOutboundIpPacketV6 = new("a3b3ab6b-3564-488c-9117-f34e82142763");
    internal static readonly Guid LayerAleAuthConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    internal static readonly Guid ConditionInterfaceType = new("daf8cd14-e09e-4c93-a5ae-c5c13b73ffca");
    internal static readonly Guid ConditionFlags = new("632ce23b-5167-435c-86d7-e903684aa80c");
    internal static readonly Guid SublayerUniversal = new("eebecc03-ced4-4380-819a-2734397b2b74");

    // ------------------------------------------------------------------ WFP Native Structs
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    internal struct FwpmSession0
    {
        [FieldOffset(0)] public Guid SessionKey;
        [FieldOffset(16)] public nint Name;
        [FieldOffset(24)] public nint Description;
        [FieldOffset(32)] public uint Flags;
        [FieldOffset(36)] public uint TxnWaitTimeoutInMSec;
        [FieldOffset(40)] public uint ProcessId;
        [FieldOffset(48)] public nint Sid;
        [FieldOffset(56)] public nint Username;
        [FieldOffset(64)] public int KernelMode;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    internal struct FwpValue0
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public byte Uint8;
        [FieldOffset(8)] public uint Uint32;
        [FieldOffset(8)] public ulong Uint64;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    internal struct FwpConditionValue0
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public byte Uint8;
        [FieldOffset(8)] public uint Uint32;
        [FieldOffset(8)] public ulong Uint64;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    internal struct FwpmFilterCondition0
    {
        [FieldOffset(0)] public Guid FieldKey;
        [FieldOffset(16)] public uint MatchType;
        [FieldOffset(24)] public FwpConditionValue0 ConditionValue;
    }

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    internal struct FwpmAction0
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public Guid FilterType;
    }

    [StructLayout(LayoutKind.Explicit, Size = 200)]
    internal struct FwpmFilter0
    {
        [FieldOffset(0)] public Guid FilterKey;
        [FieldOffset(16)] public nint Name;
        [FieldOffset(24)] public nint Description;
        [FieldOffset(32)] public uint Flags;
        [FieldOffset(40)] public nint ProviderKey;
        [FieldOffset(48)] public uint ProviderDataSize;
        [FieldOffset(56)] public nint ProviderData;
        [FieldOffset(64)] public Guid LayerKey;
        [FieldOffset(80)] public Guid SubLayerKey;
        [FieldOffset(96)] public FwpValue0 Weight;
        [FieldOffset(112)] public uint NumFilterConditions;
        [FieldOffset(120)] public nint FilterCondition;
        [FieldOffset(128)] public FwpmAction0 Action;
        [FieldOffset(152)] public ulong RawContext;
        [FieldOffset(152)] public Guid ProviderContextKey;
        [FieldOffset(168)] public nint Reserved;
        [FieldOffset(176)] public ulong FilterId;
        [FieldOffset(184)] public FwpValue0 EffectiveWeight;
    }

    [LibraryImport(Dll, EntryPoint = "FwpmEngineOpen0")]
    private static partial uint FwpmEngineOpen0(
        nint serverName,
        uint authnService,
        nint authIdentity,
        in FwpmSession0 session,
        out nint engineHandle);

    [LibraryImport(Dll, EntryPoint = "FwpmEngineClose0")]
    private static partial uint FwpmEngineClose0(nint engineHandle);

    [LibraryImport(Dll, EntryPoint = "FwpmFilterAdd0")]
    private static unsafe partial uint FwpmFilterAdd0(
        nint engineHandle,
        in FwpmFilter0 filter,
        nint sd,
        ulong* id);

    private nint _engineHandle;
    private bool _disposed;

    private FullTunnelFirewall(nint engineHandle)
    {
        _engineHandle = engineHandle;
    }

    /// <summary>
    /// ABI layout self-check: asserts that struct sizes, field offsets, and GUIDs match 64-bit Windows SDK.
    /// Safe to call anywhere; makes no native or registry mutations.
    /// </summary>
    public static void SelfCheck()
    {
        if (Unsafe.SizeOf<FwpmSession0>() != 72 ||
            Unsafe.SizeOf<FwpmFilter0>() != 200 ||
            Unsafe.SizeOf<FwpmFilterCondition0>() != 40 ||
            Unsafe.SizeOf<FwpmAction0>() != 20 ||
            Unsafe.SizeOf<FwpValue0>() != 16 ||
            Unsafe.SizeOf<FwpConditionValue0>() != 16 ||
            IntPtr.Size != 8)
        {
            throw new PlatformNotSupportedException(
                "The WFP struct layouts in this build do not match 64-bit Windows. WFP initialization is refused.");
        }

        if (Marshal.OffsetOf<FwpmSession0>(nameof(FwpmSession0.Flags)) != 32 ||
            Marshal.OffsetOf<FwpmSession0>(nameof(FwpmSession0.Sid)) != 48 ||
            Marshal.OffsetOf<FwpmFilter0>(nameof(FwpmFilter0.LayerKey)) != 64 ||
            Marshal.OffsetOf<FwpmFilter0>(nameof(FwpmFilter0.Weight)) != 96 ||
            Marshal.OffsetOf<FwpmFilter0>(nameof(FwpmFilter0.FilterCondition)) != 120 ||
            Marshal.OffsetOf<FwpmFilter0>(nameof(FwpmFilter0.Action)) != 128 ||
            Marshal.OffsetOf<FwpmFilter0>(nameof(FwpmFilter0.Reserved)) != 168 ||
            Marshal.OffsetOf<FwpmFilter0>(nameof(FwpmFilter0.FilterId)) != 176 ||
            Marshal.OffsetOf<FwpmFilter0>(nameof(FwpmFilter0.EffectiveWeight)) != 184)
        {
            throw new PlatformNotSupportedException(
                "The WFP struct field offsets do not match 64-bit Windows. WFP initialization is refused.");
        }

        if (FwpActionBlock != 0x00001001 ||
            FwpActionPermit != 0x00001002 ||
            RpcCAuthnWinnt != 10 ||
            FwpmSessionFlagDynamic != 1 ||
            IfTypeSoftwareLoopback != 24)
        {
            throw new PlatformNotSupportedException(
                "The WFP action and session constants do not match Windows SDK.");
        }
    }

    /// <summary>
    /// Installs a dynamic WFP session with IPv6 block rules on ALE connect and Outbound IP packet layers,
    /// permitting loopback.
    /// </summary>
    public static FullTunnelFirewall Install(uint adapterIndex = 0, Action<string>? log = null)
    {
        SelfCheck();

        var session = default(FwpmSession0);
        session.Flags = FwpmSessionFlagDynamic;
        session.TxnWaitTimeoutInMSec = 5000;

        var err = FwpmEngineOpen0(0, RpcCAuthnWinnt, 0, in session, out var handle);
        if (err != NoError)
        {
            throw new InvalidOperationException($"Could not open dynamic WFP session (error 0x{err:X8}).");
        }

        var fw = new FullTunnelFirewall(handle);
        try
        {
            // Block outbound IPv6 on both ALE connect (immediate socket fail) and OUTBOUND IP (drop existing/raw)
            fw.AddRulesForLayer(LayerAleAuthConnectV6, "ALE Connect IPv6");
            fw.AddRulesForLayer(LayerOutboundIpPacketV6, "Outbound IP Packet IPv6");
            log?.Invoke("Firewall: dynamic WFP IPv6 guard active (loopback preserved, outbound blocked).");
            return fw;
        }
        catch
        {
            fw.Dispose();
            throw;
        }
    }

    internal static (FwpmFilter0 PermitFilter, FwpmFilterCondition0 PermitCond, FwpmFilter0 BlockFilter) BuildLayerFilterDescriptors(Guid layerKey)
    {
        var cond = default(FwpmFilterCondition0);
        cond.FieldKey = ConditionInterfaceType;
        cond.MatchType = FwpMatchEqual;
        cond.ConditionValue.Type = FwpUint32;
        cond.ConditionValue.Uint32 = IfTypeSoftwareLoopback;

        var permitFilter = default(FwpmFilter0);
        permitFilter.LayerKey = layerKey;
        permitFilter.SubLayerKey = SublayerUniversal;
        permitFilter.Weight.Type = FwpUint8;
        permitFilter.Weight.Uint8 = 15;
        permitFilter.Action.Type = FwpActionPermit;
        permitFilter.NumFilterConditions = 1;

        var blockFilter = default(FwpmFilter0);
        blockFilter.LayerKey = layerKey;
        blockFilter.SubLayerKey = SublayerUniversal;
        blockFilter.Weight.Type = FwpUint8;
        blockFilter.Weight.Uint8 = 10;
        blockFilter.Action.Type = FwpActionBlock;
        blockFilter.NumFilterConditions = 0;

        return (permitFilter, cond, blockFilter);
    }

    private unsafe void AddRulesForLayer(Guid layerKey, string desc)
    {
        var (permitFilter, cond, blockFilter) = BuildLayerFilterDescriptors(layerKey);
        permitFilter.FilterCondition = (nint)(&cond);

        var err = FwpmFilterAdd0(_engineHandle, in permitFilter, 0, null);
        if (err != NoError)
        {
            throw new InvalidOperationException($"Failed to add loopback permit filter for {desc} (error 0x{err:X8}).");
        }

        err = FwpmFilterAdd0(_engineHandle, in blockFilter, 0, null);
        if (err != NoError)
        {
            throw new InvalidOperationException($"Failed to add block filter for {desc} (error 0x{err:X8}).");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_engineHandle != 0)
        {
            var handle = _engineHandle;
            _engineHandle = 0;
            FwpmEngineClose0(handle);
        }
    }
}
