using Lidgren.Network;
using UnityEngine;

namespace MultiplayerSFS.Common;

public class Packet_PlayerConnected : Packet
{
	public int PlayerId { get; set; } = -1;

	public string Username { get; set; }

	public Color IconColor { get; set; }

	public bool PrintMessage { get; set; }

	public override PacketType Type => PacketType.PlayerConnected;

	public override void Serialize(NetOutgoingMessage msg)
	{
		msg.WriteCompressedInt(PlayerId);
		msg.WriteCompressedString(Username);
		msg.WriteCompressedColor(IconColor);
		((NetBuffer)msg).Write(PrintMessage);
	}

	public override void Deserialize(NetIncomingMessage msg)
	{
		PlayerId = msg.ReadCompressedInt();
		Username = msg.ReadCompressedString();
		IconColor = msg.ReadCompressedColor();
		PrintMessage = ((NetBuffer)msg).ReadBoolean();
	}
}
