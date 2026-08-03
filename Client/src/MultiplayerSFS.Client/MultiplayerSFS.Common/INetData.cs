using Lidgren.Network;

namespace MultiplayerSFS.Common;

public interface INetData
{
	void Serialize(NetOutgoingMessage msg);

	void Deserialize(NetIncomingMessage msg);
}
