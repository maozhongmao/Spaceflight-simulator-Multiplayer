using Lidgren.Network;

namespace MultiplayerSFS.Common;

public class Packet_UpdatePart_MoveModule : Packet
{
	public double WorldTime { get; set; } = double.NaN;

	public int RocketId { get; set; } = -1;

	public int PartId { get; set; } = -1;

	public float Time { get; set; }

	public float TargetTime { get; set; }

	public override PacketType Type => PacketType.UpdatePart_MoveModule;

	public override void Serialize(NetOutgoingMessage msg)
	{
		((NetBuffer)msg).Write(WorldTime);
		((NetBuffer)msg).Write(RocketId);
		((NetBuffer)msg).Write(PartId);
		((NetBuffer)msg).Write(Time);
		((NetBuffer)msg).Write(TargetTime);
	}

	public override void Deserialize(NetIncomingMessage msg)
	{
		WorldTime = ((NetBuffer)msg).ReadDouble();
		RocketId = ((NetBuffer)msg).ReadInt32();
		PartId = ((NetBuffer)msg).ReadInt32();
		Time = ((NetBuffer)msg).ReadFloat();
		TargetTime = ((NetBuffer)msg).ReadFloat();
	}
}
