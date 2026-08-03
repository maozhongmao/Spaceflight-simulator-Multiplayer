using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using HarmonyLib;
using Lidgren.Network;
using ModLoader.Helpers;
using MultiplayerSFS.Common;
using SFS.Parts;
using SFS.Parts.Modules;
using SFS.UI;
using SFS.World;
using SFS.WorldBase;
using UnityEngine;

namespace MultiplayerSFS.Mod;

public static class LocalManager
{
	public static Dictionary<int, LocalPlayer> players;

	public static Dictionary<int, LocalRocket> syncedRockets;

	public static Dictionary<int, LocalRocket> unsyncedRockets;

	public static HashSet<int> updateAuthority;

	public static int unsyncedToControl = -1;

	public const DestructionReason CustomDestructionReason = (DestructionReason)4;

	public static DestructionReason TrueDestructionReason = DestructionReason.Intentional;

	public static double updateRocketsPeriod = 10.0;

	private static Timer updateTimer;

	private static readonly Dictionary<ResourceModule, double> prevResourcePercents = new Dictionary<ResourceModule, double>();

	private static readonly Dictionary<int, DateTime> lastRocketStateSend = new Dictionary<int, DateTime>();

	private static Queue mainThreadActions = new Queue();

	private static object mainThreadActionsLock = new object();

	public static LocalPlayer Player
	{
		get
		{
			if (players.TryGetValue(ClientManager.playerId, out var value))
			{
				return value;
			}
			return null;
		}
	}

	private static Rocket RocketPrefab => AccessTools.StaticFieldRefAccess<Rocket>(typeof(RocketManager), "prefab");

	public static void Initialize()
	{
		if (updateTimer != null)
		{
			return;
		}
		players = new Dictionary<int, LocalPlayer>();
		syncedRockets = new Dictionary<int, LocalRocket>();
		unsyncedRockets = new Dictionary<int, LocalRocket>();
		updateAuthority = new HashSet<int>();
		unsyncedToControl = -1;
		updateTimer = new Timer
		{
			Interval = updateRocketsPeriod,
			AutoReset = true,
			Enabled = true
		};
		updateTimer.Elapsed += delegate
		{
			lock (mainThreadActionsLock)
			{
				mainThreadActions.Enqueue((Action)delegate
				{
					SendUpdatePackets();
				});
			}
		};
		SceneHelper.OnHomeSceneLoaded += new Action(DisableUpdateTimer);
	}

	public static void Update()
	{
		if (ClientManager.multiplayerEnabled.Value && Input.GetKeyDown(KeyCode.F5))
		{
			ForcePositionSync();
		}
		while (true)
		{
			Action action = null;
			lock (mainThreadActionsLock)
			{
				if (mainThreadActions.Count <= 0)
				{
					break;
				}
				action = (Action)mainThreadActions.Dequeue();
			}
			action?.Invoke();
		}
	}

	public static void DisableUpdateTimer()
	{
		updateTimer?.Close();
		updateTimer = null;
		lastRocketStateSend.Clear();
		prevResourcePercents.Clear();
		lock (mainThreadActionsLock)
		{
			mainThreadActions.Clear();
		}
		SceneHelper.OnHomeSceneLoaded -= new Action(DisableUpdateTimer);
	}

	public static void ForcePositionSync()
	{
		if (updateAuthority == null || syncedRockets == null || ClientManager.client == null || !ClientManager.client.Connected)
		{
			return;
		}
		var sent = 0;
		foreach (var rocketId in updateAuthority)
		{
			if (!syncedRockets.TryGetValue(rocketId, out var localRocket) || localRocket.rocket == null)
			{
				continue;
			}
			var packet = localRocket.rocket.ToUpdatePacketPrimary(rocketId);
			if (ClientManager.world.rockets.TryGetValue(rocketId, out var state))
			{
				state.UpdateRocketPrimary(packet);
			}
			ClientManager.SendPacket(packet, (NetDeliveryMethod)67);
			ClientManager.client.RequestRocketSnapshot(rocketId);
			lastRocketStateSend[rocketId] = DateTime.UtcNow;
			sent++;
		}
		if (sent > 0)
		{
			MsgDrawer.main?.Log("已强制同步 " + sent + " 枚火箭的位置。");
		}
	}

