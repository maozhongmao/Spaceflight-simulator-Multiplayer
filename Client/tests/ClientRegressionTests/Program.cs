using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Lidgren.Network;
using MultiplayerSFS.Common;
using MultiplayerSFS.Mod;
using SFS.WorldBase;
using UnityEngine;

internal static class Program
{
    private static int failures;

    private static void Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--tcp-smoke")
        {
            RunTcpSmoke(args).GetAwaiter().GetResult();
            return;
        }
        Run("Packet numbers match the current .NET 8 server", PacketNumbersMatchServer);
        Run("Strings match the current server wire format", StringsMatchServerWireFormat);
        Run("Join response accepts the server payload", JoinResponseAcceptsServerPayload);
        Run("Chat packet accepts and emits the server payload", ChatPacketMatchesServerPayload);
        Run("DestroyPart routes by RocketId", DestroyPartRoutesByRocketId);
        Run("Discrete events run only when due", DiscreteEventsRunOnlyWhenDue);
        Run("Interpolation fraction handles duplicate timestamps", DuplicateTimestampIsSafe);
        Run("Interpolation correction keeps an already-timed target in place", CorrectionDoesNotAdvanceTarget);
        Run("TCP frames survive stream coalescing", TcpFramesSurviveStreamCoalescing);
        Run("TCP send queue keeps latest rocket state", TcpSendQueueKeepsLatestRocketState);
        Run("Rocket sync policy lowers idle traffic", RocketSyncPolicyLowersIdleTraffic);
        Run("Time-warp vote packet matches server wire format", TimeWarpPacketMatchesServerWireFormat);

