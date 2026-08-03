using Lidgren.Network;
using UnityEngine;

namespace MultiplayerSFS.Common;

public class Packet_UpdatePlayerColor : Packet
{
	public int PlayerId { get; set; } = -1;

	public Color Color { get; set; }

	public override PacketType Type => PacketType.UpdatePlayerColor;

	public override void Serialize(NetOutgoingMessage msg)
	{
		msg.WriteCompressedInt(PlayerId);
		msg.WriteCompressedColor(Color);
	}

	public override void Deserialize(NetIncomingMessage msg)
	{
		PlayerId = msg.ReadCompressedInt();
		Color = msg.ReadCompressedColor();
	}
}
