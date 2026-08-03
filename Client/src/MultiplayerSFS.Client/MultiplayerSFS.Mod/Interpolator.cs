using System;
using System.Collections.Generic;
using MultiplayerSFS.Common;
using SFS.Parts;
using SFS.Parts.Modules;
using SFS.World;
using UnityEngine;

namespace MultiplayerSFS.Mod;

public class Interpolator : MonoBehaviour
{
	public enum InterpolationMode
	{
		Hermite,
		Linear,
		Spherical
	}

	public const int MaxBuffer = 10;

	public const int MinBufferForInterpolation = 2;

	public LocalRocket rocket;

	public Packet_UpdateRocketPrimary currentUpdate;

	public bool isNewlyCreated;

	public List<Packet_UpdateRocketPrimary> updateBuffer = new List<Packet_UpdateRocketPrimary>();

	public List<(double, Packet)> packetBuffer = new List<(double, Packet)>();

	public InterpolationMode interpolationMode;

	private double lastSnapshotRequestWorldTime = double.NegativeInfinity;

	public static NetworkAdaptiveProfile AdaptiveProfile => ClientManager.client?.AdaptiveProfile ?? NetworkAdaptationPolicy.Evaluate(0, 0, 0);

	public static double TimeDelay => AdaptiveProfile.InterpolationDelaySeconds;

	public static double DelayedWorldTime => ClientManager.world.WorldTime - TimeDelay;

	public static bool IsPacketDue(double packetTime, double delayedWorldTime)
	{
		return packetTime <= delayedWorldTime;
	}

	public static double GetInterpolationFraction(double previousTime, double nextTime, double targetTime)
	{
		double duration = nextTime - previousTime;
		if (duration <= 0.0)
		{
			return targetTime >= nextTime ? 1.0 : 0.0;
		}

		double value = (targetTime - previousTime) / duration;
		return Math.Max(0.0, Math.Min(1.0, value));
	}

	public static void AddPacketToQueue(Packet packet, int rocketId, double worldTime)
	{
		if (!LocalManager.syncedRockets.TryGetValue(rocketId, out var value))
		{
			return;
		}
		Interpolator interpolator = value.interpolator;
		if ((object)interpolator == null)
		{
			return;
		}
		if (interpolator.rocket == null)
		{
			interpolator.rocket = value;
			interpolator.currentUpdate = value.rocket.ToUpdatePacketPrimary(rocketId);
		}
		if (packet is Packet_UpdateRocketPrimary item)
		{
			if (interpolator.currentUpdate != null && item.WorldTime <= interpolator.currentUpdate.WorldTime) return;
			int existing = interpolator.updateBuffer.FindIndex(update => update.WorldTime == item.WorldTime);
			if (existing >= 0) interpolator.updateBuffer[existing] = item;
			else
			{
				int index = interpolator.updateBuffer.FindIndex(update => update.WorldTime > item.WorldTime);
				if (index < 0) interpolator.updateBuffer.Add(item);
				else interpolator.updateBuffer.Insert(index, item);
			}
			while (interpolator.updateBuffer.Count > MaxBuffer) interpolator.updateBuffer.RemoveAt(0);
		}
		else
		{
			interpolator.packetBuffer.Add((worldTime, packet));
		}
	}

	public static double GetCorrectionTarget(double position, double velocity)
	{
		return position;
	}

	public static float GetCorrectionRotation(float rotation, float angularVelocity)
	{
		return rotation;
	}

