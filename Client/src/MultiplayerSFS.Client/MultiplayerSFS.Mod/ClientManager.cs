using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lidgren.Network;
using MultiplayerSFS.Common;
using SFS;
using SFS.UI;
using SFS.Variables;
using SFS.World;
using SFS.WorldBase;
using UnityEngine;

namespace MultiplayerSFS.Mod;

public static class ClientManager
{
	public static Bool_Local multiplayerEnabled = new Bool_Local
	{
		Value = false
	};

	public static TcpClientTransport client;

	public static WorldState world;

	public static int playerId;

	private static bool disconnectionHandled;

	private static bool connecting;

	private static bool networkReady;

	private static JoinInfo activeJoinInfo;
	private static string resumeToken;
	private static bool recoveryRunning;

	public static async Task TryConnect(JoinInfo info)
	{
		connecting = true;
		activeJoinInfo = info;
		resumeToken = string.Empty;
		recoveryRunning = false;
		disconnectionHandled = false;
		client?.Disconnect("Re-attempting join request");
		client = new TcpClientTransport();
		string solarSystemName = "";
		try
		{
			string path = Path.Combine(Application.dataPath, "Custom Solar Systems");
			if (Directory.Exists(path))
			{
				string[] directories = Directory.GetDirectories(path);
				if (directories.Length != 0)
				{
					solarSystemName = Path.GetFileName(directories[0]);
					Debug.Log("Found local solar system: " + solarSystemName);
				}
			}
		}
		catch (Exception ex)
		{
			Debug.LogWarning("Failed to check local solar systems: " + ex.Message);
		}

		Menu.loading.Open("Connecting to SFS Multiplayer V1.0.6.2...");
		try
		{
			Packet_JoinResponse response = await client.ConnectAsync(info.address, info.port,
				new Packet_JoinRequest
				{
					Username = info.username,
					Password = info.password,
					SolarSystemName = solarSystemName
				});
			connecting = false;
			resumeToken = response.ResumeToken;
			Menu.loading.Close();
			Debug.Log("[MP-CONNECT] LOAD_WORLD_BEGIN player=" + response.PlayerId);
			LoadWorld(response);
			Debug.Log("[MP-CONNECT] LOAD_WORLD_DISPATCHED player=" + response.PlayerId);
		}
		catch (Exception ex)
		{
			connecting = false;
			disconnectionHandled = true;
			Menu.loading.Close();
			client.Disconnect(ex.Message);
			throw;
		}
	}

	public static void LoadWorld(Packet_JoinResponse packet_JoinResponse)
	{
		playerId = packet_JoinResponse.PlayerId;
		LocalManager.updateRocketsPeriod = packet_JoinResponse.UpdateRocketsPeriod;
		world = new WorldState
		{
			initWorldTime = packet_JoinResponse.WorldTime,
			difficulty = packet_JoinResponse.Difficulty,
			solarSystemName = packet_JoinResponse.SolarSystemName
		};
		LocalManager.Initialize();
		// ChatWindow.CreateCooldownTimer(packet_JoinResponse.ChatMessageCooldown);
		networkReady = true;
		Debug.Log("[MP-CONNECT] LOAD_WORLD_READY player=" + playerId);
		bool flag = false;
		if (!string.IsNullOrEmpty(packet_JoinResponse.SolarSystemName))
		{
			flag = Directory.Exists(Path.Combine(Application.dataPath, "Custom Solar Systems", packet_JoinResponse.SolarSystemName));
			Debug.Log($"Solar system '{packet_JoinResponse.SolarSystemName}' exists locally: {flag}");
		}
		WorldSettings worldSettings = new WorldSettings(new SolarSystemReference(packet_JoinResponse.SolarSystemName), new Difficulty
		{
			difficulty = world.difficulty
		}, new WorldMode(WorldMode.Mode.Sandbox)
		{
			allowQuicksaves = false
		}, new WorldPlaytime(), new SandboxSettings.Data());
		typeof(WorldBaseManager).GetMethod("EnterWorld", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Base.worldBase, new object[3]
		{
			null,
			worldSettings,
			new Action(Base.sceneLoader.LoadHubScene)
		});
	}

