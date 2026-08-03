using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Lidgren.Network;
using MultiplayerSFS.Common;
using SFS.World;
using UnityEngine;

namespace MultiplayerSFS.Mod.Patches;

public class DivertRocketSwitching
{
	[HarmonyPatch(typeof(PlayerController), "SmoothChangePlayer")]
	public static class PlayerController_SmoothChangePlayer
	{
		public static bool Prefix(Player newPlayer)
		{
			if (TrySwitchPlayer(newPlayer))
			{
				if (PlayerController.main.player.Value == null)
				{
					PlayerController.main.player.Value = newPlayer;
				}
				return true;
			}
			return false;
		}
	}

	[HarmonyPatch(typeof(Rocket), "SetPlayerToBestControllable")]
	public static class Rocket_SetPlayerToBestControllable
	{
		public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
		{
			Label label_CheckSwitch = generator.DefineLabel();
			foreach (CodeInstruction code in instructions)
			{
				if (code.opcode == OpCodes.Ret)
				{
					yield return code.WithLabels(label_CheckSwitch);
					continue;
				}
				if (code.opcode == OpCodes.Brfalse_S)
				{
					yield return new CodeInstruction(OpCodes.Ldloc_0);
					yield return CodeInstruction.Call(typeof(Rocket_SetPlayerToBestControllable), "CheckSwitch");
					yield return new CodeInstruction(OpCodes.Brfalse, label_CheckSwitch);
				}
				yield return code;
			}
		}

		public static bool CheckSwitch(List<Rocket> rockets)
		{
			if (rockets[0] != null)
			{
				return TrySwitchPlayer(rockets[0]);
			}
			return false;
		}
	}

	[HarmonyPatch(typeof(GameSelector), "SwitchTo")]
	public static class GameSelector_SwitchTo
	{
		public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
		{
			bool found_ldsfld = false;
			Label label_CheckSwitch = generator.DefineLabel();
			foreach (CodeInstruction code in instructions)
			{
				if (!found_ldsfld && code.opcode == OpCodes.Ldsfld && code.operand is FieldInfo { Name: "view" })
				{
					found_ldsfld = true;
					yield return new CodeInstruction(OpCodes.Ldloc_0);
					yield return new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(MapPlayer), "Player"));
					yield return CodeInstruction.Call(typeof(DivertRocketSwitching), "TrySwitchPlayer");
					yield return new CodeInstruction(OpCodes.Brfalse, label_CheckSwitch);
				}
				if (found_ldsfld && code.opcode == OpCodes.Ret)
				{
					yield return code.WithLabels(label_CheckSwitch);
				}
				else
				{
					yield return code;
				}
			}
		}
	}

	public static bool TrySwitchPlayer(Player player)
	{
		if ((bool)ClientManager.multiplayerEnabled)
		{
			if (player is Rocket rocket)
			{
				int id = LocalManager.GetSyncedRocketID(rocket);
				if (id >= 0)
				{
					if (LocalManager.players.Any((KeyValuePair<int, LocalPlayer> kvp) => kvp.Key != ClientManager.playerId && (int)kvp.Value.controlledRocket == id))
					{
						return false;
					}
					LocalManager.Player.controlledRocket.Value = id;
					ClientManager.SendPacket(new Packet_UpdatePlayerControl
					{
						PlayerId = ClientManager.playerId,
						RocketId = id
					}, (NetDeliveryMethod)67);
					return true;
				}
				id = LocalManager.GetUnsyncedRocketID(rocket);
				if (id >= 0)
				{
					LocalManager.unsyncedToControl = id;
					return false;
				}
				Debug.LogError("`TrySwitchPlayer`: `player` isn't registered!");
				return false;
			}
			if ((object)player == null)
			{
				Debug.LogError("`TrySwitchPlayer`: `player` is null!");
				return false;
			}
			Debug.LogError("`TrySwitchPlayer`: `player` is not a rocket!");
			return false;
		}
		return true;
	}
}
