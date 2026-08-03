using System;
using HarmonyLib;
using SFS.Input;
using SFS.UI;
using UnityEngine.SceneManagement;

namespace MultiplayerSFS.Mod.Patches;

public class DisableWorldSavingUI
{
	[HarmonyPatch(typeof(LoadMenu), "OpenSaveMenu", new Type[] { typeof(CloseMode) })]
	public class LoadMenu_OpenSaveMenu
	{
		public static bool Prefix()
		{
			if (ClientManager.multiplayerEnabled.Value && SceneManager.GetActiveScene().name == "World_PC")
			{
				MsgDrawer.main.Log("Saving is disabled in multiplayer");
				return false;
			}
			return true;
		}
	}

	[HarmonyPatch(typeof(Screen_Menu), "Open")]
	public class LoadMenu_Open
	{
		public static bool Prefix(Screen_Menu __instance)
		{
			if (ClientManager.multiplayerEnabled.Value && __instance is LoadMenu && SceneManager.GetActiveScene().name == "World_PC")
			{
				MsgDrawer.main.Log("Saving is disabled in multiplayer");
				return false;
			}
			return true;
		}
	}
}
