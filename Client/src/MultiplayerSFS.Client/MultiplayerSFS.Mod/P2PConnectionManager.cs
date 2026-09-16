using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using MultiplayerSFS.Common;

namespace MultiplayerSFS.Mod;

public static class P2PConnectionManager
{
    private const byte Hello = 1;
    private const byte Ack = 2;
    private const byte State = 3;
    private static readonly Dictionary<int, Peer> peers = new Dictionary<int, Peer>();

    public static string Status { get; private set; } = "Relay Fallback";
    public static int ActivePeerCount { get; private set; }

    public static void ApplyOffer(Packet_P2PPeerOffer offer)
    {
        if (offer == null || offer.PeerPlayerId < 0) return;
        if (!offer.Active)
        {
            peers.Remove(offer.PeerPlayerId);
            RefreshStatus();
            return;
        }
        if (!IPAddress.TryParse(offer.PeerAddress, out var address) || offer.PeerPort < 1 || offer.PeerPort > 65535 ||
            string.IsNullOrEmpty(offer.PairToken)) return;

        var endpoint = new IPEndPoint(address, offer.PeerPort);
        if (!peers.TryGetValue(offer.PeerPlayerId, out var peer) ||
            !string.Equals(peer.Token, offer.PairToken, StringComparison.Ordinal) || !peer.Endpoint.Equals(endpoint))
        {
            peer = new Peer(offer.PeerPlayerId, endpoint, offer.PairToken, Math.Max(0, offer.TransitionBufferSeconds));
            peers[offer.PeerPlayerId] = peer;
            Status = "P2P Connecting";
            ToastHelper.ShowToast(Status);
        }
        peer.LocalRocketIds = new HashSet<int>(offer.LocalRocketIds ?? new List<int>());
        peer.PeerRocketIds = new HashSet<int>(offer.PeerRocketIds ?? new List<int>());
    }

    public static bool SendRocketState(Packet_UpdateRocketPrimary packet)
    {
        if (packet == null) return false;
        var udp = ClientManager.client?.UdpTransport;
        if (udp == null) return false;
        var payload = NetPayloadCodec.Serialize(packet, false);
        var sent = false;
        foreach (var peer in peers.Values)
        {
            if (!peer.Active || !peer.LocalRocketIds.Contains(packet.RocketId)) continue;
            peer.NextStateSequence++;
            sent |= udp.TrySendPeer(peer.Endpoint, EncodeState(peer.Token, peer.NextStateSequence, payload));
        }
        return sent;
    }

    public static void Clear()
    {
        peers.Clear();
        ActivePeerCount = 0;
        Status = "Relay Fallback";
    }

    public static void Update()
    {
        var udp = ClientManager.client?.UdpTransport;
        if (udp == null) return;
        while (udp.TryReceivePeer(out var datagram)) HandleDatagram(udp, datagram);

        var now = DateTime.UtcNow;
        foreach (var peer in peers.Values)
        {
            if (peer.Active && now - peer.LastPacketUtc >= TimeSpan.FromSeconds(3))
            {
                peer.Active = false;
                peer.Fallback = true;
            }
            if (!peer.Active && now - peer.CreatedUtc >= TimeSpan.FromSeconds(peer.TransitionBufferSeconds))
                peer.Fallback = true;

            var probePeriod = peer.Active ? 1000 : 250;
            if (!peer.Fallback && (now - peer.LastProbeUtc).TotalMilliseconds >= probePeriod)
            {
                peer.LastProbeUtc = now;
                udp.TrySendPeer(peer.Endpoint, EncodeControl(Hello, peer.Token));
            }
        }
        RefreshStatus();
    }

    private static void HandleDatagram(UdpClientTransport udp, P2PRawDatagram datagram)
    {
        if (!TryDecode(datagram.Data, out var kind, out var token, out var offset)) return;
        foreach (var peer in peers.Values)
        {
            if (!string.Equals(peer.Token, token, StringComparison.Ordinal) || !peer.Endpoint.Equals(datagram.RemoteEndPoint)) continue;
            peer.LastPacketUtc = DateTime.UtcNow;
            if (kind == Hello)
            {
                peer.ValidHandshakePackets++;
                udp.TrySendPeer(peer.Endpoint, EncodeControl(Ack, peer.Token));
                if (peer.ValidHandshakePackets >= 3) peer.Active = true;
                return;
            }
            if (kind == Ack)
            {
                peer.ValidHandshakePackets++;
                if (peer.ValidHandshakePackets >= 3) peer.Active = true;
                return;
            }
            if (kind == State)
            {
                ApplyState(peer, datagram.Data, offset);
                return;
            }
            return;
        }
    }

