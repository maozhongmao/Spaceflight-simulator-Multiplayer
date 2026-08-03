using System.Collections.Generic;
using Lidgren.Network;

namespace MultiplayerSFS.Common;

public abstract class Packet : INetData
{
	private static readonly HashSet<PacketType> DoNotDebug = new HashSet<PacketType>
	{
		PacketType.UpdateRocketPrimary,
		PacketType.UpdateRocketSecondary,
		PacketType.UpdatePart_ResourceModule
	};

	public abstract PacketType Type { get; }

	public abstract void Serialize(NetOutgoingMessage msg);

	public abstract void Deserialize(NetIncomingMessage msg);

	public static bool ShouldDebug(PacketType type)
	{
		return !DoNotDebug.Contains(type);
	}
}