	public static void UpdateNetwork()
	{
		TcpClientTransport transport = client;
		if (transport == null || connecting || !networkReady) return;
		if (!transport.Connected && !disconnectionHandled && multiplayerEnabled.Value)
		{
			if (!recoveryRunning && activeJoinInfo != null && !string.IsNullOrEmpty(resumeToken))
			{
				recoveryRunning = true;
				_ = RecoverTcpAsync(transport.LastDisconnectReason);
				return;
			}
			HandleDisconnect(transport.LastDisconnectReason);
			return;
		}

		TcpFrame frame;
		int processed = 0;
		while (processed++ < 256 && transport.TryReceive(out frame))
		{
			if (frame.Kind == TcpFrameKind.Packet)
			{
				try
				{
					HandlePacket(NetPayloadCodec.ToIncoming(frame.Payload, frame.PayloadBits));
				}
				catch (Exception ex)
				{
					Debug.LogError("TCP packet processing failed: " + ex);
				}
			}
			else if (frame.Kind == TcpFrameKind.Disconnect)
			{
				HandleDisconnect(transport.LastDisconnectReason);
			}
		}
		if (!transport.Connected && !disconnectionHandled && multiplayerEnabled.Value)
		{
			if (recoveryRunning) return;
			HandleDisconnect(transport.LastDisconnectReason);
		}
	}

	private static async Task RecoverTcpAsync(string reason)
	{
		Debug.LogWarning("[MP-RECOVERY] TCP disconnected, keeping UDP alive and restoring the session: " + reason);
		if (MsgDrawer.main != null) MsgDrawer.main.Log("TCP disconnected, restoring session...");
		try
		{
			for (int attempt = 0; attempt < 20 && !disconnectionHandled; attempt++)
			{
				try
				{
					Packet_JoinResponse response = await client.ResumeAsync(activeJoinInfo.address, activeJoinInfo.port,
						new Packet_JoinRequest
						{
							Username = activeJoinInfo.username,
							Password = activeJoinInfo.password,
							ResumePlayerId = playerId,
							ResumeToken = resumeToken
						});
					resumeToken = response.ResumeToken;
					client.RequestWorldSnapshot();
					Debug.Log("[MP-RECOVERY] TCP restored for player=" + response.PlayerId);
					if (MsgDrawer.main != null) MsgDrawer.main.Log("TCP session restored.");
					return;
				}
				catch (Exception ex)
				{
					Debug.LogWarning("[MP-RECOVERY] attempt " + (attempt + 1) + " failed: " + ex.Message);
					await Task.Delay(1000);
				}
			}
			HandleDisconnect("TCP recovery timed out.");
		}
		finally
		{
			recoveryRunning = false;
		}
	}

	private static void HandleDisconnect(string reason)
	{
		if (disconnectionHandled) return;
		disconnectionHandled = true;
		networkReady = false;
		string text = string.IsNullOrEmpty(reason) ? "Disconnected from server." : reason;
		Debug.LogWarning("TCP disconnected: " + text);
		if (MsgDrawer.main != null) MsgDrawer.main.Log(text);
		client?.Disconnect(text);
		multiplayerEnabled.Value = false;
		if (GameManager.main != null || WorldTime.main != null)
		{
			SceneLoader.ExitToMainMenu();
		}
	}

	public static void Disconnect(string reason)
	{
		connecting = false;
		networkReady = false;
		disconnectionHandled = true;
		client?.Disconnect(reason);
	}

