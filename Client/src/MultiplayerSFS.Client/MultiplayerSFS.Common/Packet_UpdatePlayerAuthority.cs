using System;
using System.Collections.Generic;
using Lidgren.Network;

namespace MultiplayerSFS.Common;

public class Packet_UpdatePlayerAuthority : Packet
{
	public HashSet<int> RocketIds { get; set; }

	public override PacketType Type => PacketType.UpdatePlayerAuthority;

	public override void Serialize(NetOutgoingMessage msg)
	{
		msg.WriteCollection((ICollection<int>)RocketIds, (Action<int>)((NetBuffer)msg).Write);
	}

	public override void Deserialize(NetIncomingMessage msg)
	{
		RocketIds = msg.ReadCollection((Func<int, HashSet<int>>)((int count) => new HashSet<int>(count)), (Func<int>)((NetBuffer)msg).ReadInt32);
	}
}
