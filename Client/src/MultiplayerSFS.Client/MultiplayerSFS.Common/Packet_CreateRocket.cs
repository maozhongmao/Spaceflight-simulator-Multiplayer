using Lidgren.Network;

namespace MultiplayerSFS.Common;

public class Packet_CreateRocket : Packet
{
	public double WorldTime { get; set; } = double.NaN;

	public int LocalId { get; set; } = -1;

	public int GlobalId { get; set; } = -1;

	public bool ForLaunch { get; set; }

	public RocketState Rocket { get; set; }

	public override PacketType Type => PacketType.CreateRocket;

	public override void Serialize(NetOutgoingMessage msg)
	{
		msg.WriteCompressedDouble(WorldTime);
		msg.WriteCompressedInt(LocalId);
		msg.WriteCompressedInt(GlobalId);
		((NetBuffer)msg).Write(ForLaunch);
		msg.Write(Rocket);
	}

	public override void Deserialize(NetIncomingMessage msg)
	{
		WorldTime = msg.ReadCompressedDouble();
		LocalId = msg.ReadCompressedInt();
		GlobalId = msg.ReadCompressedInt();
		ForLaunch = ((NetBuffer)msg).ReadBoolean();
		Rocket = msg.Read<RocketState>();
	}
}
