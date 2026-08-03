using Lidgren.Network;

namespace MultiplayerSFS.Common;

public class NetLocation : INetData
{
	public Double2 position;

	public Double2 velocity;

	public string address;

	public NetLocation()
	{
	}

	public NetLocation(Double2 pos, Double2 vel, string planetName)
	{
		position = pos;
		velocity = vel;
		address = planetName;
	}

	public void Serialize(NetOutgoingMessage msg)
	{
		msg.WriteCompressedDouble2(position);
		msg.WriteCompressedDouble2(velocity);
		msg.WriteCompressedString(address);
	}

	public void Deserialize(NetIncomingMessage msg)
	{
		position = msg.ReadCompressedDouble2();
		velocity = msg.ReadCompressedDouble2();
		address = msg.ReadCompressedString();
	}
}
