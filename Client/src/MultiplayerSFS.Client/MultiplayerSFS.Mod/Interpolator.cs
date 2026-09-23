// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using MultiplayerSFS.Common;
using SFS.Parts;
using SFS.Parts.Modules;
using SFS.UI;
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

	// 数据断流后是否已交回本地物理引擎（见 PredictState）：为 true 时不写 transform，由游戏自己积分
	public bool freeSimulation;

	// 远端火箭位置偏差超过这个距离就直接硬对齐（不走平滑），同时立刻向服务端要一次该火箭的
	// 权威快照。10 米仍远大于正常插值误差（<1 米），但能挡住"断流期间本地引擎按轨道推进"造成的
	// 偏差堆积（实测几百米），不会等它涨到几十米才处理。
	//
	// 【2026-09-22 回滚记录】曾把门限改成 max(10, v×τ×3) 想把"平滑必然产生的 v×τ 滞后"排除掉，
	// 结果：闪烁一点没好（说明闪烁不来自这里），反而让真实漂移没人管，
	// 把原本只在 P2P 出现的"双方位置不一致"复现到了中继路径上。别再照那个思路改。
	private const double GrossPositionErrorMeters = 10.0;
	private float nextDriftCorrectionLogTime;
	// 【临时诊断】每枚远端火箭每 0.5 秒打一行同步状态，用来定位"高速时一闪一闪"到底来自哪一层。
	// 查清后删掉。文案必须英文：游戏内控制台没有中文字形，中文会渲染成方框。
	private const double SyncProbeIntervalSeconds = 0.5;
	private double nextSyncProbeTime;

	// ——— 预测式同步（re-seed 形态，V1.2.3.20）———
	// 远端火箭不再"每帧被钉在滞后的插值曲线上"：每收到一个权威包就把 (位置,速度,角度,角速度)
	// 整份写回（走游戏物理 API），包间隔（20~50ms）内由 Unity 外推补足。
	// 相比旧插值路径，渲染延迟从 0.12~0.48s 降到 0.06s → "飘"（600m/s 时 70~290m）大幅减小。
	// 回退开关：改成 false 即完全回到插值路径。
	private const bool PredictiveReconcile = true;
	// 渲染延迟：吸收"包到达抖动"。这是抖动缓冲区（jitter buffer）的经典取值——**半个发包间隔**：
	// 太贴近"当下"（延迟小）→ 网络一抖就露；太靠后（延迟大）→ 又回到"飘"。
	// 所以不再写死：按实测包间隔自适应（见 RenderDelaySeconds），只钳上下限。
	private const double MinRenderDelaySeconds = 0.05;
	private const double MaxRenderDelaySeconds = 0.25;
	// 实测包间隔（指数平滑），初值取常见发包间隔 200ms
	private double smoothedPacketIntervalSeconds = 0.2;
	private double lastPrimaryPacketWorldTime;
	private double RenderDelaySeconds =>
		Math.Max(MinRenderDelaySeconds, Math.Min(MaxRenderDelaySeconds, smoothedPacketIntervalSeconds * 0.5));
	// 超过它就认定本地与权威端已脱节（长断流），记一行日志（写回本身照做）
	private const double HardResyncMeters = 200.0;

	public List<Packet_UpdateRocketPrimary> updateBuffer = new List<Packet_UpdateRocketPrimary>();

	public List<(double, Packet)> packetBuffer = new List<(double, Packet)>();

	public InterpolationMode interpolationMode;

	private double lastSnapshotRequestWorldTime = double.NegativeInfinity;
	private bool authorityStateKnown;
	private bool wasAuthority;

	// 客户端未连接时的兜底档位：NetworkAdaptationPolicy.Evaluate 每次调用都会 new 一个
	// NetworkAdaptiveProfile 对象，而 AdaptiveProfile 在插值路径上是【每帧 × 每枚远端火箭】被读取
	// （SetState / DelayedWorldTime / IsPacketDue），每次都新建 + 三次锁 —— 纯垃圾。
	// 兜底档位是常量，缓存一份即可。
	private static readonly NetworkAdaptiveProfile fallbackAdaptiveProfile = NetworkAdaptationPolicy.Evaluate(0, 0, 0);

	public static NetworkAdaptiveProfile AdaptiveProfile => ClientManager.client?.AdaptiveProfile ?? fallbackAdaptiveProfile;

	public static double TimeDelay => AdaptiveProfile.InterpolationDelaySeconds;

	public static double DelayedWorldTime => (ClientManager.world != null ? ClientManager.world.WorldTime : 0.0) - TimeDelay;

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
		if (interpolator.freeSimulation)
		{
			// 数据恢复：从"本地物理模拟"交回插值器接管（下一帧起按包插值重新对齐）
			interpolator.freeSimulation = false;
			// 诊断：记录本地模拟与恢复包之间的偏差。若以后再出现"火箭突然飞起来"，
			// 这条日志能区分是本地物理跑飞了（差值巨大），还是包本身就是异常值。
			if (packet is Packet_UpdateRocketPrimary resumed && interpolator.rocket != null
				&& interpolator.rocket.rocket != null)
			{
				Location local = interpolator.rocket.rocket.location.Value;
				double dx = local.position.x - resumed.Location.position.x;
				double dy = local.position.y - resumed.Location.position.y;
				Debug.Log($"[SFS-MP] 数据恢复: 火箭 {rocketId} 本地模拟与包位置相差 {Math.Sqrt(dx * dx + dy * dy):F1}m "
						  + $"(本地行星={local.planet.codeName} 包行星={resumed.Location.address})");
			}
		}
		if (packet is Packet_UpdateRocketPrimary item)
		{
			if (interpolator.currentUpdate != null && item.WorldTime == interpolator.currentUpdate.WorldTime) return;
			// 手写线性查找替代 updateBuffer.FindIndex(lambda)：FindIndex 每次调用都要分配
			// 一个闭包 + 委托（每个收到的包两次），而且缓冲区只有 ≤10 个元素，手写循环更快。
			int existing = -1;
			var buffer = interpolator.updateBuffer;
			for (int i = 0; i < buffer.Count; i++)
			{
				if (buffer[i].WorldTime == item.WorldTime)
				{
					existing = i;
					break;
				}
			}
			if (existing >= 0) buffer[existing] = item;
			else
			{
				int index = -1;
				for (int i = 0; i < buffer.Count; i++)
				{
					if (buffer[i].WorldTime > item.WorldTime)
					{
						index = i;
						break;
					}
				}
				if (index < 0) buffer.Add(item);
				else buffer.Insert(index, item);
			}
			while (buffer.Count > MaxBuffer) buffer.RemoveAt(0);
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

	public static bool ShouldPrimeAuthorityState(bool wasAuthority, bool isAuthority)
	{
		return !wasAuthority && isAuthority;
	}

	private void Update()
	{
		if (currentUpdate == null)
		{
			return;
		}
		// 单向冻结根因：若 updateAuthority 误含对方火箭，本端会把它当本地权威（Dynamic 物理体、无输入），导致完全不动。
		// 只有本地玩家真正控制的火箭才走权威路径；否则强制非权威插值，保证对方火箭在本端动起来。
		bool isLocalPlayerControlled = LocalManager.Player != null && LocalManager.Player.controlledRocket.Value == currentUpdate.RocketId;
		bool hosted = LocalManager.updateAuthority.Contains(currentUpdate.RocketId);
		// 本端托管、但没人在开这枚火箭（发射台上留下的 / 分离掉的 / 切走后留下的）：
		// 必须由本端游戏引擎推进（on-rails 轨道或物理），模组只负责把它的真实状态发出去。
		// 反例就在下面：一旦被设成 Kinematic + Extrapolate，而它又收不到任何远端包
		// （本端就是权威，没人会发它的状态过来），SetState 会一直 early-return，
		// 于是没有任何代码把它设回 Dynamic —— Unity 便按最后速度对它做匀速外推，
		// 表现为"无人操控的火箭按固定方向一直飞"；Kinematic 还会顶飞真实火箭。
		// 只在对端有包在流时才让出（那种情况说明权威集合误含了对方在操控的火箭，不能抢）。
		bool remoteDriven = ClientManager.world != null && ClientManager.world.WorldTime - currentUpdate.WorldTime < 0.5;
		if (hosted && !isLocalPlayerControlled && !remoteDriven)
		{
			if (rocket.rocket.rb2d.bodyType != RigidbodyType2D.Dynamic)
			{
				rocket.rocket.rb2d.bodyType = RigidbodyType2D.Dynamic;
			}
			if (rocket.rocket.rb2d.interpolation != RigidbodyInterpolation2D.None)
			{
				rocket.rocket.rb2d.interpolation = RigidbodyInterpolation2D.None;
			}
			freeSimulation = true;
			return;
		}
		bool isAuthority = hosted && isLocalPlayerControlled;
		bool gainedAuthority = !authorityStateKnown
			? isAuthority
			: ShouldPrimeAuthorityState(wasAuthority, isAuthority);
		authorityStateKnown = true;
		wasAuthority = isAuthority;
		if (isAuthority)
		{
			if (gainedAuthority) PrimeAuthorityState();
			rocket.rocket.rb2d.bodyType = RigidbodyType2D.Dynamic;
			rocket.rocket.rb2d.interpolation = RigidbodyInterpolation2D.None;
			RunAllPackets();
			currentUpdate = rocket.rocket.ToUpdatePacketPrimary(currentUpdate.RocketId);
			return;
		}
		if (PredictiveReconcile)
		{
			// 预测式同步：本地引擎推进 + 权威包软纠偏（见字段区注释）
			UpdatePredictivePrimary();
		}
		else
		{
			rocket.rocket.rb2d.bodyType = RigidbodyType2D.Kinematic;
			// 【勿改成 None】2026-09-22 实测：关掉 Extrapolate 后双方位置彻底不同步（"全乱套"）。
			// 说明本类写进去的位置不是每渲染帧直接落到 transform 的（要经游戏物理层），
			// Unity 这层外推是在补渲染帧与物理步之间的空隙，是承重的，不是重复积分。
			rocket.rocket.rb2d.interpolation = RigidbodyInterpolation2D.Extrapolate;
			if (isNewlyCreated)
			{
				if (updateBuffer.Count > 0)
				{
					isNewlyCreated = false;
					// 非权威端首包硬对齐：直接 snap 到第一个真实 primary 包的物理位置（不走平滑），
					// 消除从"创建包出生位置"慢慢追逐导致的初始错位（表现为需要碰撞一次才同步）。
					PrimeNonAuthorityState();
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
		}
		// 手写原地压缩替代 packetBuffer.RemoveAll(委托)：RemoveAll 每次调用都要分配一个闭包 + 委托
		//（本函数是【每帧 × 每枚远端火箭】），内部还要整表复制一遍。语义保持不变：
		// 按时间顺序执行到期包、剔除 WorldTime 未设置的包，未到期的保持相对顺序留在表里。
		int write = 0;
		for (int read = 0; read < packetBuffer.Count; read++)
		{
			(double time, Packet packet) tuple = packetBuffer[read];
			bool consumed;
			if (double.IsNaN(tuple.time))
			{
				Debug.LogError($"Interpolator Error: WorldTime of `{tuple.packet.Type}` packet has not been set!");
				consumed = true;
			}
			else if (IsPacketDue(tuple.time, DelayedWorldTime))
			{
				RunPacket(tuple.packet);
				consumed = true;
			}
			else
			{
				consumed = false;
			}
			if (!consumed)
			{
				packetBuffer[write++] = tuple;
			}
		}
		if (write < packetBuffer.Count)
		{
			packetBuffer.RemoveRange(write, packetBuffer.Count - write);
		}
	}

	private void PrimeAuthorityState()
	{
		Packet_UpdateRocketPrimary authoritative = currentUpdate;
		if (updateBuffer.Count > 0)
		{
			authoritative = updateBuffer[updateBuffer.Count - 1];
		}
		if (authoritative == null || rocket == null || rocket.rocket == null)
		{
			return;
		}
		ApplyStateImmediately(authoritative.Location.ToVanillaLocation(), authoritative.Rotation, authoritative.AngularVelocity);
	}

	// 非权威端首包硬对齐：把 currentUpdate 直接跳到缓冲区里第一个真实包（最早 WorldTime）并立即应用，
	// 使下一帧插值从真实位置起步，而不是从创建包出生位置慢慢平滑追逐（那是"需要碰撞一次才同步"的根因）。
	private void PrimeNonAuthorityState()
	{
		if (updateBuffer.Count == 0 || rocket == null || rocket.rocket == null)
		{
			return;
		}
		var first = updateBuffer[0];
		currentUpdate = first;
		updateBuffer.RemoveAt(0);
		ApplyStateImmediately(first.Location.ToVanillaLocation(), first.Rotation, first.AngularVelocity);
	}

	private void ApplyStateImmediately(Location loc, float rot, float angVel)
	{
		loc = ClampBelowTerrain(loc);
		if (loc == null) return;   // 地形未就绪：本帧不写位置/速度/角度，避免把未钳制的状态灌进物理体
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

	private void PredictState(Packet_UpdateRocketPrimary lastPacket)
	{
		// 本端持有这枚火箭的托管权（updateAuthority 含它）但不是本地玩家在操控它 ——
		// 例如发射台上留下的、分离掉的、切走后留下的那枚。这种情况绝不能做线性外推：
		// 外推把速度冻成常量，本端又一直在替它发 Primary 包，结果就是所有客户端都看到
		// 它"按固定方向一直飞"（也就是轨迹没有被真正传输）。正确做法是把轨迹交回本端
		// 游戏引擎（on-rails 轨道推进 / 物理），只把最后已知状态写一次；之后本端继续
		// 替它发 Primary 包，包里就是真实轨迹。
		if (rocket != null && rocket.rocket != null && rocket.rocket.location != null
			&& LocalManager.updateAuthority.Contains(lastPacket.RocketId)
			&& !(LocalManager.Player != null && LocalManager.Player.controlledRocket.Value == lastPacket.RocketId))
		{
			if (!freeSimulation)
			{
				freeSimulation = true;
				ApplyStateImmediately(lastPacket.Location.ToVanillaLocation(), lastPacket.Rotation, lastPacket.AngularVelocity);
				// 一次性诊断：证明轨迹已交回本端引擎（之后本端不再覆盖它，引擎按轨道/物理推进，
				// 发出去的 Primary 包里速度会随时间变化 —— 而不是恒定的直线速度）。
				Debug.Log($"[SFS-MP] 无操控的托管火箭交回本地引擎: id={lastPacket.RocketId} "
						+ $"planet={lastPacket.Location.address} "
						+ $"pos=({lastPacket.Location.position.x:F1},{lastPacket.Location.position.y:F1}) "
						+ $"vel=({lastPacket.Location.velocity.x:F1},{lastPacket.Location.velocity.y:F1})");
			}
			return;
		}
		double num = DelayedWorldTime - lastPacket.WorldTime;
		if (!(num <= 0.0))
		{
			double maximum = AdaptiveProfile.MaximumExtrapolationSeconds;
			if (num > maximum)
			{
				// 数据断流（超过外推上限）：把火箭交回【本地物理引擎】自己算。
				// 客户端的物理引擎才是真实的那一套（权威端本来也是本地物理在跑），
				// 断流时继续硬外推只会卡住/漂移。这里只把最后已知状态写一次（按上限外推到当下），
				// 之后不再每帧写 transform，让游戏自己积分；收到新包会清掉 freeSimulation 重新对齐。
				if (!freeSimulation && rocket != null)
				{
					Location loc = lastPacket.Location.ToVanillaLocation();
					loc.position += lastPacket.Location.velocity * maximum;
					loc.velocity = lastPacket.Location.velocity;
					float rot = lastPacket.Rotation + lastPacket.AngularVelocity * (float)maximum;
					// 必须用 ApplyStateImmediately：它带 ClampBelowTerrain（低于地形的坐标会被钳回来）。
					// 若用普通 SetState 把火箭写在地形里，本地物理一解冻就会被碰撞求解器猛地弹出，
					// 表现为"火箭莫名其妙突然飞起来"。
					ApplyStateImmediately(loc, rot, lastPacket.AngularVelocity);
					freeSimulation = true;
					Debug.Log($"[SFS-MP] 数据断流 {num:F2}s，交回本地物理: id={lastPacket.RocketId} "
							  + $"planet={lastPacket.Location.address} pos=({loc.position.x:F1},{loc.position.y:F1}) "
							  + $"vel=({loc.velocity.x:F1},{loc.velocity.y:F1}) angVel={lastPacket.AngularVelocity:F3}");
				}
				if (ClientManager.world.WorldTime - lastSnapshotRequestWorldTime >= AdaptiveProfile.ValidationIntervalMilliseconds / 1000.0)
				{
					lastSnapshotRequestWorldTime = ClientManager.world.WorldTime;
					ClientManager.client?.RequestRocketSnapshot(lastPacket.RocketId);
				}
				return;
			}
			num = Math.Min(num, maximum);
			Location location = lastPacket.Location.ToVanillaLocation();
			location.position += lastPacket.Location.velocity * num;
			location.velocity = lastPacket.Location.velocity;
			float rot2 = lastPacket.Rotation + lastPacket.AngularVelocity * (float)num;
			float angularVelocity = lastPacket.AngularVelocity;
			SetState(location, rot2, angularVelocity);
		}
	}

	// ——— 预测式同步实现 ———
	// 远端火箭由本地引擎推进，我们每帧只做两件事：
	//   1) 有新权威包 → 换算成"此刻应该在哪儿"，与本地状态求差，存成待纠偏量
	//   2) 有待纠偏量 → 按窗口吃掉一小口（指数衰减），全程不瞬移
	// 没有偏差时完全不碰物理体，引擎自己积分（滑行段与权威端同解）。
	private void UpdatePredictivePrimary()
	{
		if (rocket == null || rocket.rocket == null || rocket.rocket.rb2d == null) return;

		if (isNewlyCreated)
		{
			if (updateBuffer.Count == 0) return;
			isNewlyCreated = false;
			PrimeNonAuthorityState();   // 首包硬对齐一次：从出生位置直接落到真实位置
		}

		// 【顺序很重要】先消费新包，再判断断流。
		// V1.2.3.20 把顺序写反了：断流判断在消费之前 return，于是首次误判后就永远吃不到包、
		// currentUpdate 永远停在旧值（日志实证：packet stall 1071698.8s = 世界时间 - 0），
		// 表现为远端火箭"彻底不动"。
		if (updateBuffer.Count > 0)
		{
			Packet_UpdateRocketPrimary newest = updateBuffer[updateBuffer.Count - 1];
			updateBuffer.Clear();
			// 用相邻包的 WorldTime 差估发包间隔 → 自适应的抖动缓冲区长度（见 RenderDelaySeconds）
			if (lastPrimaryPacketWorldTime > 0.0 && newest.WorldTime > lastPrimaryPacketWorldTime)
			{
				double interval = newest.WorldTime - lastPrimaryPacketWorldTime;
				if (interval < 2.0)
				{
					smoothedPacketIntervalSeconds = smoothedPacketIntervalSeconds * 0.7 + interval * 0.3;
				}
			}
			if (newest.WorldTime > 0.0) lastPrimaryPacketWorldTime = newest.WorldTime;
			currentUpdate = newest;
			freeSimulation = false;
			// 从断流恢复时刚体可能还停在 Dynamic，这里改回 Kinematic + Extrapolate
			if (rocket.rocket.rb2d.bodyType != RigidbodyType2D.Kinematic)
				rocket.rocket.rb2d.bodyType = RigidbodyType2D.Kinematic;
			if (rocket.rocket.rb2d.interpolation != RigidbodyInterpolation2D.Extrapolate)
				rocket.rocket.rb2d.interpolation = RigidbodyInterpolation2D.Extrapolate;
			ReSeedFromAuthoritative(newest);
			return;
		}

		// 没有新包：只有"确实很久没收到包"才交回本地引擎。
		// WorldTime<=0 的包（服务端部分初始包就是这样）不参与判断，否则会立刻误判成断流。
		if (currentUpdate != null && ClientManager.world != null && currentUpdate.WorldTime > 0.0)
		{
			double packetAge = ClientManager.world.WorldTime - currentUpdate.WorldTime;
			if (packetAge > AdaptiveProfile.MaximumExtrapolationSeconds)
			{
				if (!freeSimulation)
				{
					freeSimulation = true;
					rocket.rocket.rb2d.bodyType = RigidbodyType2D.Dynamic;
					rocket.rocket.rb2d.interpolation = RigidbodyInterpolation2D.None;
					Debug.Log($"[SFS-MP] packet stall {packetAge:F1}s, hand to local physics: id={currentUpdate.RocketId}");
				}
				return;
			}
		}

		// Kinematic + Extrapolate：火箭由我们的写回驱动，包间由 Unity 外推补足
		if (rocket.rocket.rb2d.bodyType != RigidbodyType2D.Kinematic)
			rocket.rocket.rb2d.bodyType = RigidbodyType2D.Kinematic;
		if (rocket.rocket.rb2d.interpolation != RigidbodyInterpolation2D.Extrapolate)
			rocket.rocket.rb2d.interpolation = RigidbodyInterpolation2D.Extrapolate;
	}

	// 每收到一个权威包，把权威状态整份写回（走游戏自己的物理 API）。
	//
	// 为什么是"整份写回 + 包间交给游戏推进"，而不是逐帧写、也不是弹簧速度：
	//  - 逐帧写位置（V1.2.3.18）= 每帧扰动刚体 → 高频中幅抖动；
	//  - 弹簧速度写 rb2d.velocity（V1.2.3.19）= 被游戏物理模块覆盖，纠偏不生效 → 位置不同步（实测）；
	//  - 整份写回走的是 ApplyStateImmediately → LocalPosition/LocalVelocity 或 SetLocationAndState，
	//    与旧插值路径同一个原语（实机证明有效）。包间隔 20~50ms 内由 Unity 外推补足，误差 ~½at²。
	private void ReSeedFromAuthoritative(Packet_UpdateRocketPrimary packet)
	{
		Location here = rocket.rocket.location.Value;
		if (here == null) return;

		Location want = packet.Location.ToVanillaLocation();
		double renderTime = ClientManager.world != null ? ClientManager.world.WorldTime : packet.WorldTime;
		double renderDelay = RenderDelaySeconds;
		double age = renderTime - renderDelay - packet.WorldTime;
		if (age > 0.0)
		{
			// 把包推到"渲染时刻"（比旧插值延迟小一个量级，所以不再有几十上百米的滞后）
			double max = AdaptiveProfile.MaximumExtrapolationSeconds;
			want.position += packet.Location.velocity * Math.Min(age, max);
		}

		if (here.planet != want.planet)
		{
			// 换星球/传送：必须硬对齐
			ApplyStateImmediately(want, packet.Rotation, packet.AngularVelocity);
			return;
		}

		Vector2 posErr = WorldView.ToLocalPosition(want.position) - WorldView.ToLocalPosition(here.position);
		Vector2 velErr = WorldView.ToLocalVelocity(want.velocity) - WorldView.ToLocalVelocity(here.velocity);
		float posErrLen = posErr.magnitude;

		// 【临时诊断】预测模式下的实时误差
		if (Time.unscaledTime >= nextSyncProbeTime)
		{
			nextSyncProbeTime = Time.unscaledTime + SyncProbeIntervalSeconds;
			Debug.Log($"[SFS-MP][syncprobe] id={packet.RocketId} q={AdaptiveProfile.Quality} "
				+ $"v={WorldView.ToLocalVelocity(want.velocity).magnitude:F0}m/s posErr={posErrLen:F1}m "
				+ $"velErr={velErr.magnitude:F1}m/s age={age:F3}s buf={updateBuffer.Count} "
				+ $"delay={renderDelay * 1000:F0}ms interval={smoothedPacketIntervalSeconds * 1000:F0}ms "
				+ $"body={rocket.rocket.rb2d.bodyType} phys={rocket.rocket.physics.PhysicsMode}");
		}

		if (posErrLen > HardResyncMeters)
		{
			// 几十上百米：说明本地与权威端已经脱节（长断流/长时间无包），对齐一次并留日志
			Debug.Log($"[SFS-MP] remote rocket resync: id={packet.RocketId} err={posErrLen:F0}m");
		}

		// 整份写回：位置、速度、角度、角速度都取权威值（含地形钳制与物理模式处理）
		ApplyStateImmediately(want, packet.Rotation, packet.AngularVelocity);
	}

	private void InterpolatePackets(Packet_UpdateRocketPrimary prev, Packet_UpdateRocketPrimary next)
	{
		if (prev.Location.address != next.Location.address)
		{
			SetState(next.Location.ToVanillaLocation(), next.Rotation, next.AngularVelocity);
			if (LocalManager.players != null)
			{
				foreach (var player in LocalManager.players.Values)
				{
					if (player.controlledRocket.Value == next.RocketId)
					{
						MsgDrawer.main?.Log($"{player.username} arrived at {next.Location.address}");
						break;
					}
				}
			}
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

	private static bool terrainLookupFailed;

	private static Location ClampBelowTerrain(Location loc)
	{
		if (loc == null || loc.planet == null || loc.position.sqrMagnitude <= 1E-10)
		{
			return loc;
		}
		// 【性能关键，勿删】远高于地表时不可能埋进地形：直接跳过地形采样 ——
		// 与游戏本体 Planet.IsInsideTerrain 的首行守卫完全同款
		//（本体的判定是 position.Mag_MoreThan(Radius + maxTerrainHeight) 就直接 return false）。
		// 为什么必须加：GetTerrainHeight → Planet.GetTerrainHeightAtAngle 每次调用都会
		//   ① new double[1]（angle 数组）② 里面再 new double[] 两次 ③ 构造 TerrainSample
		//   （含一个 Dictionary<string,double[]>）④ 跑一整轮地形采样解释器
		//   （AddHeightMap/曲线，每步都分配数组）⑤ 有海水的行星还要逐角度做
		//   Texture2D.GetPixelBilinear 读切图；
		// 而本函数在插值路径上是【每帧 × 每枚远端火箭】都会被调用一次
		//（SetState / ApplyStateImmediately），12~18 枚火箭 = 每秒上千次这种调用，
		// 且火箭在轨/在太空时这次采样纯属浪费 ——
		// 这就是"只有联机时才内存暴涨到数 GB（每秒一次 GC、MarkObjects 40ms）+ CPU 拉满"的主因。
		// 该守卫与旧行为严格等价：|pos| > Radius + maxTerrainHeight 时
		// terrainHeightAtAngle(angle) ≤ maxTerrainHeight，GetTerrainHeight() 必然 ≥ 0，
		// 原代码本来就会原样 return loc。
		var planetData = loc.planet.data;
		if (planetData == null || planetData.basics == null)
		{
			return loc;
		}
		double terrainLimit = planetData.basics.radius + loc.planet.maxTerrainHeight;
		if (loc.position.sqrMagnitude > terrainLimit * terrainLimit)
		{
			return loc;
		}
		// 地形数据未就绪时 GetTerrainHeightAtAngles 会抛 NullReferenceException（行星地形尚未生成，
		// 实测一局 22 次）。Unity 每个异常都要抓栈并触发一次崩溃报告上传，而且异常会中断调用方的
		// 状态写入（位置/速度写一半）—— 那是"火箭消失/位置异常"的温床。这里吞掉并跳过钳制，
		// 位置照常写入；地形可用后自动恢复钳制。
		double belowTerrain;
		try
		{
#if SFS15
			// SFS 1.5 没有海洋：Location.GetTerrainHeight(clampToWater) 不存在，
			// 等价物是 TerrainHeight 属性（纯地形高度）。按"海洋当陆地"的口径直接用陆地高度。
			belowTerrain = loc.TerrainHeight;
#else
			belowTerrain = loc.GetTerrainHeight(clampToWater: true);
#endif
		}
		catch (Exception)
		{
			if (!terrainLookupFailed)
			{
				terrainLookupFailed = true;
				UnityEngine.Debug.LogWarning("[SFS-MP] 行星地形数据未就绪，暂时跳过位置钳制（会自动恢复）。");
			}
			// 返回 null 表示"本帧无法判断位置是否在地面以下"：调用方必须跳过本帧位置写入。
			// 旧版在这里原样返回未钳制的 Location，调用方照样写进物理体 —— 位置落到地形内部后，
			// 原版 SFS.World.Physics.Update → Planet.IsInsideTerrain → GetTerrainHeightAtAngles
			// 会在地形未就绪时抛 NullReferenceException（现场 NRE ×10+、崩溃报告 ×3 的唯一栈）。
			return null;
		}
		terrainLookupFailed = false;
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
		if (loc == null) return;   // 地形未就绪：本帧不写位置/速度/角度，避免把未钳制的状态灌进物理体
		// 防飘修复（勿回退）：旧版在此对插值结果叠加重指数平滑（CorrectionSeconds 0.45~1.4s），
		// 恒速滞后 = 速度 × 常数，真机表现为远端火箭持续“飘”。全裸切（零平滑）又会在包边界微跳。
		// 折衷：平滑常数 = 包间隔 × 0.2（钳制 0.02~0.12s），滞后比旧版低一个量级且不闪。
		// 跳变恢复仍由首包硬对齐（PrimeNonAuthorityState/PrimeAuthorityState）负责。
		NetworkAdaptiveProfile profile = AdaptiveProfile;
		double smoothing = InterpolationRenderPolicy.SmoothingSeconds(profile.ControlledIntervalMilliseconds / 1000.0);
		double delta = Math.Max(0.001, Time.unscaledDeltaTime);
		Location current = rocket.rocket.location.Value;
		if (current != null && current.planet == loc.planet)
		{
			// 粗差硬对齐（仅在真断流时启用，见下方 if 的 freeSimulation 门）：
			// 平滑是"每帧朝目标靠一点"，偏差几百米时永远靠不完，所以需要一次性对齐。
			// 【2026-09-22 修正】以前是无条件启用，而平滑跟踪运动目标的稳态滞后就是 v×τ
			//（600m/s × 0.02s = 12m），远大于这里 10m 的门限 → 每几帧被判成"粗差"瞬移一次，
			// 实机就是"速度越快越一闪一闪、延迟越大越明显"。
			// 反编译对照 1.1.4.5（用户认为不闪的那版）：它的 SetState 只有纯平滑，根本没有这段硬对齐。
			double errorX = loc.position.x - current.position.x;
			double errorY = loc.position.y - current.position.y;

			// 【临时诊断】分清两种错位：
			//   targetErr = 我们要的位置 与 逻辑当前位置 之差（插值/包/纠偏层面的）
			//   renderErr = 我们要的位置 与 屏幕上实际位置 之差（含 Unity 外推的那一段）
			// 闪烁若来自外推层，renderErr 会随帧相位大幅摆动而 targetErr 很小；反之则是插值/包的问题。
			if (Time.unscaledTime >= nextSyncProbeTime)
			{
				nextSyncProbeTime = Time.unscaledTime + SyncProbeIntervalSeconds;
				Vector2 wantPos = WorldView.ToLocalPosition(loc.position);
				Vector2 renderPos = rocket.rocket.rb2d.transform.position;
				Debug.Log($"[SFS-MP][syncprobe] id={currentUpdate.RocketId} q={profile.Quality} "
					+ $"v={loc.velocity.magnitude:F0}m/s targetErr={Math.Sqrt(errorX * errorX + errorY * errorY):F1}m "
					+ $"renderErr={Vector2.Distance(renderPos, wantPos):F1}m tau={smoothing * 1000:F0}ms "
					+ $"age={DelayedWorldTime - currentUpdate.WorldTime:F3}s buf={updateBuffer.Count} "
					+ $"freeSim={freeSimulation} stepMs={Time.fixedDeltaTime * 1000:F1} frameMs={delta * 1000:F1}");
			}

			// 只有"本地引擎在擅自推进"（断流后的 freeSimulation）才可能出现几百米级真偏差；
			// 常规飞行交给平滑跟踪，不做瞬移。
			if (freeSimulation
				&& errorX * errorX + errorY * errorY > GrossPositionErrorMeters * GrossPositionErrorMeters)
			{
				if (Time.unscaledTime >= nextDriftCorrectionLogTime)
				{
					nextDriftCorrectionLogTime = Time.unscaledTime + 1f;
					Debug.Log($"[SFS-MP] stall resync: id={currentUpdate.RocketId} "
							+ $"err={Math.Sqrt(errorX * errorX + errorY * errorY):F0}m");
				}
				// 偏差是"本地引擎擅自推进"造成的，硬对齐之后必须再从服务端取一次权威状态，
				// 否则下一帧又会按本地结果漂出去。
				if (ClientManager.world.WorldTime - lastSnapshotRequestWorldTime >= 0.5)
				{
					lastSnapshotRequestWorldTime = ClientManager.world.WorldTime;
					ClientManager.client?.RequestRocketSnapshot(currentUpdate.RocketId);
				}
			}
			else
			{
				(loc.position.x, loc.position.y) = InterpolationRenderPolicy.ResolvePosition(
					smoothing, delta, current.position.x, current.position.y, loc.position.x, loc.position.y);
				(loc.velocity.x, loc.velocity.y) = InterpolationRenderPolicy.ResolveVelocity(
					smoothing, delta, current.velocity.x, current.velocity.y, loc.velocity.x, loc.velocity.y);
			}
		}
		rot = InterpolationRenderPolicy.ResolveRotation(smoothing, delta, rocket.rocket.rb2d.transform.eulerAngles.z, rot);
		angVel = InterpolationRenderPolicy.ResolveAngularVelocity(smoothing, delta, rocket.rocket.rb2d.angularVelocity, angVel);
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
			// 爆炸特效走每帧预算：整枚火箭被炸时不让一帧内生成几十个特效（那是卡顿来源）。
			// 预算用尽时部件照常摧毁，只是不放特效。
			bool explosion = packet.CreateExplosion && LocalManager.AllowRemoteExplosion();
			value.DestroyPart(explosion, updateJoints: true, (DestructionReason)4);
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
				if (rocket.parts.TryGetValue(partID, out var part))
					list.Add(part);
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
				if (modules.Length == 0)
				{
					// 无资源模块的部件（结构件等）收到资源更新包时会走到这里，
					// 直接跳过。原先跳过了长度 0 的判断，会用 modules[0] 抛 IndexOutOfRangeException。
					continue;
				}
				if (modules.Length > 1)
				{
					Debug.LogWarning("OnPacket_UpdatePart_ResourceModule: Found multiple resource modules on part \"" + value.Name + "\".");
				}
				modules[0].resourcePercent.Value = packet.ResourcePercent;
			}
		}
	}
}