	public static void HandlePacket(NetIncomingMessage msg)
	{
		PacketType packetType = (PacketType)((NetBuffer)msg).ReadByte();
		if (client != null) client.LastPacketType = packetType.ToString();
		if (Packet.ShouldDebug(packetType))
		{
			Debug.Log($"Recieved packet of type {packetType}.");
		}
		switch (packetType)
		{
		case PacketType.PlayerConnected:
			OnPacket_PlayerConnected(msg);
			break;
		case PacketType.PlayerDisconnected:
			OnPacket_PlayerDisconnected(msg);
			break;
		case PacketType.UpdatePlayerControl:
			OnPacket_UpdatePlayerControl(msg);
			break;
		case PacketType.UpdatePlayerAuthority:
			OnPacket_UpdatePlayerAuthority(msg);
			break;
		case PacketType.UpdateWorldTime:
			OnPacket_UpdateWorldTime(msg);
			break;
		case PacketType.UpdatePlayerColor:
			OnPacket_UpdatePlayerColor(msg);
			break;
		case PacketType.SendChatMessage:
			OnPacket_SendChatMessage(msg);
			break;
		case PacketType.ShowToastMessage:
			OnPacket_ShowToastMessage(msg);
			break;
		case PacketType.UpdateCheatStatus:
			OnPacket_UpdateCheatStatus(msg);
			break;
		case PacketType.CreateRocket:
			OnPacket_CreateRocket(msg);
			break;
		case PacketType.DestroyRocket:
			OnPacket_DestroyRocket(msg);
			break;
		case PacketType.UpdateRocketPrimary:
			OnPacket_UpdateRocketPrimary(msg);
			break;
		case PacketType.UpdateRocketSecondary:
			OnPacket_UpdateRocketSecondary(msg);
			break;
		case PacketType.DestroyPart:
			OnPacket_DestroyPart(msg);
			break;
		case PacketType.UpdateStaging:
			OnPacket_UpdateStaging(msg);
			break;
		case PacketType.UpdatePart_EngineModule:
			OnPacket_UpdatePart_EngineModule(msg);
			break;
		case PacketType.UpdatePart_WheelModule:
			OnPacket_UpdatePart_WheelModule(msg);
			break;
		case PacketType.UpdatePart_BoosterModule:
			OnPacket_UpdatePart_BoosterModule(msg);
			break;
		case PacketType.UpdatePart_ParachuteModule:
			OnPacket_UpdatePart_ParachuteModule(msg);
			break;
		case PacketType.UpdatePart_MoveModule:
			OnPacket_UpdatePart_MoveModule(msg);
			break;
		case PacketType.UpdatePart_ResourceModule:
			OnPacket_UpdatePart_ResourceModule(msg);
			break;
		case PacketType.DockTransaction:
			OnPacket_DockTransaction(msg);
			break;
		case PacketType.TimeWarp:
			OnPacket_TimeWarp(msg);
			break;
		case PacketType.JoinResponse:
			Debug.LogWarning("Recieved server info packet outside of connection attempt.");
			break;
		case PacketType.JoinRequest:
			Debug.LogWarning($"Recieved packet (of type {packetType}) intended for the server.");
			break;
		default:
			Debug.LogWarning($"Unhandled packet type ({packetType})!");
			break;
		}
	}

	public static void SendPacket(Packet packet, NetDeliveryMethod method = (NetDeliveryMethod)67)
	{
		if (packet == null || client == null || !client.Connected) return;
		if (Packet.ShouldDebug(packet.Type))
		{
			Debug.Log($"Sending TCP packet of type {packet.Type}.");
		}
		client.Send(packet);
	}

	public static void RequestTimeScale(double multiplier)
	{
		if (!multiplayerEnabled.Value || world == null) return;
		SendPacket(new Packet_TimeWarp
		{
			Operation = TimeWarpOperation.Request,
			Multiplier = multiplier
		});
	}

	private static void OnPacket_TimeWarp(NetIncomingMessage msg)
	{
		Packet_TimeWarp packet = msg.Read<Packet_TimeWarp>();
		if (packet.Operation != TimeWarpOperation.Applied) return;
		ApplyTimeScale(packet.Multiplier, packet.WorldTime);
	}