        Console.WriteLine(failures == 0 ? "ALL CLIENT REGRESSION TESTS PASSED" : $"FAILED: {failures}");
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }

    private static async Task RunTcpSmoke(string[] args)
    {
        var transport = new TcpClientTransport();
        try
        {
            Packet_JoinResponse response = await transport.ConnectAsync(
                IPAddress.Parse(args[1]), int.Parse(args[2]), new Packet_JoinRequest
                {
                    Username = "net48-smoke",
                    Password = args.Length >= 4 ? args[3] : string.Empty,
                    SolarSystemName = string.Empty
                });
            True(response.PlayerId >= 0, "TCP handshake player id");
            Equal(50.0, response.UpdateRocketsPeriod, "TCP update period");

            bool receivedSelf = false;
            DateTime deadline = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < deadline && (!receivedSelf || transport.RoundTripMs <= 0))
            {
                TcpFrame frame;
                while (transport.TryReceive(out frame))
                {
                    if (frame.Kind != TcpFrameKind.Packet) continue;
                    NetIncomingMessage message = NetPayloadCodec.ToIncoming(frame.Payload, frame.PayloadBits);
                    PacketType type = (PacketType)message.ReadByte();
                    if (type == PacketType.PlayerConnected)
                    {
                        Packet_PlayerConnected player = message.Read<Packet_PlayerConnected>();
                        if (player.PlayerId == response.PlayerId && player.Username == "net48-smoke") receivedSelf = true;
                    }
                }
                await Task.Delay(25);
            }
            True(receivedSelf, "TCP self player snapshot");
            True(transport.RoundTripMs > 0, "TCP application heartbeat RTT");
            True(transport.Connected, "TCP connection remains active");

            var rocket = new RocketState
            {
                rocketName = "TCP Cross Runtime Rocket",
                location = new NetLocation(new Double2(1, 2), new Double2(0, 0), "Earth"),
                rotation = 0,
                angularVelocity = 0,
                throttleOn = false,
                throttlePercent = 0,
                RCS = false,
                parts = new Dictionary<int, PartState>(),
                joints = new List<JointState>(),
                stages = new List<StageState>()
            };
            transport.Send(new Packet_CreateRocket
            {
                WorldTime = response.WorldTime,
                LocalId = 321,
                GlobalId = -1,
                ForLaunch = true,
                Rocket = rocket
            });

            Packet_CreateRocket echoedRocket = null;
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && echoedRocket == null)
            {
                TcpFrame frame;
                while (transport.TryReceive(out frame))
                {
                    if (frame.Kind != TcpFrameKind.Packet) continue;
                    NetIncomingMessage message = NetPayloadCodec.ToIncoming(frame.Payload, frame.PayloadBits);
                    if ((PacketType)message.ReadByte() != PacketType.CreateRocket) continue;
                    Packet_CreateRocket candidate = message.Read<Packet_CreateRocket>();
                    if (candidate.LocalId == 321) echoedRocket = candidate;
                }
                await Task.Delay(25);
            }
            True(echoedRocket != null, "TCP CreateRocket echo");
            True(echoedRocket.GlobalId > 0, "TCP server assigned global rocket id");
            Equal("TCP Cross Runtime Rocket", echoedRocket.Rocket.rocketName, "TCP rocket payload");
            Console.WriteLine("TCP_NET48_SMOKE_OK PlayerId=" + response.PlayerId +
                " RocketId=" + echoedRocket.GlobalId + " RTT=" + transport.RoundTripMs.ToString("F0") + "ms");
        }
        finally
        {
            transport.Disconnect("Smoke test complete");
            transport.Dispose();
        }
    }

    private static void PacketNumbersMatchServer()
    {
    	Equal(9, (int)PacketType.CreateRocket, "CreateRocket");
    	Equal(12, (int)PacketType.UpdateRocketSecondary, "UpdateRocketSecondary");
    	Equal(20, (int)PacketType.UpdatePart_ResourceModule, "UpdatePart_ResourceModule");
    	Equal(21, (int)PacketType.ShowToastMessage, "ShowToastMessage extension");
    	Equal(22, (int)PacketType.UpdateCheatStatus, "UpdateCheatStatus");
    }

    private static void StringsMatchServerWireFormat()
    {
    	NetOutgoingMessage client = NewOutgoing();
    	client.WriteCompressedString("Earth");
    	NetOutgoingMessage server = NewOutgoing();
    	server.Write("Earth");
    	EqualBytes(server, client, "string bytes");
    }

    private static void JoinResponseAcceptsServerPayload()
    {
    	NetOutgoingMessage server = NewOutgoing();
    	server.Write(42);
    	server.Write(20.0);
    	server.Write(3.0);
    	server.Write(100.5);
    	server.Write(2.5);
    	server.Write((byte)Difficulty.DifficultyType.Normal);

    	var packet = new Packet_JoinResponse();
    	packet.Deserialize(ToIncoming(server));
    	Equal(42, packet.PlayerId, "player id");
    	Equal(string.Empty, packet.SolarSystemName, "default solar system");
    }

    private static void ChatPacketMatchesServerPayload()
    {
    	NetOutgoingMessage server = NewOutgoing();
    	server.Write(7);
    	server.Write("hello");

    	var packet = new Packet_SendChatMessage();
    	packet.Deserialize(ToIncoming(server));
    	Equal(7, packet.SenderId, "chat sender");
    	Equal("hello", packet.Message, "chat text");
    	Equal(Color.white, packet.Color, "default chat color");

    	NetOutgoingMessage client = NewOutgoing();
    	packet.Serialize(client);
    	EqualBytes(server, client, "chat bytes");
    }

    private static void TimeWarpPacketMatchesServerWireFormat()
    {
        NetOutgoingMessage server = NewOutgoing();
        server.Write((byte)TimeWarpOperation.Vote);
        server.Write(42);
        server.Write(7);
        server.Write("tester");
        server.Write(25.0);
        server.Write(true);
        server.Write(1234.5);
        server.Write(30);
        server.Write("vote");

        var packet = new Packet_TimeWarp();
        packet.Deserialize(ToIncoming(server));
        Equal(TimeWarpOperation.Vote, packet.Operation, "time-warp operation");
        Equal(42, packet.VoteId, "time-warp vote id");
        Equal(25.0, packet.Multiplier, "time-warp multiplier");

        NetOutgoingMessage client = NewOutgoing();
        packet.Serialize(client);
        EqualBytes(server, client, "time-warp bytes");
    }

    private static void DestroyPartRoutesByRocketId()
    {
        var packet = new Packet_DestroyPart { RocketId = 41, PartId = 7, WorldTime = 10 };
        Equal(41, ClientPacketRouting.GetRocketId(packet), "destroy-part routing key");
    }

    private static void DiscreteEventsRunOnlyWhenDue()
    {
        True(Interpolator.IsPacketDue(9.9, 10.0), "past event must run");
        True(Interpolator.IsPacketDue(10.0, 10.0), "current event must run");
        False(Interpolator.IsPacketDue(10.1, 10.0), "future event must wait");
    }

    private static void DuplicateTimestampIsSafe()
    {
        Equal(1.0, Interpolator.GetInterpolationFraction(4, 4, 4), "duplicate at same time");
        Equal(1.0, Interpolator.GetInterpolationFraction(4, 4, 5), "duplicate after time");
        Equal(0.5, Interpolator.GetInterpolationFraction(4, 6, 5), "normal midpoint");
    }

    private static void CorrectionDoesNotAdvanceTarget()
    {
        Equal(100.0, Interpolator.GetCorrectionTarget(100.0, 1000.0), "target position");
        Equal(15f, Interpolator.GetCorrectionRotation(15f, 40f), "target rotation");
    }

    private static void TcpFramesSurviveStreamCoalescing()
    {
        var first = new TcpFrame(TcpFrameKind.Packet, 11, new byte[] { 9, 1, 2 }, 19);
        var second = new TcpFrame(TcpFrameKind.Ping, 12, BitConverter.GetBytes(123L), 64);
        byte[] joined = TcpFrameCodec.Encode(first).Concat(TcpFrameCodec.Encode(second)).ToArray();
        using (var stream = new MemoryStream(joined))
        {
            TcpFrame decodedFirst = TcpFrameCodec.Read(stream);
            TcpFrame decodedSecond = TcpFrameCodec.Read(stream);
            Equal(TcpFrameKind.Packet, decodedFirst.Kind, "first kind");
            Equal(11, decodedFirst.Sequence, "first sequence");
            Equal(19, decodedFirst.PayloadBits, "first exact bit length");
            True(decodedFirst.Payload.SequenceEqual(first.Payload), "first payload");
            Equal(TcpFrameKind.Ping, decodedSecond.Kind, "second kind");
            Equal(12, decodedSecond.Sequence, "second sequence");
        }
    }

    private static void TcpSendQueueKeepsLatestRocketState()
    {
        var queue = new TcpSendQueue();
        queue.EnqueueLatest(41, new TcpFrame(TcpFrameKind.Packet, 1, new byte[] { 1 }, 8));
        queue.EnqueueLatest(41, new TcpFrame(TcpFrameKind.Packet, 2, new byte[] { 2 }, 8));
        queue.EnqueueCritical(new TcpFrame(TcpFrameKind.Packet, 3, new byte[] { 3 }, 8));

        TcpFrame critical;
        TcpFrame latest;
        True(queue.TryDequeue(out critical), "critical frame available");
        True(queue.TryDequeue(out latest), "latest state available");
        Equal(3, critical.Sequence, "critical priority");
        Equal(2, latest.Sequence, "old rocket state overwritten");
        Equal(1L, queue.OverwrittenStates, "overwrite statistic");
        Equal(0, queue.Count, "queue drained");
    }

    private static void RocketSyncPolicyLowersIdleTraffic()
    {
        Equal(50, RocketSyncPolicy.GetIntervalMilliseconds(true, false), "controlled 20Hz");
        Equal(200, RocketSyncPolicy.GetIntervalMilliseconds(false, true), "uncontrolled moving 5Hz");
        Equal(3000, RocketSyncPolicy.GetIntervalMilliseconds(false, false), "idle snapshot");
    }

    private static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }

    private static void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{name}: expected {expected}, got {actual}");
    }

    private static void True(bool value, string name)
    {
        if (!value) throw new Exception(name);
    }

    private static void False(bool value, string name)
    {
    	if (value) throw new Exception(name);
    }

    private static NetOutgoingMessage NewOutgoing()
    {
    	return (NetOutgoingMessage)Activator.CreateInstance(typeof(NetOutgoingMessage), nonPublic: true);
    }

    private static NetIncomingMessage ToIncoming(NetOutgoingMessage outgoing)
    {
    	var incoming = (NetIncomingMessage)Activator.CreateInstance(typeof(NetIncomingMessage), nonPublic: true);
    	incoming.Data = outgoing.Data.Take(outgoing.LengthBytes).ToArray();
    	incoming.LengthBits = outgoing.LengthBits;
    	incoming.Position = 0;
    	return incoming;
    }

    private static void EqualBytes(NetOutgoingMessage expected, NetOutgoingMessage actual, string name)
    {
    	Equal(expected.LengthBits, actual.LengthBits, name + " bit length");
    	byte[] left = expected.Data.Take(expected.LengthBytes).ToArray();
    	byte[] right = actual.Data.Take(actual.LengthBytes).ToArray();
    	if (!left.SequenceEqual(right))
    		throw new Exception(name + ": byte payload differs");
    }
    }
