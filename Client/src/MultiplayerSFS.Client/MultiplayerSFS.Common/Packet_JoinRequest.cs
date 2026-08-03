using Lidgren.Network;

namespace MultiplayerSFS.Common;

public class Packet_JoinRequest : Packet
{
	public string Username { get; set; }
	public string Password { get; set; }
	public string SolarSystemName { get; set; } = "";
	public int ResumePlayerId { get; set; } = -1;
	public string ResumeToken { get; set; } = "";

	public override PacketType Type => PacketType.JoinRequest;

	public override void Serialize(NetOutgoingMessage msg)
	{
		((NetBuffer)msg).Write(Username);
		((NetBuffer)msg).Write(Password);
		((NetBuffer)msg).Write(SolarSystemName);
	}

	public override void Deserialize(NetIncomingMessage msg)
	{
		Username = ((NetBuffer)msg).ReadString();
		Password = ((NetBuffer)msg).ReadString();
		SolarSystemName = ((NetBuffer)msg).ReadString();
	}
}