	private void Update()
	{
		if (currentUpdate == null)
		{
			return;
		}
		if (LocalManager.updateAuthority.Contains(currentUpdate.RocketId))
		{
			rocket.rocket.rb2d.bodyType = RigidbodyType2D.Dynamic;
			rocket.rocket.rb2d.interpolation = RigidbodyInterpolation2D.None;
			RunAllPackets();
			currentUpdate = rocket.rocket.ToUpdatePacketPrimary(currentUpdate.RocketId);
			return;
		}
		rocket.rocket.rb2d.bodyType = RigidbodyType2D.Kinematic;
		rocket.rocket.rb2d.interpolation = RigidbodyInterpolation2D.Extrapolate;
		if (isNewlyCreated)
		{
			if (updateBuffer.Count > 0)
			{
				isNewlyCreated = false;
			}
			return;
		}
		if (updateBuffer.Count < 1)
		{
			PredictState(currentUpdate);
		}
		else
		{
			while (updateBuffer.Count > 0)
			{
				Packet_UpdateRocketPrimary prev = currentUpdate;
				Packet_UpdateRocketPrimary packet_UpdateRocketPrimary = updateBuffer[0];
				if (DelayedWorldTime > packet_UpdateRocketPrimary.WorldTime)
				{
					currentUpdate = updateBuffer[0];
					updateBuffer.RemoveAt(0);
					continue;
				}
				InterpolatePackets(prev, packet_UpdateRocketPrimary);
				break;
			}
		}
		packetBuffer.RemoveAll(delegate((double time, Packet packet) tuple)
		{
			if (double.IsNaN(tuple.time))
			{
				Debug.LogError($"Interpolator Error: WorldTime of `{tuple.packet.Type}` packet has not been set!");
				return true;
			}
			if (IsPacketDue(tuple.time, DelayedWorldTime))
			{
				RunPacket(tuple.packet);
				return true;
			}
			return false;
		});
	}

	private void PredictState(Packet_UpdateRocketPrimary lastPacket)
	{
		double num = DelayedWorldTime - lastPacket.WorldTime;
		if (!(num <= 0.0))
		{
			double maximum = AdaptiveProfile.MaximumExtrapolationSeconds;
			if (num > maximum && ClientManager.world.WorldTime - lastSnapshotRequestWorldTime >= AdaptiveProfile.ValidationIntervalMilliseconds / 1000.0)
			{
				lastSnapshotRequestWorldTime = ClientManager.world.WorldTime;
				ClientManager.client?.RequestRocketSnapshot(lastPacket.RocketId);
			}
			num = Math.Min(num, maximum);
			Location location = lastPacket.Location.ToVanillaLocation();
			location.position += lastPacket.Location.velocity * num;
			location.velocity = lastPacket.Location.velocity;
			float rot = lastPacket.Rotation + lastPacket.AngularVelocity * (float)num;
			float angularVelocity = lastPacket.AngularVelocity;
			SetState(location, rot, angularVelocity);
		}
	}

	private void InterpolatePackets(Packet_UpdateRocketPrimary prev, Packet_UpdateRocketPrimary next)
	{
		if (prev.Location.address != next.Location.address)
		{
			SetState(next.Location.ToVanillaLocation(), next.Rotation, next.AngularVelocity);
			return;
		}
		double value = GetInterpolationFraction(prev.WorldTime, next.WorldTime, DelayedWorldTime);
		Location loc = prev.Location.ToVanillaLocation();
		float rot;
		float angVel;
		switch (interpolationMode)
		{
		case InterpolationMode.Hermite:
			HermiteInterpolation(prev, next, value, out loc, out rot, out angVel);
			break;
		case InterpolationMode.Linear:
			LinearInterpolation(prev, next, value, out loc, out rot, out angVel);
			break;
		case InterpolationMode.Spherical:
			SphericalInterpolation(prev, next, value, out loc, out rot, out angVel);
			break;
		default:
			HermiteInterpolation(prev, next, value, out loc, out rot, out angVel);
			break;
		}
		SetState(loc, rot, angVel);
	}