    private static void ApplyState(Peer peer, byte[] data, int offset)
    {
        if (data.Length < offset + 8) return;
        var sequence = BitConverter.ToInt32(data, offset);
        var payloadBits = BitConverter.ToInt32(data, offset + 4);
        var payloadLength = data.Length - offset - 8;
        if (payloadBits < 0 || payloadBits > payloadLength * 8) return;
        var payload = new byte[payloadLength];
        Buffer.BlockCopy(data, offset + 8, payload, 0, payloadLength);
        try
        {
            var packet = new Packet_UpdateRocketPrimary();
            packet.Deserialize(NetPayloadCodec.ToIncoming(payload, payloadBits));
            if (!peer.PeerRocketIds.Contains(packet.RocketId)) return;
            peer.LastSequences.TryGetValue(packet.RocketId, out var lastSequence);
            if (!P2PStateOrderPolicy.ShouldAccept(0, lastSequence, 0, sequence)) return;
            peer.LastSequences[packet.RocketId] = sequence;
            ClientManager.ApplyP2PPrimary(packet);
        }
        catch { }
    }

    private static byte[] EncodeControl(byte kind, string token)
    {
        return Encode(kind, token, Array.Empty<byte>());
    }

    private static byte[] EncodeState(string token, int sequence, NetPayload payload)
    {
        var body = new byte[8 + payload.Data.Length];
        Buffer.BlockCopy(BitConverter.GetBytes(sequence), 0, body, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(payload.BitLength), 0, body, 4, 4);
        Buffer.BlockCopy(payload.Data, 0, body, 8, payload.Data.Length);
        return Encode(State, token, body);
    }

    private static byte[] Encode(byte kind, string token, byte[] body)
    {
        var tokenBytes = Encoding.UTF8.GetBytes(token ?? string.Empty);
        if (tokenBytes.Length > byte.MaxValue) return Array.Empty<byte>();
        body = body ?? Array.Empty<byte>();
        var data = new byte[3 + tokenBytes.Length + body.Length];
        data[0] = UdpClientTransport.P2PMagic;
        data[1] = kind;
        data[2] = (byte)tokenBytes.Length;
        Buffer.BlockCopy(tokenBytes, 0, data, 3, tokenBytes.Length);
        Buffer.BlockCopy(body, 0, data, 3 + tokenBytes.Length, body.Length);
        return data;
    }

    private static bool TryDecode(byte[] data, out byte kind, out string token, out int bodyOffset)
    {
        kind = 0;
        token = string.Empty;
        bodyOffset = 0;
        if (data == null || data.Length < 3 || data[0] != UdpClientTransport.P2PMagic) return false;
        kind = data[1];
        var length = data[2];
        bodyOffset = 3 + length;
        if (data.Length < bodyOffset || (kind != Hello && kind != Ack && kind != State)) return false;
        token = Encoding.UTF8.GetString(data, 3, length);
        return true;
    }

    private static void RefreshStatus()
    {
        ActivePeerCount = 0;
        var connecting = false;
        foreach (var peer in peers.Values)
        {
            if (peer.Active) ActivePeerCount++;
            else if (!peer.Fallback) connecting = true;
        }
        var next = ActivePeerCount > 0 ? "P2P Active" : connecting ? "P2P Connecting" : "Relay Fallback";
        if (next != Status)
        {
            Status = next;
            ToastHelper.ShowToast(Status);
        }
    }

    private sealed class Peer
    {
        public int PlayerId { get; }
        public IPEndPoint Endpoint { get; }
        public string Token { get; }
        public int TransitionBufferSeconds { get; }
        public DateTime CreatedUtc { get; } = DateTime.UtcNow;
        public DateTime LastProbeUtc { get; set; } = DateTime.MinValue;
        public DateTime LastPacketUtc { get; set; } = DateTime.MinValue;
        public int ValidHandshakePackets { get; set; }
        public int NextStateSequence { get; set; }
        public bool Active { get; set; }
        public bool Fallback { get; set; }
        public HashSet<int> LocalRocketIds { get; set; } = new HashSet<int>();
        public HashSet<int> PeerRocketIds { get; set; } = new HashSet<int>();
        public Dictionary<int, int> LastSequences { get; } = new Dictionary<int, int>();

        public Peer(int playerId, IPEndPoint endpoint, string token, int transitionBufferSeconds)
        {
            PlayerId = playerId;
            Endpoint = endpoint;
            Token = token;
            TransitionBufferSeconds = transitionBufferSeconds;
        }
    }
}
