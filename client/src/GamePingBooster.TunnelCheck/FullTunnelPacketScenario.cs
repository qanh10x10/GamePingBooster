using System.Buffers.Binary;

namespace GamePingBooster.TunnelCheck;

internal static partial class Program
{
    // Opaque IPv4 transport test, not a real Internet TCP handshake or Windows routing test.
    private static async Task FullTunnelCarriesNonGameTraffic()
    {
        using var relay = new FakeRelay(Psk);
        using var rig = new Rig();
        var tunnel = await rig.StartAsync(relay, clientId: 901);
        var inner = U32(tunnel.Session.ClientIp);
        var packets = new[]
        {
            InternetPacket(inner, Addr(192, 0, 2, 9), 6, 443),
            InternetPacket(inner, Addr(198, 51, 100, 7), 17, 53),
            InternetPacket(inner, Addr(203, 0, 113, 8), 17, 443),
        };
        foreach (var packet in packets) rig.Device.FromWindows(packet);
        await WaitUntil(() => relay.Arrivals.Count >= packets.Length);
        Check("Full tunnel: TCP, DNS/UDP and QUIC/UDP traverse the packet path without a game profile",
            relay.Arrivals.Select(a => a.Inner).SequenceEqual(packets, new BytesEqual()));
        var session = relay.SessionOf(tunnel.SessionId)!;
        var replies = packets.Select(packet =>
        {
            var reply = (byte[])packet.Clone();
            packet.AsSpan(16, 4).CopyTo(reply.AsSpan(12));
            packet.AsSpan(12, 4).CopyTo(reply.AsSpan(16));
            reply[10] = reply[11] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(10), Checksum(reply.AsSpan(0, 20)));
            return reply;
        }).ToList();
        foreach (var reply in replies) relay.SendToClient(session, reply);
        await WaitUntil(() => rig.Device.ToWindows.Count >= replies.Count);
        Check("Full tunnel: non-game replies reach the adapter unchanged",
            rig.Device.ToWindows.Select(p => p.Packet).SequenceEqual(replies, new BytesEqual()));
    }

    private static byte[] InternetPacket(uint source, uint destination, byte protocol, ushort port)
    {
        var transportLength = protocol == 6 ? 20 : 12;
        var packet = new byte[20 + transportLength];
        packet[0] = 0x45;
        packet[8] = 64;
        packet[9] = protocol;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), source);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), destination);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), 50000);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), port);
        if (protocol == 6) { packet[32] = 0x50; packet[33] = 0x02; }
        else BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24), (ushort)transportLength);
        var pseudo = new byte[12 + transportLength];
        packet.AsSpan(12, 8).CopyTo(pseudo);
        pseudo[9] = protocol;
        BinaryPrimitives.WriteUInt16BigEndian(pseudo.AsSpan(10), (ushort)transportLength);
        packet.AsSpan(20).CopyTo(pseudo.AsSpan(12));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(protocol == 6 ? 36 : 26), Checksum(pseudo));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), Checksum(packet.AsSpan(0, 20)));
        return packet;
    }
}
