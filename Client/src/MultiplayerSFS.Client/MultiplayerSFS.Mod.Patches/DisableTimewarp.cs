using HarmonyLib;
using MultiplayerSFS.Mod;
using SFS.UI;
using SFS.World;
using SFS.World.Maps;

namespace MultiplayerSFS.Mod.Patches;

public class DisableTimewarp
{
	private static readonly double[] Multipliers = { 1, 2, 3, 5, 25, 100, 500, 2500 };

	private static double NextMultiplier()
	{
		double current = ClientManager.world?.timeScale ?? 1;
		foreach (double multiplier in Multipliers)
			if (multiplier > current + 0.0001) return multiplier;
		return 2500;
	}

	private static double PreviousMultiplier()
	{
		double current = ClientManager.world?.timeScale ?? 1;
		for (int i = Multipliers.Length - 1; i >= 0; i--)
			if (Multipliers[i] < current - 0.0001) return Multipliers[i];
		return 1;
	}
	[HarmonyPatch(typeof(WorldTime), "AccelerateTime")]
	public class WorldTime_AccelerateTime
	{
		public static bool Prefix()
		{
			if (ClientManager.multiplayerEnabled.Value)
			{
				ClientManager.RequestTimeScale(NextMultiplier());
				return false;
			}
			return true;
		}
	}

	[HarmonyPatch(typeof(WorldTime), "DecelerateTime")]
	public class WorldTime_DecelerateTime
	{
		public static bool Prefix()
		{
			if (ClientManager.multiplayerEnabled.Value)
			{
				ClientManager.RequestTimeScale(PreviousMultiplier());
				return false;
			}
			return true;
		}
	}

	[HarmonyPatch(typeof(TimewarpTo), "StartTimewarp")]
	public class TimewarpTo_StartTimewarp
	{
		public static bool Prefix()
		{
			if (ClientManager.multiplayerEnabled.Value)
			{
				MsgDrawer.main.Log("Press F7 to request a multiplayer time-warp vote");
				return false;
			}
			return true;
		}
	}
}