	private void HermiteInterpolation(Packet_UpdateRocketPrimary prev, Packet_UpdateRocketPrimary next, double t, out Location loc, out float rot, out float angVel)
	{
		double num = t * t;
		double num2 = num * t;
		Double2 position = prev.Location.position;
		Double2 velocity = prev.Location.velocity;
		Double2 position2 = next.Location.position;
		Double2 velocity2 = next.Location.velocity;
		double duration = Math.Max(0.0001, next.WorldTime - prev.WorldTime);
		double num3 = 2.0 * num2 + -3.0 * num + 1.0;
		double num4 = num2 + -2.0 * num + t;
		double num5 = -2.0 * num2 + 3.0 * num;
		double num6 = num2 - num;
		loc = prev.Location.ToVanillaLocation();
		loc.position = num3 * position + num4 * duration * velocity + num5 * position2 + num6 * duration * velocity2;
		loc.velocity = Double2.Lerp(velocity, velocity2, t);
		rot = Mathf.LerpAngle(prev.Rotation, next.Rotation, (float)t);
		angVel = Mathf.Lerp(prev.AngularVelocity, next.AngularVelocity, (float)t);
	}

	private void LinearInterpolation(Packet_UpdateRocketPrimary prev, Packet_UpdateRocketPrimary next, double t, out Location loc, out float rot, out float angVel)
	{
		loc = prev.Location.ToVanillaLocation();
		loc.position = Double2.Lerp(prev.Location.position, next.Location.position, t);
		loc.velocity = Double2.Lerp(prev.Location.velocity, next.Location.velocity, t);
		rot = Mathf.LerpAngle(prev.Rotation, next.Rotation, (float)t);
		angVel = Mathf.Lerp(prev.AngularVelocity, next.AngularVelocity, (float)t);
	}

	private void SphericalInterpolation(Packet_UpdateRocketPrimary prev, Packet_UpdateRocketPrimary next, double t, out Location loc, out float rot, out float angVel)
	{
		loc = prev.Location.ToVanillaLocation();
		double num = t * t * (3.0 - 2.0 * t);
		loc.position = Double2.Lerp(prev.Location.position, next.Location.position, num);
		loc.velocity = Double2.Lerp(prev.Location.velocity, next.Location.velocity, num);
		rot = Mathf.LerpAngle(prev.Rotation, next.Rotation, (float)num);
		angVel = Mathf.Lerp(prev.AngularVelocity, next.AngularVelocity, (float)num);
	}

	private static Location ClampBelowTerrain(Location loc)
	{
		if (loc == null || loc.planet == null || loc.position.sqrMagnitude <= 1E-10)
		{
			return loc;
		}
		double belowTerrain = loc.GetTerrainHeight(clampToWater: true);
		if (belowTerrain >= 0.0)
		{
			return loc;
		}
		loc.position += loc.position.normalized * -belowTerrain;
		return loc;
	}

	private void SetState(Location loc, float rot, float angVel)
	{
		loc = ClampBelowTerrain(loc);
		NetworkAdaptiveProfile profile = AdaptiveProfile;
		Location current = rocket.rocket.location.Value;
		if (current != null && current.planet == loc.planet)
		{
			double alpha = 1.0 - Math.Exp(-Math.Max(0.001, Time.unscaledDeltaTime) / profile.CorrectionSeconds);
			loc.position = Double2.Lerp(current.position, new Double2(GetCorrectionTarget(loc.position.x, loc.velocity.x), GetCorrectionTarget(loc.position.y, loc.velocity.y)), alpha);
			loc.velocity = Double2.Lerp(current.velocity, loc.velocity, alpha);
			rot = Mathf.LerpAngle(rocket.rocket.rb2d.transform.eulerAngles.z,
				GetCorrectionRotation(rot, angVel), (float)alpha);
			angVel = Mathf.Lerp(rocket.rocket.rb2d.angularVelocity, angVel, (float)alpha);
		}
		rocket.rocket.rb2d.transform.eulerAngles = new Vector3(0f, 0f, rot);
		rocket.rocket.rb2d.angularVelocity = angVel;
		if (rocket.rocket.physics.PhysicsMode)
		{
			((I_Physics)rocket.rocket).LocalPosition = WorldView.ToLocalPosition(loc.position);
			((I_Physics)rocket.rocket).LocalVelocity = WorldView.ToLocalVelocity(loc.velocity);
		}
		else
		{
			rocket.rocket.physics.SetLocationAndState(loc, physicsMode: false);
		}
	}

