using System;
using MultiplayerSFS.Common;

namespace MultiplayerSFS.Mod;

public static class ClientPacketRouting
{
	public static int GetRocketId(Packet packet)
	{
		if (packet is Packet_DestroyPart destroyPart)
		{
			return destroyPart.RocketId;
		}

		throw new ArgumentException("Packet does not have a supported rocket route.", nameof(packet));
	}
}