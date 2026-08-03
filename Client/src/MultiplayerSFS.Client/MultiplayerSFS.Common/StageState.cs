using System.Collections.Generic;
using System.Linq;
using Lidgren.Network;
using SFS.World;

namespace MultiplayerSFS.Common;

public class StageState : INetData
{
	public int stageID;

	public List<int> partIDs;

	public StageState()
	{
	}

	public StageState(int stageID, List<int> partIDs)
	{
		this.stageID = stageID;
		this.partIDs = partIDs;
	}

	public StageState(StageSave save, Dictionary<int, int> partIndexToID)
	{
		stageID = save.stageId;
		partIDs = save.partIndexes.Select((int idx) => partIndexToID[idx]).ToList();
	}

	public void Serialize(NetOutgoingMessage msg)
	{
		msg.WriteCompressedInt(stageID);
		msg.WriteCollection(partIDs, msg.WriteCompressedInt);
	}

	public void Deserialize(NetIncomingMessage msg)
	{
		stageID = msg.ReadCompressedInt();
		partIDs = msg.ReadCollection((int count) => new List<int>(), msg.ReadCompressedInt);
	}
}
