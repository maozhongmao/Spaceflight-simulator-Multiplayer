using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Lidgren.Network;
using MultiplayerSFS.Common;
using SFS;
using SFS.Builds;
using SFS.Career;
using SFS.Input;
using SFS.Stats;
using SFS.UI;
using SFS.World;
using SFS.World.Maps;
using SFS.WorldBase;
using UnityEngine;
using UnityEngine.UI;

namespace MultiplayerSFS.Mod.Patches;

public class DivertLoading
{
	[HarmonyPatch(typeof(SavingCache), "Preload_WorldPersistent")]
	public class SavingCache_Preload_WorldPersistent
	{
		public static bool Prefix(SavingCache __instance, bool needsRocketsAndBranches)
		{
			if (ClientManager.multiplayerEnabled.Value)
			{
				ref SavingCache.Data<WorldSave> reference = ref __instance.FieldRef<SavingCache.Data<WorldSave>>("worldPersistent");
				if (reference == null || (needsRocketsAndBranches && (reference.result.data.rockets == null || reference.result.data.branches == null)))
				{
					reference = new SavingCache.Data<WorldSave>
					{
						thread = new Thread((ThreadStart)delegate
						{
							__instance.FieldRef<SavingCache.Data<WorldSave>>("worldPersistent").result = (success: true, data: WorldSave.CreateEmptyQuicksave(Application.version), log: null);
						})
					};
					reference.thread.Start();
				}
				return false;
			}
			return true;
		}
	}

	[HarmonyPatch(typeof(GameManager), "LoadPersistentAndLaunch")]
	public class GameManager_LoadPersistentAndLaunch
	{
		public static bool Prefix(GameManager __instance)
		{
			if (ClientManager.multiplayerEnabled.Value)
			{
				SavingCache.main.Preload_WorldPersistent(needsRocketsAndBranches: true);
				SavingCache.main.FieldRef<SavingCache.Data<WorldSave>>("worldPersistent") = null;
				AccessTools.Method(typeof(GameManager), "ClearWorld").Invoke(__instance, null);
				CareerState.main.SetState(new WorldSave.CareerState());
				WorldTime.main.worldTime = ClientManager.world.WorldTime;
				WorldTime.main.SetTimewarpIndex_ForLoad(0);
				WorldView.main.SetViewLocation(Base.planetLoader.spaceCenter.LaunchPadLocation);
				WorldView.main.viewDistance.Value = 32f;
				LocalManager.OnLoadWorld();
				AstronautState.main.state = new WorldSave.Astronauts();
				Map.manager.mapMode.Value = false;
				Map.view.view.target.Value = Base.planetLoader.spaceCenter.Planet.mapPlanet;
				Map.view.view.position.Value = Base.planetLoader.spaceCenter.LaunchPadLocation.position;
				Map.view.view.distance.Value = Base.planetLoader.spaceCenter.LaunchPadLocation.position.y * 0.65;
				Map.navigation.SetTarget(Map.view.view.target.Value);
				PlayerController.main.player.Value = null;
				PlayerController.main.cameraDistance.Value = 32f;
				if (__instance.environment.environments != null)
				{
					SFS.World.Environment[] environments = __instance.environment.environments;
					for (int i = 0; i < environments.Length; i++)
					{
						environments[i].terrain?.LoadFully();
					}
				}
				LogManager.main.ClearBranches();
				if (SavingCache.main.TryLoadBuildPersistent(MsgDrawer.main, out var buildPersistent, eraseCache: false))
				{
					RocketManager.SpawnBlueprint(buildPersistent);
				}
				GameCamerasManager.main.InstantlyRotateCamera();
				return false;
			}
			return true;
		}
	}

	[HarmonyPatch(typeof(BuildManager), "<Launch>g__Launch_2|33_5")]
	public static class BuildManager_Launch_2
	{
		[HarmonyReversePatch(HarmonyReversePatchType.Original)]
		public static void OrginalMethod(bool forceVertical)
		{
			throw new NotImplementedException("Harmony Reverse Patch");
		}

		public static bool Prefix(bool forceVertical)
		{
			if ((bool)ClientManager.multiplayerEnabled && BuildManager.main.buildGrid.activeGrid.partsHolder.parts.Count > 0)
			{
				ReplacementMethod(forceVertical);
				return false;
			}
			return true;
		}

