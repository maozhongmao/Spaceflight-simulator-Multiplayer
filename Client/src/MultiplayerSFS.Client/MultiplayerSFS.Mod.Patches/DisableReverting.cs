using HarmonyLib;
using SFS.World;
using SFS.WorldBase;

namespace MultiplayerSFS.Mod.Patches;

public class DisableReverting
{
	[HarmonyPatch(typeof(Revert), "HasRevert")]
	public class Revert_HasRevert
	{
		public static bool Prefix(ref bool __result)
		{
			if (ClientManager.multiplayerEnabled.Value)
			{
				__result = false;
				return false;
			}
			return true;
		}
	}

	[HarmonyPatch(typeof(Revert), "DeleteAll")]
	public class Revert_DeleteAll
	{
		public static bool Prefix()
		{
			return !ClientManager.multiplayerEnabled.Value;
		}
	}

	[HarmonyPatch(typeof(SavingCache), "SaveRevertToLaunch")]
	public class SavingCache_SaveRevertToLaunch
	{
		public static bool Prefix()
		{
			return !ClientManager.multiplayerEnabled.Value;
		}
	}

	[HarmonyPatch(typeof(GameMenus), "UpdateTopCenter")]
	public class GameMenus_UpdateTopCenter
	{
		public static bool Prefix()
		{
			return !ClientManager.multiplayerEnabled.Value;
		}
	}
}
