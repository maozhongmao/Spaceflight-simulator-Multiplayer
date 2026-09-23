// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using MultiplayerSFS.Common;
using SFS.UI;
using SFS.World;

namespace MultiplayerSFS.Mod;

// 时间加速只负责接收服务端确认并同步本地世界状态；不创建 F7 弹窗或额外按钮。
public static class TimeWarpVoteUI
{
	public static void Receive(Packet_TimeWarp packet)
	{
		if (packet.Operation == TimeWarpOperation.Applied)
			ApplyTimeScale(packet.Multiplier, packet.WorldTime);
		if (!string.IsNullOrEmpty(packet.Message) && MsgDrawer.main != null)
			MsgDrawer.main.Log(packet.Message);
	}

	private static void ApplyTimeScale(double multiplier, double worldTime)
	{
		if (ClientManager.client != null)
			worldTime += ClientManager.client.RoundTripMs / 2000.0 * multiplier;
		if (ClientManager.world != null) ClientManager.world.SetTimeScale(multiplier, worldTime);
		if (WorldTime.main != null)
		{
			WorldTime.main.worldTime = worldTime;
			WorldTime.main.SetState(multiplier, multiplier <= 5, false);
		}
	}
}