		public static async void ReplacementMethod(bool forceVertical)
		{
			HashSet<int> rockets = new HashSet<int>();
			HashSet<int> players = new HashSet<int>();
			bool confirmationOpen = true;
			bool confirmationVisible = true;
			int stackCount = ScreenManager.main.GetStackCount();
			while (confirmationOpen)
			{
				if (UpdateLaunchpadStatus(ref rockets, ref players, out var updateText))
				{
					OrginalMethod(forceVertical);
					break;
				}
				if (confirmationVisible && updateText)
				{
					ScreenManager.main.CloseStack();
					confirmationOpen = true;
					if (players.Count > 0)
					{
						string message = "Waiting for the following players to leave the launchpad:\n";
						message += string.Join("\n", players.Select((int id) => LocalManager.players[id].username));
						Func<Screen_Base> screen = MenuGenerator.CreateMenu(CancelButton.Close, CloseMode.Current, OnOpen, OnClose, TextBuilder.CreateText(() => message), ButtonBuilder.CreateButton(null, () => "Close", OnClose, CloseMode.Current));
						ScreenManager.main.OpenScreen(screen);
					}
					else
					{
						string message2;
						if (rockets.Count == 1)
						{
							message2 = "There is currently 1 uncontrolled rocket blocking the launchpad...";
						}
						else
						{
							message2 = $"There are currently {rockets.Count} uncontrolled rockets blocking the launchpad...";
						}
						Func<Screen_Base> screen2 = MenuGenerator.CreateMenu(CancelButton.Close, CloseMode.Current, OnOpen, OnClose, TextBuilder.CreateText(() => message2), ElementGenerator.HorizontalGroup(delegate(HorizontalLayoutGroup group)
						{
							group.spacing = 10f;
							((RectTransform)group.transform).pivot = new Vector2(0.5f, 0.5f);
						}, true, true, ButtonBuilder.CreateButton(null, () => "Close", OnClose, CloseMode.Current), ButtonBuilder.CreateButton(null, () => "Clear Launchpad", ClearLaunchpad, CloseMode.Current)));
						ScreenManager.main.OpenScreen(screen2);
					}
				}
				await Task.Delay(500);
			}
			void ClearLaunchpad()
			{
				confirmationOpen = false;
				foreach (int item in rockets)
				{
					LocalManager.syncedRockets.Remove(item);
					LocalManager.updateAuthority.Remove(item);
					ClientManager.world.rockets.Remove(item);
					ClientManager.SendPacket(new Packet_DestroyRocket
					{
						RocketId = item
					}, (NetDeliveryMethod)67);
				}
				OrginalMethod(forceVertical);
			}
			void OnClose()
			{
				if (ScreenManager.main.GetStackCount() > stackCount)
				{
					confirmationVisible = false;
				}
				else
				{
					confirmationOpen = false;
				}
			}
			void OnOpen()
			{
				confirmationVisible = true;
			}
		}

		private static bool UpdateLaunchpadStatus(ref HashSet<int> rockets, ref HashSet<int> players, out bool updateText)
		{
			HashSet<int> hashSet = new HashSet<int>();
			HashSet<int> hashSet2 = new HashSet<int>();
			foreach (KeyValuePair<int, RocketState> rocket in ClientManager.world.rockets)
			{
				if (!GameManager_IsOnLaunchpad.IsOnLaunchpad(rocket.Value.location.address, rocket.Value.location.position))
				{
					continue;
				}
				Debug.Log(rocket.Value.location.position);
				hashSet.Add(rocket.Key);
				foreach (KeyValuePair<int, LocalPlayer> player in LocalManager.players)
				{
					if ((int)player.Value.controlledRocket == rocket.Key)
					{
						hashSet2.Add(player.Key);
						break;
					}
				}
			}
			if (hashSet2.Count == 0 && hashSet.Count == 0)
			{
				updateText = false;
				return true;
			}
			updateText = !hashSet2.SetEquals(players) || !hashSet.SetEquals(rockets);
			rockets = hashSet;
			players = hashSet2;
			return false;
		}
	}

	[HarmonyPatch(typeof(GameManager), "IsOnLaunchpad")]
	public static class GameManager_IsOnLaunchpad
	{
		[HarmonyReversePatch(HarmonyReversePatchType.Original)]
		public static bool IsOnLaunchpad(string planet, Double2 postion)
		{
			throw new NotImplementedException("Harmony Reverse Patch");
		}
	}
}