	public static void SendUpdatePackets()
	{
		DateTime now = DateTime.UtcNow;
		NetworkAdaptiveProfile adaptive = ClientManager.client?.AdaptiveProfile ?? NetworkAdaptationPolicy.Evaluate(0, 0, 0);
		foreach (ResourceModule item in prevResourcePercents.Keys.ToList())
		{
			if (item == null)
			{
				prevResourcePercents.Remove(item);
			}
		}
		foreach (int item2 in updateAuthority)
		{
			if (syncedRockets.TryGetValue(item2, out var localRocket))
			{
				Rocket rocket = localRocket.rocket;
				if ((object)rocket != null)
				{
					bool controlled = players != null && players.Values.Any(player => player.controlledRocket.Value == item2);
					bool moving = rocket.location.Value.velocity.sqrMagnitude > 0.01 || Math.Abs(rocket.rb2d.angularVelocity) > 0.1f;
					int interval = RocketSyncPolicy.GetIntervalMilliseconds(controlled, moving, adaptive);
					if (lastRocketStateSend.TryGetValue(item2, out DateTime lastSend) &&
						(now - lastSend).TotalMilliseconds < interval)
					{
						continue;
					}
					lastRocketStateSend[item2] = now;
					Packet_UpdateRocketPrimary packet = rocket.ToUpdatePacketPrimary(item2);
					if (ClientManager.world.rockets.TryGetValue(item2, out var value))
					{
						value.UpdateRocketPrimary(packet);
					}
					else
					{
						Debug.LogError("Missing rocket state while trying to send update packets!");
					}
					ClientManager.SendPacket(packet, (NetDeliveryMethod)67);
					if (controlled || moving)
					{
						Packet_UpdateRocketSecondary packet2 = rocket.ToUpdatePacketSecondary(item2);
						if (ClientManager.world.rockets.TryGetValue(item2, out var state)) state.UpdateRocketSecondary(packet2);
						ClientManager.SendPacket(packet2, (NetDeliveryMethod)67);
					}
					if (!rocket.physics.PhysicsMode)
					{
						continue;
					}
					ResourceModule[] localGroups = rocket.resources.localGroups;
					foreach (ResourceModule resourceModule in localGroups)
					{
						if (prevResourcePercents.TryGetValue(resourceModule, out var value2) && value2 != resourceModule.resourcePercent.Value)
						{
							ClientManager.SendPacket(new Packet_UpdatePart_ResourceModule
							{
								WorldTime = ClientManager.world.WorldTime,
								RocketId = item2,
								PartIds = (from r in resourceModule.children
									select r.GetComponentInParent<Part>() into p
									select localRocket.GetPartID(p)).ToHashSet(),
								ResourcePercent = resourceModule.resourcePercent.Value
							}, (NetDeliveryMethod)67);
						}
						prevResourcePercents[resourceModule] = resourceModule.resourcePercent.Value;
					}
					continue;
				}
			}
			Debug.LogError("Missing local rocket while trying to send update packets!");
		}
	}

	public static int GetSyncedRocketID(Rocket rocket)
	{
		try
		{
			return syncedRockets.First((KeyValuePair<int, LocalRocket> kvp) => kvp.Value.rocket == rocket).Key;
		}
		catch (InvalidOperationException)
		{
			return -1;
		}
	}

	public static int GetUnsyncedRocketID(Rocket rocket)
	{
		try
		{
			return unsyncedRockets.First((KeyValuePair<int, LocalRocket> kvp) => kvp.Value.rocket == rocket).Key;
		}
		catch (InvalidOperationException)
		{
			return -1;
		}
	}

	public static int GetLocalPartID(int rocketId, Part part)
	{
		try
		{
			return syncedRockets[rocketId].parts.First((KeyValuePair<int, Part> kvp) => kvp.Value == part).Key;
		}
		catch (InvalidOperationException)
		{
			return -1;
		}
	}

	public static Packet_UpdateRocketPrimary ToUpdatePacketPrimary(this Rocket rocket, int id)
	{
		return new Packet_UpdateRocketPrimary
		{
			WorldTime = ClientManager.world.WorldTime,
			RocketId = id,
			Location = rocket.location.Value.ToNetLocation(),
			Rotation = rocket.rb2d.transform.eulerAngles.z,
			AngularVelocity = rocket.rb2d.angularVelocity
		};
	}

	public static Packet_UpdateRocketSecondary ToUpdatePacketSecondary(this Rocket rocket, int id)
	{
		return new Packet_UpdateRocketSecondary
		{
			WorldTime = ClientManager.world.WorldTime,
			RocketId = id,
			Input_Turn = rocket.arrowkeys.turnAxis,
			Input_Raw = rocket.arrowkeys.rawArrowkeysAxis,
			Input_Horizontal = rocket.arrowkeys.horizontalAxis,
			Input_Vertical = rocket.arrowkeys.verticalAxis,
			ThrottlePercent = rocket.throttle.throttlePercent,
			ThrottleOn = rocket.throttle.throttleOn,
			RCS = rocket.arrowkeys.rcs
		};
	}