	private static void ApplyTimeScale(double multiplier, double worldTime)
	{
		if (client != null) worldTime += client.RoundTripMs / 2000.0 * multiplier;
		if (world != null) world.SetTimeScale(multiplier, worldTime);
		if (WorldTime.main != null)
		{
			WorldTime.main.worldTime = worldTime;
			WorldTime.main.SetState(multiplier, multiplier <= 5, false);
		}
	}

	private static void OnPacket_PlayerConnected(NetIncomingMessage msg)
	{
		Packet_PlayerConnected packet_PlayerConnected = msg.Read<Packet_PlayerConnected>();
		if (LocalManager.players.ContainsKey(packet_PlayerConnected.PlayerId))
		{
			LocalManager.players[packet_PlayerConnected.PlayerId] = new LocalPlayer(packet_PlayerConnected.Username, packet_PlayerConnected.IconColor);
		}
		else
		{
			LocalManager.players.Add(packet_PlayerConnected.PlayerId, new LocalPlayer(packet_PlayerConnected.Username, packet_PlayerConnected.IconColor));
		}
		if (packet_PlayerConnected.PrintMessage)
		{
			string text = packet_PlayerConnected.Username + " connected";
			MsgDrawer.main.Log(text);
			// ChatWindow.AddMessage(new ChatMessage(text));
		}
	}

	private static void OnPacket_PlayerDisconnected(NetIncomingMessage msg)
	{
		Packet_PlayerDisconnected packet_PlayerDisconnected = msg.Read<Packet_PlayerDisconnected>();
		if (LocalManager.players.TryGetValue(packet_PlayerDisconnected.PlayerId, out var value))
		{
			string text = value.username + " disconnected";
			MsgDrawer.main.Log(text);
			// ChatWindow.AddMessage(new ChatMessage(text));
			LocalManager.players.Remove(packet_PlayerDisconnected.PlayerId);
		}
	}

	private static void OnPacket_UpdatePlayerControl(NetIncomingMessage msg)
	{
		Packet_UpdatePlayerControl packet_UpdatePlayerControl = msg.Read<Packet_UpdatePlayerControl>();
		if (LocalManager.players.TryGetValue(packet_UpdatePlayerControl.PlayerId, out var value))
		{
			value.controlledRocket.Value = packet_UpdatePlayerControl.RocketId;
		}
		else
		{
			Debug.LogError("Missing player while trying to update controlled rocket!");
		}
	}

	private static void OnPacket_UpdatePlayerAuthority(NetIncomingMessage msg)
	{
		LocalManager.updateAuthority = msg.Read<Packet_UpdatePlayerAuthority>().RocketIds;
		foreach (int item in LocalManager.updateAuthority)
		{
			LocalManager.syncedRockets.TryGetValue(item, out var _);
		}
	}

	private static void OnPacket_UpdateWorldTime(NetIncomingMessage msg)
	{
		Packet_UpdateWorldTime packet_UpdateWorldTime = msg.Read<Packet_UpdateWorldTime>();
		if (WorldTime.main != null)
		{
			WorldTime main = WorldTime.main;
			double worldTime = (world.WorldTime = packet_UpdateWorldTime.WorldTime);
			main.worldTime = worldTime;
		}
	}

	private static void OnPacket_UpdatePlayerColor(NetIncomingMessage msg)
	{
		Packet_UpdatePlayerColor packet_UpdatePlayerColor = msg.Read<Packet_UpdatePlayerColor>();
		if (LocalManager.players.TryGetValue(packet_UpdatePlayerColor.PlayerId, out var value))
		{
			value.iconColor = packet_UpdatePlayerColor.Color;
			// ChatWindow.OnPlayerColorChange(packet_UpdatePlayerColor.PlayerId, packet_UpdatePlayerColor.Color);
		}
	}

	private static void OnPacket_SendChatMessage(NetIncomingMessage msg)
	{
		Packet_SendChatMessage packet_SendChatMessage = msg.Read<Packet_SendChatMessage>();
		// ChatWindow.AddMessage(new ChatMessage(packet_SendChatMessage.Message, packet_SendChatMessage.SenderId, packet_SendChatMessage.Color));
	}

