// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Linq;
using System.Threading;
using MultiplayerSFS.Common;
using MultiplayerSFS.Common.Transport.Kcp;
using SFS.World;
using UnityEngine;

namespace MultiplayerSFS.Mod;

public static class P2PConnectionManager
{
    private static readonly Dictionary<int, Peer> peers = new Dictionary<int, Peer>();

    public static string Status { get; private set; } = "Relay Fallback";
    public static int ActivePeerCount { get; private set; }

    public static void ApplyOffer(Packet_P2PPeerOffer offer)
    {
        if (offer == null || offer.PeerPlayerId < 0) return;
        if (!offer.Active)
        {
            if (peers.TryGetValue(offer.PeerPlayerId, out var oldPeer))
            {
                oldPeer.Dispose();
            }
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
            if (LocalManager.players != null && LocalManager.players.TryGetValue(offer.PeerPlayerId, out var lp))
                peer.Username = lp.username;
            Status = "P2P Connecting";
            ToastHelper.ShowToast(Status);
        }
        peer.LocalRocketIds = new HashSet<int>(offer.LocalRocketIds ?? new List<int>());
        peer.PeerRocketIds = new HashSet<int>(offer.PeerRocketIds ?? new List<int>());
    }

    // 玩家火箭严格只走 TCP 中继单源（服务器盖章 WorldTime），P2P 不再发送任何玩家火箭 primary，
    // 避免同一枚火箭在接收端同时收到 TCP 包与 P2P 包、两个时间戳轴不完全对齐导致位置来回飘移（双源冲突）。
    // P2P 握手机制保留，待未来有非玩家邻近物体（本地 world 没有、对方世界有）时才用于直传显示体。
    public static bool SendRocketState(Packet_UpdateRocketPrimary packet)
    {
        return false;
    }

    public static void Clear()
    {
        foreach (var peer in peers.Values)
        {
            peer.Dispose();
        }
        peers.Clear();
        ActivePeerCount = 0;
        Status = "Relay Fallback";
    }

    public static void Update()
    {
        var now = DateTime.UtcNow;
        uint currentMs = (uint)(now.Ticks / TimeSpan.TicksPerMillisecond);

        foreach (var peer in peers.Values)
        {
            if (!peer.KcpInitialized && peer.Fallback == false)
            {
                peer.InitializeKcp();
            }

            if (peer.KcpInitialized)
            {
                peer.KcpContext.Update(currentMs);

                // 处理网络接收
                while (peer.Socket != null && peer.TryReceiveDatagram(out var datagram))
                {
                    peer.KcpContext.Input(datagram.Data, 0, datagram.Data.Length);
                }

                // 处理 KCP 接收队列
                while (peer.KcpContext.PeekSize() > 0)
                {
                    byte[] recvBuf = peer.RecvBuffer;
                    int len = peer.KcpContext.Recv(recvBuf, 0, recvBuf.Length);
                    if (len > 0)
                    {
                        HandleKcpMessage(peer, recvBuf, len);
                    }
                    else
                    {
                        break;
                    }
                }

                // 新连接的 LastProbeUtc 是 MinValue，因此首次会立即发送；后续按状态保活。
                if (!peer.Fallback && (now - peer.LastProbeUtc).TotalMilliseconds >= (peer.Active ? 1000 : 250))
                {
                    peer.LastProbeUtc = now;
                    SendProbe(peer);
                }
            }

            // 修复"双方互相看不到对方状态"（臭名昭著的老 bug）：P2P 握手成功不等于双方世界一致。
            // P2P 不承载玩家火箭状态（严格服务器单源），所以首次转 Active 且稳定窗口过后，
            // 必须向服务器请求一次权威世界快照，把双方已有火箭拉回服务器权威状态。
            // 每个连接周期只发一次（ServerSyncRequested 门控）；断线重连生成新 Peer 时会再次触发。
            if (peer.Active && now - peer.LastPacketUtc >= TimeSpan.FromSeconds(3))
            {
                peer.Active = false;
                peer.Fallback = true;
            }

            // 握手超时：10 秒仍未 Active 则标记 Fallback
            if (!peer.Active && !peer.Fallback && now >= peer.HandshakeDeadlineUtc)
            {
                peer.Fallback = true;
            }
            if (!peer.Active && now - peer.CreatedUtc >= TimeSpan.FromSeconds(peer.TransitionBufferSeconds))
                peer.Fallback = true;

            if (P2PPostConnectSyncPolicy.ShouldRequestServerSync(
                    peer.Active, peer.ServerSyncRequested, peer.ActiveSinceUtc, now))
            {
                peer.ServerSyncRequested = true;
                ClientManager.client?.RequestWorldSnapshot();
            }

            // 发送待发数据
            if (peer.KcpInitialized && peer.KcpContext.WaitSnd > 0)
            {
                peer.KcpContext.Flush();
            }
        }

        // 清理已落后的 peer
        var toRemove = new List<int>();
        foreach (var kv in peers)
        {
            if (kv.Value.Fallback && kv.Value.KcpInitialized && kv.Value.KcpContext.State == uint.MaxValue)
            {
                toRemove.Add(kv.Key);
            }
        }
        foreach (var id in toRemove)
        {
            if (peers.TryGetValue(id, out var p))
            {
                p.Dispose();
                peers.Remove(id);
            }
        }

        RefreshStatus();
    }

