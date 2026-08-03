using System.Collections.Generic;
using Lidgren.Network;
using SFS.World;

namespace MultiplayerSFS.Common;

public class JointState : INetData
{
	public int id_A;

	public int id_B;

	public JointState()
	{
	}

	public JointState(int id_A, int id_B)
	{
		this.id_A = id_A;
		this.id_B = id_B;
	}

	public JointState(JointSave save, Dictionary<int, int> partIndexToID)
	{
		id_A = partIndexToID[save.partIndex_A];
		id_B = partIndexToID[save.partIndex_B];
	}

	public void Serialize(NetOutgoingMessage msg)
	{
		msg.WriteCompressedInt(id_A);
		msg.WriteCompressedInt(id_B);
	}

	public void Deserialize(NetIncomingMessage msg)
	{
		id_A = msg.ReadCompressedInt();
		id_B = msg.ReadCompressedInt();
	}
}