	private static void OnPacket_ShowToastMessage(NetIncomingMessage msg)
	{
		ToastHelper.ShowToast(msg.Read<Packet_ShowToastMessage>().Message);
	}

	private static void OnPacket_UpdateCheatStatus(NetIncomingMessage msg)
	{
		Packet_UpdateCheatStatus packet_UpdateCheatStatus = msg.Read<Packet_UpdateCheatStatus>();
		if (SandboxSettings.main != null)
		{
			SandboxSettings.main.settings.infiniteFuel = packet_UpdateCheatStatus.InfiniteFuel;
			SandboxSettings.main.settings.noAtmosphericDrag = packet_UpdateCheatStatus.NoAtmosphericDrag;
			SandboxSettings.main.settings.unbreakableParts = packet_UpdateCheatStatus.UnbreakableParts;
			SandboxSettings.main.settings.noGravity = packet_UpdateCheatStatus.NoGravity;
			SandboxSettings.main.settings.noHeatDamage = packet_UpdateCheatStatus.NoHeatDamage;
			SandboxSettings.main.settings.noBurnMarks = packet_UpdateCheatStatus.NoBurnMarks;
			SandboxSettings.main.settings.infiniteBuildArea = packet_UpdateCheatStatus.InfiniteBuildArea;
			SandboxSettings.main.settings.partClipping = packet_UpdateCheatStatus.PartClipping;
			SandboxSettings.main.UpdateUI(instantAnimation: false);
			Debug.Log($"Cheat status updated: InfiniteFuel={packet_UpdateCheatStatus.InfiniteFuel}, NoAtmosphericDrag={packet_UpdateCheatStatus.NoAtmosphericDrag}");
		}
	}

	private static void OnPacket_CreateRocket(NetIncomingMessage msg)
	{
		Packet_CreateRocket packet_CreateRocket = msg.Read<Packet_CreateRocket>();
		world.rockets[packet_CreateRocket.GlobalId] = packet_CreateRocket.Rocket;
		LocalManager.OnPacket_CreateRocket(packet_CreateRocket);
	}

	private static void OnPacket_DestroyRocket(NetIncomingMessage msg)
	{
		Packet_DestroyRocket packet_DestroyRocket = msg.Read<Packet_DestroyRocket>();
		world.rockets.Remove(packet_DestroyRocket.RocketId);
		LocalManager.TrueDestructionReason = packet_DestroyRocket.Reason;
		LocalManager.DestroyLocalRocket(packet_DestroyRocket.RocketId);
	}

	private static void OnPacket_DockTransaction(NetIncomingMessage msg)
	{
		Packet_DockTransaction packet = msg.Read<Packet_DockTransaction>();
		if (!packet.Committed || packet.MergedRocket == null) return;

		bool localControlled = LocalManager.Player != null &&
			((int)LocalManager.Player.controlledRocket == packet.KeepRocketId ||
			 (int)LocalManager.Player.controlledRocket == packet.RemoveRocketId);
		world.rockets.Remove(packet.RemoveRocketId);
		world.rockets[packet.KeepRocketId] = packet.MergedRocket;
		LocalManager.DestroyLocalRocket(packet.KeepRocketId);
		LocalManager.DestroyLocalRocket(packet.RemoveRocketId);
		LocalRocket merged = LocalManager.SpawnLocalRocket(packet.MergedRocket);
		LocalManager.syncedRockets[packet.KeepRocketId] = merged;
		LocalManager.updateAuthority.Remove(packet.RemoveRocketId);
		if (packet.SecondRocket != null && packet.SecondRocketId != -1)
		{
			world.rockets[packet.SecondRocketId] = packet.SecondRocket;
			LocalManager.DestroyLocalRocket(packet.SecondRocketId);
			LocalManager.syncedRockets[packet.SecondRocketId] = LocalManager.SpawnLocalRocket(packet.SecondRocket);
		}
		if (localControlled)
		{
			LocalManager.Player.controlledRocket.Value = -1;
			if (PlayerController.main != null) PlayerController.main.player.Value = null;
		}
		Debug.Log("Server committed docking transaction " + packet.TransactionId +
			" into rocket " + packet.KeepRocketId + ".");
	}