    private static void HandleKcpMessage(Peer peer, byte[] data, int length)
    {
        if (length <= 0) return;
        peer.LastPacketUtc = DateTime.UtcNow;

        TcpFrame frame;
        try
        {
            // P2P 与主连接使用同一种帧格式，避免发送端和接收端各自维护一套协议。
            var encoded = new byte[length];
            Buffer.BlockCopy(data, 0, encoded, 0, length);
            frame = DecodeP2PFrame(encoded);
        }
        catch
        {
            return;
        }

        if (frame.Kind == TcpFrameKind.Hello)
        {
            peer.ValidHandshakePackets++;
            SendAck(peer, frame);
            if (peer.ValidHandshakePackets >= 3 && !peer.Active)
            {
                peer.Active = true;
                peer.ActiveSinceUtc = DateTime.UtcNow;
            }
        }
        else if (frame.Kind == TcpFrameKind.HelloAck)
        {
            UpdateRtt(peer, frame.Payload);
            peer.ValidHandshakePackets++;
            if (peer.ValidHandshakePackets >= 3 && !peer.Active)
            {
                peer.Active = true;
                peer.ActiveSinceUtc = DateTime.UtcNow;
            }
        }
        else if (frame.Kind == TcpFrameKind.Packet)
        {
            ApplyState(peer, frame.Payload, 0);
        }
    }

    private static void SendProbe(Peer peer)
    {
        // 时间戳由对端原样回显，避免依赖本地“最后一次探测”状态。
        byte[] payload = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
        var probeFrame = new TcpFrame(TcpFrameKind.Hello, ++peer.NextStateSequence,
            payload, payload.Length * 8);
        peer.LastPingSentTicks = DateTime.UtcNow.Ticks;
        peer.KcpContext.Send(TcpFrameCodec.Encode(probeFrame));
    }

    private static void SendAck(Peer peer, TcpFrame hello)
    {
        var ackFrame = new TcpFrame(TcpFrameKind.HelloAck, hello.Sequence,
            hello.Payload, hello.PayloadBits);
        peer.KcpContext.Send(TcpFrameCodec.Encode(ackFrame));
    }

