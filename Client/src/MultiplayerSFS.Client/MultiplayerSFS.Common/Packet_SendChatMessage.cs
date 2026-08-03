using Lidgren.Network;
using UnityEngine;

namespace MultiplayerSFS.Common;

public class Packet_SendChatMessage : Packet
{
	public int SenderId { get; set; } = -1;

	public string Message { get; set; }

	public Color Color { get; set; } = Color.white;

	public override PacketType Type => PacketType.SendChatMessage;

	public override void Serialize(NetOutgoingMessage msg)
	{
		((NetBuffer)msg).Write(SenderId);
		((NetBuffer)msg).Write(Message);
	}

	public override void Deserialize(NetIncomingMessage msg)
	{
		SenderId = ((NetBuffer)msg).ReadInt32();
		Message = ((NetBuffer)msg).ReadString();
		Color = Color.white;
		if (msg.LengthBits - msg.Position >= 128)
		{
			float r = ((NetBuffer)msg).ReadFloat();
			float g = ((NetBuffer)msg).ReadFloat();
			float b = ((NetBuffer)msg).ReadFloat();
			float a = ((NetBuffer)msg).ReadFloat();
			Color = new Color(r, g, b, a);
		}
	}
}