	private static void OnPacket_UpdateRocketPrimary(NetIncomingMessage msg)
	{
		Packet_UpdateRocketPrimary packet_UpdateRocketPrimary = msg.Read<Packet_UpdateRocketPrimary>();
		if (world.rockets.TryGetValue(packet_UpdateRocketPrimary.RocketId, out var value))
		{
			value.UpdateRocketPrimary(packet_UpdateRocketPrimary);
			Interpolator.AddPacketToQueue(packet_UpdateRocketPrimary, packet_UpdateRocketPrimary.RocketId, packet_UpdateRocketPrimary.WorldTime);
		}
		else
		{
			Debug.LogWarning("Missing rocket from world state; requesting TCP snapshot for " + packet_UpdateRocketPrimary.RocketId + ".");
			client?.RequestRocketSnapshot(packet_UpdateRocketPrimary.RocketId);
		}
	}

	private static void OnPacket_UpdateRocketSecondary(NetIncomingMessage msg)
	{
		Packet_UpdateRocketSecondary packet_UpdateRocketSecondary = msg.Read<Packet_UpdateRocketSecondary>();
		if (world.rockets.TryGetValue(packet_UpdateRocketSecondary.RocketId, out var value))
		{
			value.UpdateRocketSecondary(packet_UpdateRocketSecondary);
			Interpolator.AddPacketToQueue(packet_UpdateRocketSecondary, packet_UpdateRocketSecondary.RocketId, packet_UpdateRocketSecondary.WorldTime);
		}
	}

	private static void OnPacket_DestroyPart(NetIncomingMessage msg)
	{
		Packet_DestroyPart packet_DestroyPart = msg.Read<Packet_DestroyPart>();
		if (world.rockets.TryGetValue(packet_DestroyPart.RocketId, out var value))
		{
			value.RemovePart(packet_DestroyPart.PartId);
			Interpolator.AddPacketToQueue(packet_DestroyPart, ClientPacketRouting.GetRocketId(packet_DestroyPart), packet_DestroyPart.WorldTime);
		}
	}

	private static void OnPacket_UpdateStaging(NetIncomingMessage msg)
	{
		Packet_UpdateStaging packet_UpdateStaging = msg.Read<Packet_UpdateStaging>();
		if (world.rockets.TryGetValue(packet_UpdateStaging.RocketId, out var value))
		{
			value.stages = packet_UpdateStaging.Stages;
			Interpolator.AddPacketToQueue(packet_UpdateStaging, packet_UpdateStaging.RocketId, packet_UpdateStaging.WorldTime);
		}
	}

	private static void OnPacket_UpdatePart_EngineModule(NetIncomingMessage msg)
	{
		Packet_UpdatePart_EngineModule packet_UpdatePart_EngineModule = msg.Read<Packet_UpdatePart_EngineModule>();
		if (world.rockets.TryGetValue(packet_UpdatePart_EngineModule.RocketId, out var value))
		{
			if (value.parts.TryGetValue(packet_UpdatePart_EngineModule.PartId, out var value2))
			{
				value2.part.TOGGLE_VARIABLES["engine_on"] = packet_UpdatePart_EngineModule.EngineOn;
			}
			Interpolator.AddPacketToQueue(packet_UpdatePart_EngineModule, packet_UpdatePart_EngineModule.RocketId, packet_UpdatePart_EngineModule.WorldTime);
		}
	}

	private static void OnPacket_UpdatePart_WheelModule(NetIncomingMessage msg)
	{
		Packet_UpdatePart_WheelModule packet_UpdatePart_WheelModule = msg.Read<Packet_UpdatePart_WheelModule>();
		if (world.rockets.TryGetValue(packet_UpdatePart_WheelModule.RocketId, out var value))
		{
			if (value.parts.TryGetValue(packet_UpdatePart_WheelModule.PartId, out var value2))
			{
				value2.part.TOGGLE_VARIABLES["wheel_on"] = packet_UpdatePart_WheelModule.WheelOn;
			}
			Interpolator.AddPacketToQueue(packet_UpdatePart_WheelModule, packet_UpdatePart_WheelModule.RocketId, packet_UpdatePart_WheelModule.WorldTime);
		}
	}

