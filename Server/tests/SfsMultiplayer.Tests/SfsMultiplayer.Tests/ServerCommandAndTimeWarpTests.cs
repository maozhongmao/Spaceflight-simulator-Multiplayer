using SfsMultiplayer.Protocol;
using SfsMultiplayer.Server;

namespace SfsMultiplayer.Tests;

public sealed class ServerCommandAndTimeWarpTests
{
    [Fact]
    public void TimeWarpPacketRoundTrips()
    {
        var expected = new TimeWarpPacket
        {
            Operation = TimeWarpOperation.Request,
            VoteId = 42,
            RequesterId = 7,
            RequesterName = "tester",
            Multiplier = 25,
            Approved = true,
            WorldTime = 1234.5,
            TimeoutSeconds = 30,
            Message = "vote"
        };

        var payload = NetPayloadCodec.Serialize(expected);
        var actual = NetPayloadCodec.Deserialize<TimeWarpPacket>(payload.Data, payload.BitLength);

        Assert.Equal(expected.Operation, actual.Operation);
        Assert.Equal(expected.VoteId, actual.VoteId);
        Assert.Equal(expected.RequesterId, actual.RequesterId);
        Assert.Equal(expected.RequesterName, actual.RequesterName);
        Assert.Equal(expected.Multiplier, actual.Multiplier);
        Assert.Equal(expected.Approved, actual.Approved);
        Assert.Equal(expected.WorldTime, actual.WorldTime);
        Assert.Equal(expected.TimeoutSeconds, actual.TimeoutSeconds);
        Assert.Equal(expected.Message, actual.Message);
    }

    [Fact]
    public void ConsoleCanForceAllowedTimeWarpWithoutVote()
    {
        var server = NewServer(new WorldSnapshot { WorldTime = 100 });

        var result = server.ExecuteCommand("timewarp 25");

        Assert.False(result.RequestShutdown);
        Assert.Contains("25", result.Message);
        Assert.Equal(25, server.TimeScale);
    }

    [Fact]
    public void ConsoleRejectsTimeWarpMultiplierAboveMaximum()
    {
        var server = NewServer(new WorldSnapshot { WorldTime = 100 });

        var result = server.ExecuteCommand("timewarp 2500.01");

        Assert.Contains("1 到 2500", result.Message);
        Assert.Equal(1, server.TimeScale);
    }

    [Fact]
    public void ClearDebrisRemovesSmallUncontrolledRocketsOnly()
    {
        var world = new WorldSnapshot { WorldTime = 100 };
        world.Rockets[1] = RocketWithParts("tiny debris", 2);
        world.Rockets[2] = RocketWithParts("large station", 4);
        var server = NewServer(world);

        var result = server.ExecuteCommand("cleardebris 3");

        Assert.Contains("1", result.Message);
        Assert.DoesNotContain(1, world.Rockets.Keys);
        Assert.Contains(2, world.Rockets.Keys);
    }

    private static TcpMultiplayerServer NewServer(WorldSnapshot world) => new(new ServerSettings
    {
        Port = 0,
        StatePath = string.Empty,
        AutoSaveSeconds = 0,
    }, world);

    private static RocketState RocketWithParts(string name, int count)
    {
        var rocket = new RocketState { RocketName = name, Location = NetLocation.Empty };
        for (var i = 0; i < count; i++) rocket.Parts[i + 1] = new PartState();
        return rocket;
    }
}
