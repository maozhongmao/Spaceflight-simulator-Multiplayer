using System.Net.Sockets;
using SfsMultiplayer.Protocol;
using SfsMultiplayer.Server;

namespace SfsMultiplayer.Tests;

public sealed class TcpNetworkV2Tests
{
    [Fact(Timeout = 15_000)]
    public async Task FixedHandshakeClientCanJoinCurrentServer()
    {
        var settings = new ServerSettings { Port = 0, MaxConnections = 2 };
        await using var server = new TcpMultiplayerServer(settings, new WorldSnapshot());
        server.Start();
        using var stop = new CancellationTokenSource();
        var serverTask = server.RunAsync(stop.Token);

        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync("127.0.0.1", server.Port);
        using NetworkStream stream = client.GetStream();
        var helloPayload = SfsMultiplayer.Protocol.SessionHandshakeCodec.EncodeHello(new JoinRequestPacket
        {
            Username = "fixed-handshake-client",
        });
        await TcpFrameCodec.WriteAsync(stream,
            new TcpFrame(TcpFrameKind.Hello, SfsMultiplayer.Protocol.SessionHandshakeCodec.Version,
                helloPayload, helloPayload.Length * 8),
            CancellationToken.None);

        var reply = await TcpFrameCodec.ReadAsync(stream, CancellationToken.None);
        try
        {
            Assert.Equal(TcpFrameKind.HelloAck, reply.Kind);
            Assert.Equal(SfsMultiplayer.Protocol.SessionHandshakeCodec.Version, reply.Sequence);
            Assert.NotEqual(-1, SfsMultiplayer.Protocol.SessionHandshakeCodec.DecodeAck(reply.Payload).PlayerId);
            Assert.False(string.IsNullOrEmpty(SfsMultiplayer.Protocol.SessionHandshakeCodec.DecodeAck(reply.Payload).ResumeToken));
        }
        finally
        {
            stop.Cancel();
            await serverTask;
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task TcpClientCanJoinReceiveSnapshotAndHeartbeat()
    {
        var world = new WorldSnapshot { WorldTime = 4321, Difficulty = DifficultyType.Normal };
        world.Rockets.Add(77, new RocketState
        {
            RocketName = "TCP Test",
            Location = new NetLocation(1, 2, 3, 4, "Earth")
        });
        var settings = new ServerSettings
        {
            Port = 0,
            Password = "secret",
            MaxConnections = 4,
        };

        await using var server = new TcpMultiplayerServer(settings, world);
        server.Start();
        using var stop = new CancellationTokenSource();
        var serverTask = server.RunAsync(stop.Token);

        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync("127.0.0.1", server.Port);
        using NetworkStream stream = client.GetStream();

        var helloPayload = SfsMultiplayer.Protocol.SessionHandshakeCodec.EncodeHello(new JoinRequestPacket
        {
            Username = "tcp-tester",
            Password = "secret",
        });
        await TcpFrameCodec.WriteAsync(stream,
            new TcpFrame(TcpFrameKind.Hello, SfsMultiplayer.Protocol.SessionHandshakeCodec.Version,
                helloPayload, helloPayload.Length * 8), CancellationToken.None);

        var helloAck = await TcpFrameCodec.ReadAsync(stream, CancellationToken.None);
        Assert.Equal(TcpFrameKind.HelloAck, helloAck.Kind);
        Assert.Equal(SfsMultiplayer.Protocol.SessionHandshakeCodec.Version, helloAck.Sequence);
        var response = SfsMultiplayer.Protocol.SessionHandshakeCodec.DecodeAck(helloAck.Payload);
        Assert.NotEqual(-1, response.PlayerId);

        CreateRocketPacket? snapshot = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && snapshot is null)
        {
            var frame = await TcpFrameCodec.ReadAsync(stream, CancellationToken.None);
            if (frame.Kind != TcpFrameKind.Packet) continue;
            var message = NetPayloadCodec.ToIncoming(frame.Payload, frame.PayloadBits);
            if ((PacketType)message.ReadByte() != PacketType.CreateRocket) continue;
            snapshot = new CreateRocketPacket();
            snapshot.Deserialize(message);
        }
        Assert.NotNull(snapshot);
        Assert.Equal(77, snapshot!.GlobalId);
        Assert.Equal("TCP Test", snapshot.Rocket.RocketName);

        byte[] ping = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
        await TcpFrameCodec.WriteAsync(stream,
            new TcpFrame(TcpFrameKind.Ping, 99, ping, ping.Length * 8), CancellationToken.None);
        TcpFrame pong;
        do
        {
            pong = await TcpFrameCodec.ReadAsync(stream, CancellationToken.None);
        } while (pong.Kind != TcpFrameKind.Pong);
        Assert.Equal(99, pong.Sequence);
        Assert.Equal(ping, pong.Payload);

        stop.Cancel();
        await serverTask;
    }
}