	private static void OnPacket_UpdatePart_BoosterModule(NetIncomingMessage msg)
	{
		Packet_UpdatePart_BoosterModule packet_UpdatePart_BoosterModule = msg.Read<Packet_UpdatePart_BoosterModule>();
		if (world.rockets.TryGetValue(packet_UpdatePart_BoosterModule.RocketId, out var value))
		{
			if (value.parts.TryGetValue(packet_UpdatePart_BoosterModule.PartId, out var value2))
			{
				value2.part.NUMBER_VARIABLES["fuel_percent"] = packet_UpdatePart_BoosterModule.FuelPercent;
			}
			Interpolator.AddPacketToQueue(packet_UpdatePart_BoosterModule, packet_UpdatePart_BoosterModule.RocketId, packet_UpdatePart_BoosterModule.WorldTime);
		}
	}

	private static void OnPacket_UpdatePart_ParachuteModule(NetIncomingMessage msg)
	{
		Packet_UpdatePart_ParachuteModule packet_UpdatePart_ParachuteModule = msg.Read<Packet_UpdatePart_ParachuteModule>();
		if (world.rockets.TryGetValue(packet_UpdatePart_ParachuteModule.RocketId, out var value))
		{
			if (value.parts.TryGetValue(packet_UpdatePart_ParachuteModule.PartId, out var value2))
			{
				value2.part.NUMBER_VARIABLES["animation_state"] = packet_UpdatePart_ParachuteModule.State;
				value2.part.NUMBER_VARIABLES["deploy_state"] = packet_UpdatePart_ParachuteModule.TargetState;
			}
			Interpolator.AddPacketToQueue(packet_UpdatePart_ParachuteModule, packet_UpdatePart_ParachuteModule.RocketId, packet_UpdatePart_ParachuteModule.WorldTime);
		}
	}

	private static void OnPacket_UpdatePart_MoveModule(NetIncomingMessage msg)
	{
		Packet_UpdatePart_MoveModule packet_UpdatePart_MoveModule = msg.Read<Packet_UpdatePart_MoveModule>();
		if (world.rockets.TryGetValue(packet_UpdatePart_MoveModule.RocketId, out var value))
		{
			if (value.parts.TryGetValue(packet_UpdatePart_MoveModule.PartId, out var value2))
			{
				value2.part.NUMBER_VARIABLES["state"] = packet_UpdatePart_MoveModule.Time;
				value2.part.NUMBER_VARIABLES["state_target"] = packet_UpdatePart_MoveModule.TargetTime;
			}
			Interpolator.AddPacketToQueue(packet_UpdatePart_MoveModule, packet_UpdatePart_MoveModule.RocketId, packet_UpdatePart_MoveModule.WorldTime);
		}
	}

	private static void OnPacket_UpdatePart_ResourceModule(NetIncomingMessage msg)
	{
		Packet_UpdatePart_ResourceModule packet_UpdatePart_ResourceModule = msg.Read<Packet_UpdatePart_ResourceModule>();
		if (!world.rockets.TryGetValue(packet_UpdatePart_ResourceModule.RocketId, out var value))
		{
			return;
		}
		foreach (int partId in packet_UpdatePart_ResourceModule.PartIds)
		{
			if (value.parts.TryGetValue(partId, out var value2))
			{
				value2.part.NUMBER_VARIABLES["fuel_percent"] = packet_UpdatePart_ResourceModule.ResourcePercent;
			}
		}
		Interpolator.AddPacketToQueue(packet_UpdatePart_ResourceModule, packet_UpdatePart_ResourceModule.RocketId, packet_UpdatePart_ResourceModule.WorldTime);
	}
}