	public static LocalRocket SpawnLocalRocket(RocketState state)
	{
		Rocket rocket = UnityEngine.Object.Instantiate(RocketPrefab);
		rocket.rocketName = state.rocketName;
		rocket.throttle.throttleOn.Value = state.throttleOn;
		rocket.throttle.throttlePercent.Value = state.throttlePercent;
		rocket.arrowkeys.rcs.Value = state.RCS;
		Dictionary<int, Part> parts = new Dictionary<int, Part>(state.parts.Count);
		foreach (KeyValuePair<int, PartState> part3 in state.parts)
		{
			OwnershipState ownershipState;
			Part value = PartsLoader.CreatePart(part3.Value.part, null, null, OnPartNotOwned.Allow, out ownershipState);
			parts.Add(part3.Key, value);
		}
		List<PartJoint> list = new List<PartJoint>(state.joints.Count);
		foreach (JointState joint in state.joints)
		{
			if (joint.id_A != -1 && joint.id_B != -1)
			{
				Part part = parts[joint.id_A];
				Part part2 = parts[joint.id_B];
				list.Add(new PartJoint(part, part2, part2.Position - part.Position));
			}
		}
		rocket.SetJointGroup(new JointGroup(list, parts.Values.ToList()));
		rocket.rb2d.transform.eulerAngles = new Vector3(0f, 0f, state.rotation);
		rocket.physics.SetLocationAndState(state.location.ToVanillaLocation(), physicsMode: true);
		rocket.rb2d.angularVelocity = state.angularVelocity;
		foreach (StageState stage in state.stages)
		{
			List<Part> parts2 = stage.partIDs.Select((int id) => parts[id]).ToList();
			rocket.staging.InsertStage(new Stage(stage.stageID, parts2), record: false);
		}
		return new LocalRocket(rocket, parts);
	}

	public static void DestroyLocalRocket(int id)
	{
		if (syncedRockets.TryGetValue(id, out var value) && value.rocket != null)
		{
			TrueDestructionReason = DestructionReason.Intentional;
			RocketManager.DestroyRocket(value.rocket, (DestructionReason)4);
		}
		syncedRockets.Remove(id);
		lastRocketStateSend.Remove(id);
	}

	public static void OnLoadWorld()
	{
		unsyncedRockets.Clear();
		foreach (KeyValuePair<int, RocketState> rocket in ClientManager.world.rockets)
		{
			DestroyLocalRocket(rocket.Key);
			LocalRocket value = SpawnLocalRocket(rocket.Value);
			syncedRockets.Add(rocket.Key, value);
		}
	}

	public static Location ToVanillaLocation(this NetLocation loc)
	{
		return new Location(loc.address.GetPlanet(), loc.position, loc.velocity);
	}

	public static NetLocation ToNetLocation(this Location loc)
	{
		return new NetLocation(loc.position, loc.velocity, loc.planet.codeName);
	}

	public static void OnPacket_CreateRocket(Packet_CreateRocket packet)
	{
		if (syncedRockets.TryGetValue(packet.GlobalId, out var _))
		{
			DestroyLocalRocket(packet.GlobalId);
			LocalRocket localRocket = SpawnLocalRocket(packet.Rocket);
			syncedRockets.Add(packet.GlobalId, localRocket);
			if (localRocket.interpolator != null)
			{
				localRocket.interpolator.updateBuffer.Clear();
				localRocket.interpolator.packetBuffer.Clear();
				localRocket.interpolator.currentUpdate = localRocket.rocket.ToUpdatePacketPrimary(packet.GlobalId);
				localRocket.interpolator.isNewlyCreated = true;
			}
			if ((int)Player.controlledRocket == packet.GlobalId)
			{
				PlayerController.main.player.Value = localRocket.rocket;
			}
			return;
		}
		DestroyLocalRocket(packet.GlobalId);
		if (unsyncedRockets.TryGetValue(packet.LocalId, out var value2))
		{
			unsyncedRockets.Remove(packet.LocalId);
			syncedRockets.Add(packet.GlobalId, value2);
			if (packet.LocalId == unsyncedToControl)
			{
				unsyncedToControl = -1;
				PlayerController.main.SmoothChangePlayer(value2.rocket);
				GameCamerasManager.main.InstantlyRotateCamera();
				Menu.loading.Close();
			}
		}
		else if (GameManager.main != null)
		{
			LocalRocket localRocket2 = SpawnLocalRocket(packet.Rocket);
			syncedRockets.Add(packet.GlobalId, localRocket2);
			if (localRocket2.interpolator != null)
			{
				localRocket2.interpolator.updateBuffer.Clear();
				localRocket2.interpolator.packetBuffer.Clear();
				localRocket2.interpolator.currentUpdate = localRocket2.rocket.ToUpdatePacketPrimary(packet.GlobalId);
				localRocket2.interpolator.isNewlyCreated = true;
				localRocket2.rocket.physics.SetLocationAndState(packet.Rocket.location.ToVanillaLocation(), physicsMode: true);
				localRocket2.rocket.rb2d.transform.eulerAngles = new Vector3(0f, 0f, packet.Rocket.rotation);
				localRocket2.rocket.rb2d.angularVelocity = packet.Rocket.angularVelocity;
			}
			if ((int)Player.controlledRocket == packet.GlobalId)
			{
				PlayerController.main.player.Value = localRocket2.rocket;
			}
		}
	}
}
