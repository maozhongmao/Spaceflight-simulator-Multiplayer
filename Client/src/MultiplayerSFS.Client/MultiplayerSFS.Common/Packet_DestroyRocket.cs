using Lidgren.Network;
using SFS.World;

namespace MultiplayerSFS.Common;

public class Packet_DestroyRocket : Packet
{
	public double WorldTime { get; set; } = double.NaN;

	public int RocketId { get; set; } = -1;

	public DestructionReason Reason { get; set; }

	public override PacketType Type => PacketType.DestroyRocket;

	public override void Serialize(NetOutgoingMessage msg)
	{
		((NetBuffer)msg).Write(WorldTime);
		((NetBuffer)msg).Write(RocketId);
		((NetBuffer)msg).Write((byte)Reason);
	}

	public override void Deserialize(NetIncomingMessage msg)
	{
		WorldTime = ((NetBuffer)msg).ReadDouble();
		RocketId = ((NetBuffer)msg).ReadInt32();
		Reason = (DestructionReason)((NetBuffer)msg).ReadByte();
	}
}
