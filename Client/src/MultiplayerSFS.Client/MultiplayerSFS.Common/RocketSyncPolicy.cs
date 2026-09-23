// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace MultiplayerSFS.Common;

public static class RocketSyncPolicy
{
	public const int ControlledIntervalMilliseconds = 50;
	public const int UncontrolledMovingIntervalMilliseconds = 200;
	// 空闲火箭的同步间隔。回归测试（ClientRegressionTests: RocketSyncPolicyLowersIdleTraffic）
	// 的规格是 1000ms；此前被"收紧插值"改动改成 500 后没回退，导致空闲流量翻倍。
	public const int IdleIntervalMilliseconds = 1000;

	public static int GetIntervalMilliseconds(bool controlled, bool moving)
	{
		return GetIntervalMilliseconds(controlled, moving, null);
	}

	public static int GetIntervalMilliseconds(bool controlled, bool moving, NetworkAdaptiveProfile profile)
	{
		if (profile == null)
		{
			if (controlled) return ControlledIntervalMilliseconds;
			return moving ? UncontrolledMovingIntervalMilliseconds : IdleIntervalMilliseconds;
		}
		if (controlled) return profile.ControlledIntervalMilliseconds;
		return moving ? profile.MovingIntervalMilliseconds : profile.IdleIntervalMilliseconds;
	}
}