    private static TcpFrame DecodeP2PFrame(byte[] data)
    {
        if (data.Length < 4) throw new InvalidDataException("P2P frame is truncated.");
        int bodyLength = data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);
        if (bodyLength < 9 || bodyLength + 4 != data.Length)
            throw new InvalidDataException("P2P frame length is invalid.");
        var body = new byte[bodyLength];
        Buffer.BlockCopy(data, 4, body, 0, bodyLength);
        return TcpFrameCodec.DecodeBody(body);
    }

    private static void UpdateRtt(Peer peer, byte[] payload)
    {
        if (payload == null || payload.Length != sizeof(long)) return;
        long sentTicks = BitConverter.ToInt64(payload, 0);
        double sampleMs = Math.Max(0, (DateTime.UtcNow.Ticks - sentTicks) / 10000.0);
        if (sampleMs <= 0) return;
        double previous = peer.RttMs;
        peer.RttMs = previous <= 0 ? sampleMs : previous * 0.8 + sampleMs * 0.2;
        peer.JitterMs = previous <= 0
            ? 0
            : peer.JitterMs * 0.8 + Math.Abs(sampleMs - previous) * 0.2;
        peer.LastPingSentTicks = 0;
    }

    private static void ApplyState(Peer peer, byte[] data, int offset)
    {
        if (data.Length < offset + 8) return;
        var sequence = BitConverter.ToInt32(data, offset);
        var payloadCount = BitConverter.ToInt32(data, offset + 4);
        int cursor = offset + 8;
        for (int i = 0; i < payloadCount; i++)
        {
            if (data.Length < cursor + 8) return;
            var payloadBits = BitConverter.ToInt32(data, cursor);
            var payloadLength = (payloadBits + 7) / 8;
            if (payloadLength < 0 || payloadLength > data.Length - cursor - 8) return;
            var payload = new byte[payloadLength];
            Buffer.BlockCopy(data, cursor + 8, payload, 0, payloadLength);
            cursor += 8 + payloadLength;
            try
            {
                var packet = new Packet_UpdateRocketPrimary();
                packet.Deserialize(NetPayloadCodec.ToIncoming(payload, payloadBits));
                // 关键守卫：玩家火箭（已在 world.rockets 中、由 TCP 中继单源同步）一律跳过 P2P，
                // 只处理本地 world 里不存在的未知物体（未来非玩家邻近物体的显示体）。
                // 这样玩家火箭在接收端只有 TCP 一个时间戳源，杜绝双源飘移。
                if (ClientManager.world != null && ClientManager.world.rockets.ContainsKey(packet.RocketId))
                {
                    continue;
                }
                // TODO: 未知非玩家物体 → 维护轻量显示体（当前无此类物体，暂不发生）。
            }
            catch { }
        }
    }

    private static void RefreshStatus()
    {
        ActivePeerCount = 0;
        var connecting = false;
        foreach (var peer in peers.Values)
        {
            if (peer.Active)
            {
                if (!peer.ReportedEstablished)
                {
                    peer.ReportedEstablished = true;
                    var name = string.IsNullOrEmpty(peer.Username) ? $"player {peer.PlayerId}" : peer.Username;
                    ToastHelper.ShowToast($"P2P link established with {name}");
                }
                ActivePeerCount++;
            }
            else
            {
                peer.ReportedEstablished = false;
                if (!peer.Fallback) connecting = true;
            }
        }
        var activePeer = peers.Values.FirstOrDefault(p => p.Active);
        var next = ActivePeerCount > 0 ? $"P2P Active (RTT: {activePeer?.RttMs:F0}ms, 抖动: {activePeer?.JitterMs:F0}ms)" : connecting ? "P2P Connecting" : "Relay Fallback";
        if (next != Status)
        {
            if (next == "Relay Fallback" && Status == "P2P Active")
                ToastHelper.ShowToast("P2P link lost, falling back to relay");
            Status = next;
            ToastHelper.ShowToast(Status);
        }
    }

    private sealed class Peer : IDisposable
    {
        public int PlayerId { get; }
        public IPEndPoint Endpoint { get; }
        public string Token { get; }
        public string Username { get; set; } = string.Empty;
        public int TransitionBufferSeconds { get; }
        public DateTime CreatedUtc { get; } = DateTime.UtcNow;
        public DateTime LastProbeUtc { get; set; } = DateTime.MinValue;
        public DateTime LastPacketUtc { get; set; } = DateTime.MinValue;
        public int ValidHandshakePackets { get; set; }
        public int NextStateSequence { get; set; }
        public bool Active { get; set; }
        public bool Fallback { get; set; }
        public bool ReportedEstablished { get; set; }
        // P2P 首次转 Active 的时刻；强制重同步的稳定窗口从这一刻起算。
        public DateTime ActiveSinceUtc { get; set; } = DateTime.MaxValue;
        // 本连接周期是否已向服务器请求过权威快照；防止每个连接周期重复全量同步。
        public bool ServerSyncRequested { get; set; }
        public HashSet<int> LocalRocketIds { get; set; } = new HashSet<int>();
        public HashSet<int> PeerRocketIds { get; set; } = new HashSet<int>();
        public Dictionary<int, int> LastSequences { get; } = new Dictionary<int, int>();

        // RTT 和抖动统计（用于 UI 显示和自适应参数调整）
        public double RttMs { get; set; }
        public double JitterMs { get; set; }
        public long LastPingSentTicks { get; set; }

        // 握手超时：CreatedUtc 后 10 秒仍未 Active 则标记 Fallback
        public DateTime HandshakeDeadlineUtc { get; set; } = DateTime.MaxValue;

        // KCP 相关
        public System.Net.Sockets.UdpClient Socket { get; private set; }
        public KcpContext KcpContext { get; private set; }
        public bool KcpInitialized { get; private set; }
        public byte[] RecvBuffer { get; } = new byte[65536];
        private System.Threading.Tasks.Task _receiveLoopTask;
        private CancellationTokenSource _receiveLoopCts;

        public Peer(int playerId, IPEndPoint endpoint, string token, int transitionBufferSeconds)
        {
            PlayerId = playerId;
            Endpoint = endpoint;
            Token = token;
            TransitionBufferSeconds = transitionBufferSeconds;
            HandshakeDeadlineUtc = DateTime.UtcNow.AddSeconds(10);
        }

        public void InitializeKcp()
                {
                    // 创建 UDP socket
                    Socket = new UdpClient(Endpoint.AddressFamily);
                    // 必须显式绑定本地端口：UdpClient(AddressFamily) 是未绑定 socket，
                    // 下面 ReceiveLoop 里的 ReceiveAsync 会抛 "在执行此操作前必须先调用 Bind 方法"，
                    // 接收循环启动即死 —— 表现就是 P2P 信令正常但永远收不到直连数据。
                    Socket.Client.Bind(new IPEndPoint(
                        Endpoint.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any,
                        0));
                    Socket.Client.ReceiveBufferSize = 256 * 1024;
                    MultiplayerSFS.Common.UdpSocketGuard.DisableConnReset(Socket);
                    Socket.Client.SendBufferSize = 256 * 1024;

                    // 两端必须得到同一个 conv。只用“对方 PlayerId”会让双方产生不同会话号。
                    uint conv = CreateConversation(ClientManager.playerId, PlayerId);
                    KcpContext = new KcpContext(conv, (data, ctx) => SendToNetwork(data));

                    // 配置 nodelay 模式：低延迟、快速重传
                    KcpContext.SetNoDelay(1, 10, 2, 1);
                    KcpContext.SetMtu(1400);
                    KcpContext.WndSize(256, 512);

                    KcpInitialized = true;

                    // 启动接收循环
                    _receiveLoopCts = new CancellationTokenSource();
                    _receiveLoopTask = ReceiveLoop(_receiveLoopCts.Token);
                }

        private void SendToNetwork(byte[] data)
        {
            try
            {
                if (Socket != null)
                {
                    Socket.Send(data, data.Length, Endpoint);
                }
            }
            catch { }
        }

        private static uint CreateConversation(int firstPlayerId, int secondPlayerId)
        {
            uint low = unchecked((uint)Math.Min(firstPlayerId, secondPlayerId));
            uint high = unchecked((uint)Math.Max(firstPlayerId, secondPlayerId));
            return (low * 0x9E3779B9u) ^ high ^ 0x5346534Bu;
        }

        private async System.Threading.Tasks.Task ReceiveLoop(CancellationToken cancellationToken)
        {
            try
            {
                while (Socket != null && KcpInitialized && !cancellationToken.IsCancellationRequested)
                {
                    UdpReceiveResult result;
                    try
                    {
                        result = await Socket.ReceiveAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        await System.Threading.Tasks.Task.Delay(10).ConfigureAwait(false);
                        continue;
                    }

                    lock (_datagramLock)
                    {
                        if (_pendingDatagrams.Count < 512)
                        {
                            _pendingDatagrams.Enqueue(new P2PRawDatagram(result.RemoteEndPoint, result.Buffer));
                        }
                    }
                }
            }
            catch { }
        }

        private readonly object _datagramLock = new object();
        private readonly Queue<P2PRawDatagram> _pendingDatagrams = new Queue<P2PRawDatagram>();

        public bool TryReceiveDatagram(out P2PRawDatagram datagram)
        {
            lock (_datagramLock)
            {
                if (_pendingDatagrams.Count > 0)
                {
                    datagram = _pendingDatagrams.Dequeue();
                    return true;
                }
            }
            datagram = default;
            return false;
        }

        public void Dispose()
        {
            KcpInitialized = false;
            _receiveLoopCts?.Cancel();
            try { _receiveLoopTask?.Wait(1000); } catch { }
            try { _receiveLoopTask?.Dispose(); } catch { }
            try { _receiveLoopCts?.Dispose(); } catch { }
            try { KcpContext?.Dispose(); } catch { }
            try { Socket?.Close(); } catch { }
            lock (_datagramLock) _pendingDatagrams.Clear();
        }
    }

    // 复用原有的数据报结构
    private readonly struct P2PRawDatagram
    {
        public readonly IPEndPoint RemoteEndPoint;
        public readonly byte[] Data;
        public P2PRawDatagram(IPEndPoint endPoint, byte[] data)
        {
            RemoteEndPoint = endPoint;
            Data = data;
        }
    }
}