	private void RunAllPackets()
	{
		foreach (var item2 in packetBuffer)
		{
			Packet item = item2.Item2;
			RunPacket(item);
		}
		packetBuffer.Clear();
		Packet_UpdateRocketPrimary packet_UpdateRocketPrimary = null;
		while (updateBuffer.Count > 0)
		{
			packet_UpdateRocketPrimary = updateBuffer[0];
			updateBuffer.RemoveAt(0);
		}
		if (packet_UpdateRocketPrimary != null)
		{
			SetState(packet_UpdateRocketPrimary.Location.ToVanillaLocation(), packet_UpdateRocketPrimary.Rotation, packet_UpdateRocketPrimary.AngularVelocity);
		}
	}

	private void RunPacket(Packet packet)
	{
		switch (packet.Type)
		{
		case PacketType.UpdateRocketSecondary:
			OnPacket_UpdateRocketSecondary(packet as Packet_UpdateRocketSecondary);
			break;
		case PacketType.DestroyPart:
			OnPacket_DestroyPart(packet as Packet_DestroyPart);
			break;
		case PacketType.UpdateStaging:
			OnPacket_UpdateStaging(packet as Packet_UpdateStaging);
			break;
		case PacketType.UpdatePart_EngineModule:
			OnPacket_UpdatePart_EngineModule(packet as Packet_UpdatePart_EngineModule);
			break;
		case PacketType.UpdatePart_WheelModule:
			OnPacket_UpdatePart_WheelModule(packet as Packet_UpdatePart_WheelModule);
			break;
		case PacketType.UpdatePart_BoosterModule:
			OnPacket_UpdatePart_BoosterModule(packet as Packet_UpdatePart_BoosterModule);
			break;
		case PacketType.UpdatePart_ParachuteModule:
			OnPacket_UpdatePart_ParachuteModule(packet as Packet_UpdatePart_ParachuteModule);
			break;
		case PacketType.UpdatePart_MoveModule:
			OnPacket_UpdatePart_MoveModule(packet as Packet_UpdatePart_MoveModule);
			break;
		case PacketType.UpdatePart_ResourceModule:
			OnPacket_UpdatePart_ResourceModule(packet as Packet_UpdatePart_ResourceModule);
			break;
		default:
			Debug.LogError($"Invalid packet type used in interpolator: {packet.Type}");
			break;
		}
	}

	private void OnPacket_UpdateRocketSecondary(Packet_UpdateRocketSecondary packet)
	{
		Arrowkeys arrowkeys = rocket.rocket.arrowkeys;
		arrowkeys.turnAxis.Value = packet.Input_Turn;
		arrowkeys.rawArrowkeysAxis.Value = packet.Input_Raw;
		arrowkeys.horizontalAxis.Value = packet.Input_Horizontal;
		arrowkeys.verticalAxis.Value = packet.Input_Vertical;
		arrowkeys.rcs.Value = packet.RCS;
		rocket.rocket.throttle.throttlePercent.Value = packet.ThrottlePercent;
		rocket.rocket.throttle.throttleOn.Value = packet.ThrottleOn;
	}

	private void OnPacket_DestroyPart(Packet_DestroyPart packet)
	{
		if (rocket.parts.TryGetValue(packet.PartId, out var value) && value != null)
		{
			LocalManager.TrueDestructionReason = packet.Reason;
			value.DestroyPart(packet.CreateExplosion, updateJoints: true, (DestructionReason)4);
		}
	}

