using Lidgren.Network;

namespace MultiplayerSFS.Common;

public class Packet_UpdatePlayerControl : Packet
{
	public int PlayerId { get; set; } = -1;

	public int RocketId { get; set; } = -1;

	public override PacketType Type => PacketType.UpdatePlayerControl;

	public override void Serialize(NetOutgoingMessage msg)
	{
		((NetBuffer)msg).Write(PlayerId);
		((NetBuffer)msg).Write(RocketId);
	}

	public override void Deserialize(NetIncomingMessage msg)
	{
		PlayerId = ((NetBuffer)msg).ReadInt32();
		RocketId = ((NetBuffer)msg).ReadInt32();
	}
}