	private void OnPacket_UpdateStaging(Packet_UpdateStaging packet)
	{
		rocket.rocket.staging.ClearStages(record: false);
		foreach (StageState stage in packet.Stages)
		{
			List<Part> list = new List<Part>();
			foreach (int partID in stage.partIDs)
			{
				list.Add(rocket.parts[partID]);
			}
			rocket.rocket.staging.InsertStage(new Stage(stage.stageID, list), record: false);
		}
	}

	private void OnPacket_UpdatePart_EngineModule(Packet_UpdatePart_EngineModule packet)
	{
		if (rocket.parts.TryGetValue(packet.PartId, out var value))
		{
			EngineModule[] modules = value.GetModules<EngineModule>();
			if (modules.Length > 1)
			{
				Debug.LogWarning("OnPacket_UpdatePart_EngineModule: Found multiple engine modules on part \"" + value.Name + "\".");
			}
			modules[0].engineOn.Value = packet.EngineOn;
		}
	}

	private void OnPacket_UpdatePart_WheelModule(Packet_UpdatePart_WheelModule packet)
	{
		if (rocket.parts.TryGetValue(packet.PartId, out var value))
		{
			WheelModule[] modules = value.GetModules<WheelModule>();
			if (modules.Length > 1)
			{
				Debug.LogWarning("OnPacket_UpdatePart_WheelModule: Found multiple wheel modules on part \"" + value.Name + "\".");
			}
			modules[0].on.Value = packet.WheelOn;
		}
	}

	private void OnPacket_UpdatePart_BoosterModule(Packet_UpdatePart_BoosterModule packet)
	{
		if (rocket.parts.TryGetValue(packet.PartId, out var value))
		{
			BoosterModule[] modules = value.GetModules<BoosterModule>();
			if (modules.Length > 1)
			{
				Debug.LogWarning("OnPacket_UpdatePart_BoosterModule: Found multiple booster modules on part \"" + value.Name + "\".");
			}
			modules[0].boosterPrimed.Value = packet.Primed;
			modules[0].throttle_Out.Value = packet.Throttle;
			modules[0].fuelPercent.Value = packet.FuelPercent;
		}
	}

	private void OnPacket_UpdatePart_ParachuteModule(Packet_UpdatePart_ParachuteModule packet)
	{
		if (rocket.parts.TryGetValue(packet.PartId, out var value))
		{
			ParachuteModule[] modules = value.GetModules<ParachuteModule>();
			if (modules.Length > 1)
			{
				Debug.LogWarning("OnPacket_UpdatePart_ParachuteModule: Found multiple parachute modules on part \"" + value.Name + "\".");
			}
			modules[0].state.Value = packet.State;
			modules[0].targetState.Value = packet.TargetState;
		}
	}

	private void OnPacket_UpdatePart_MoveModule(Packet_UpdatePart_MoveModule packet)
	{
		if (rocket.parts.TryGetValue(packet.PartId, out var value))
		{
			MoveModule[] modules = value.GetModules<MoveModule>();
			if (modules.Length > 1)
			{
				Debug.LogWarning("OnPacket_UpdatePart_MoveModule: Found multiple move modules on part \"" + value.Name + "\".");
			}
			modules[0].time.Value = packet.Time;
			modules[0].targetTime.Value = packet.TargetTime;
		}
	}

	private void OnPacket_UpdatePart_ResourceModule(Packet_UpdatePart_ResourceModule packet)
	{
		foreach (int partId in packet.PartIds)
		{
			if (rocket.parts.TryGetValue(partId, out var value))
			{
				ResourceModule[] modules = value.GetModules<ResourceModule>();
				if (modules.Length > 1)
				{
					Debug.LogWarning("OnPacket_UpdatePart_ResourceModule: Found multiple resource modules on part \"" + value.Name + "\".");
				}
				modules[0].resourcePercent.Value = packet.ResourcePercent;
			}
		}
	}
}
