// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

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
using MultiplayerSFS.Mod.Patches;

namespace MultiplayerSFS.Mod
{

public static class LocalManager
{
    // 内存保护：防止字典无限增长
    private const int MaxTrackedRockets = 256;
    private const int MaxPendingCreates = 64;
    private const int MaxResourceModules = 1024;

    // 每帧都会跑的维护/重试必须限流：它们要解析 Rocket prefab（含全场景对象扫描），
    // 每帧执行会把主线程拖成幻灯片（真机日志：同一段重试刷了 963 次）。
    private const float MaintenanceIntervalSeconds = 1.0f;
    private const float PendingRetryIntervalSeconds = 0.5f;

    // 待创建火箭的退避重试状态（见 Update 里的重试块）：
    // 原来每轮 0.5 秒把【全部】待创建包重试一遍，18 枚火箭 = 每秒 36 次整枚 Instantiate
    // （75 部件），实测把主线程按到 fps=1、窗口"未响应"；而且失败永不放弃，形成永久风暴。
    private const int MaxPendingCreateAttempts = 2;
    private static float pendingRetryInterval = PendingRetryIntervalSeconds;
    private static readonly Dictionary<int, int> pendingCreateAttempts = new Dictionary<int, int>();
    private const float PrefabLookupCooldownSeconds = 1.0f;
    private static float nextMaintenanceTime;
    private static float nextPendingRetryTime;
    private static float nextPrefabLookupTime;

    // 50ms 一轮的发送循环里禁止 LINQ 分配：把"当前有人控制的火箭"缓存进这个复用集合
    private static readonly HashSet<int> controlledRocketIdsScratch = new HashSet<int>();

    // 性能快照用：待建火箭队列长度
#pragma warning disable IDE1006
    public static int PendingCreateCount
    {
        get { return pendingCreateRockets.Count; }
    }
#pragma warning restore IDE1006

    public static Dictionary<int, LocalPlayer> players;

    public static Dictionary<int, LocalRocket> syncedRockets;

    public static Dictionary<int, LocalRocket> unsyncedRockets;

    public static HashSet<int> updateAuthority;

    public static int unsyncedToControl = -1;

    public static HashSet<int> pendingControlLocalIds = new HashSet<int>();

    private static readonly Dictionary<int, Packet_CreateRocket> pendingCreateRockets = new Dictionary<int, Packet_CreateRocket>();

    public const DestructionReason CustomDestructionReason = (DestructionReason)4;

    public static DestructionReason TrueDestructionReason = DestructionReason.Intentional;

    public static double updateRocketsPeriod = 10.0;

    private static Timer updateTimer;

    // ---- 发送热路径的复用容器：这几处原来每个 tick（10ms 一轮 = 100 次/秒）都新建
    //      List / ToList / LINQ ToHashSet，在 Mono 的非分代 GC 下直接变成每秒数 MB 垃圾 ——
    //      实测表现是内存涨到数 GB、GC 抖动导致 CPU 拉满与跳帧（"只有联机卡"）。
    //      这些容器只在主线程的 SendUpdatePackets 里使用，复用是安全的。
    private static readonly List<ResourceModule> resourceKeySweepScratch = new List<ResourceModule>();
    private static readonly List<int> staleRocketIdsScratch = new List<int>();
    private static readonly HashSet<int> partIdsScratch = new HashSet<int>();
    private static float lastResourceKeySweepTime = -999f;

    private static readonly Dictionary<ResourceModule, double> prevResourcePercents = new Dictionary<ResourceModule, double>();

    private static readonly Dictionary<int, DateTime> lastRocketStateSend = new Dictionary<int, DateTime>();

    private static readonly Dictionary<int, DateTime> lastRocketInputSend = new Dictionary<int, DateTime>();

    private static Queue mainThreadActions = new Queue();

    private static object mainThreadActionsLock = new object();

    public static LocalPlayer Player
    {
        get
        {
            if (players != null && players.TryGetValue(ClientManager.playerId, out var value))
            {
                return value;
            }
            return null;
        }
    }

    private static Rocket cachedRocketPrefab;
    private static System.Reflection.FieldInfo rocketPrefabFieldInfo;

    // 读 RocketManager 的静态 prefab 字段 —— 1.1.4 一直在用的那一路（AccessTools.StaticFieldRefAccess）。
    // 这里用原生反射等价实现，避免额外依赖；FieldInfo 只解析一次。
    private static Rocket ReadStaticRocketPrefabField()
    {
        try
        {
            if (rocketPrefabFieldInfo == null)
            {
                rocketPrefabFieldInfo = typeof(RocketManager).GetField("prefab",
                    System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic);
            }
            if (rocketPrefabFieldInfo == null) return null;
            return rocketPrefabFieldInfo.GetValue(null) as Rocket;
        }
        catch
        {
            return null;
        }
    }

    private static Rocket RocketPrefab
    {
        get
        {
            // 联机场景下静态 RocketManager.prefab 为 null，直接 Instantiate(null) 抛异常
            // 导致 OnPacket_CreateRocket 中断、对方 LocalRocket 永远建不出 -> 完全冻结。
            // 优先取当前活跃 RocketManager 实例的 rocketPrefab 字段（游戏正常造火箭用的就是它）。

            // 1) 已解析成功就复用：Unity 的 == null 能识别被销毁的对象，世界重启会自动重新解析
            if (cachedRocketPrefab != null) return cachedRocketPrefab;

            // 1.5) 与 1.1.4 同款：先直接读 RocketManager 的【静态 prefab 字段】（一次反射读，零扫描）。
            // 1.1.4 就靠这一行拿 prefab，所以从不会出现"prefab 未就绪"；新版换成 FindObjectOfType
            // 全场景扫描后，为省开销加了 1 秒冷却 —— 突发时会假报未就绪，于是整条创建路径被推进
            // pending 队列、每 0.5 秒全量重试（实测 fps=1、窗口"未响应"）。这里把旧版那一路加回来，
            // 冷却只留给下面真正昂贵的扫描兜底。
            Rocket staticPrefabField = ReadStaticRocketPrefabField();
            if (staticPrefabField != null)
            {
                cachedRocketPrefab = staticPrefabField;
                return staticPrefabField;
            }

            // 2) 解析失败时限流：未进世界时 RocketManager 不存在，
            //    每次查找都要 FindObjectOfType + Resources.FindObjectsOfTypeAll 全场景扫描，
            //    每帧重试会把主线程卡成幻灯片。这里 1 秒内只尝试一次。
            if (Time.unscaledTime < nextPrefabLookupTime) return null;
            nextPrefabLookupTime = Time.unscaledTime + PrefabLookupCooldownSeconds;

            RocketManager manager = UnityEngine.Object.FindObjectOfType<RocketManager>();
            if (manager == null)
            {
                var all = UnityEngine.Resources.FindObjectsOfTypeAll<RocketManager>();
                if (all != null && all.Length > 0) manager = all[0];
            }
            if (manager != null)
            {
                Rocket instPrefab = AccessTools.FieldRefAccess<RocketManager, Rocket>(manager, "rocketPrefab");
                if (instPrefab != null)
                {
                    cachedRocketPrefab = instPrefab;
                    UnityEngine.Debug.Log($"[SFS-MP] RocketPrefab 解析成功: prefab={instPrefab.GetInstanceID()}");
                    return instPrefab;
                }
            }
            Rocket staticPrefab = AccessTools.StaticFieldRefAccess<Rocket>(typeof(RocketManager), "prefab");
            if (staticPrefab != null)
            {
                cachedRocketPrefab = staticPrefab;
                UnityEngine.Debug.Log($"[SFS-MP] RocketPrefab 回退静态 prefab={staticPrefab.GetInstanceID()}");
                return staticPrefab;
            }
            return null;
        }
    }

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
        pendingControlLocalIds.Clear();
        pendingCreateRockets.Clear();
        // 上一局的发射/创建登记必须清掉：否则新会话里 Update 一 tick 就会把旧 CreateRocket
        // （旧 LocalId + 旧状态）重发出去，在新世界凭空造出一枚幽灵火箭。
        pendingLaunches.Clear();
        lastRocketStateSend.Clear();
        lastRocketInputSend.Clear();
        prevResourcePercents.Clear();
        launchpadStateByInstance.Clear();
        ignoredRocketPairs.Clear();
        launchpadStateDirty = true;
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
                // 定时器周期可能只有 10ms（=100 次/秒）。主线程渲染慢时，这里会持续积压
                // 待处理发送任务（每次都是一个 Action 分配 + 一次全量发送检查），
                // 表现为每秒 GC 与越来越严重的卡顿。已有待处理项就跳过本次入队。
                if (mainThreadActions.Count == 0)
                {
                    mainThreadActions.Enqueue((Action)delegate
                    {
                        SendUpdatePackets();
                    });
                }
            }
        };
        SceneHelper.OnHomeSceneLoaded += new Action(DisableUpdateTimer);
    }

    // 远端爆炸特效预算：一帧内最多生成几次爆炸特效。
    // 服务端把一整枚火箭炸掉时，客户端会在一帧里对其所有部件逐个 DestroyPart，
    // 每件都生成一次爆炸特效 -> 渲染峰值（实测：自己的火箭爆炸不卡，看别人的火箭爆炸卡）。
    private const int RemoteExplosionPerFrame = 2;
    private static int remoteExplosionBudget;

    // 每帧重置（Update 里调用）
    public static void ResetRemoteExplosionBudget()
    {
        remoteExplosionBudget = RemoteExplosionPerFrame;
    }

    // 远端部件摧毁是否允许生成爆炸特效（超出预算就静默摧毁，部件照常消失）
    public static bool AllowRemoteExplosion()
    {
        if (remoteExplosionBudget <= 0) return false;
        remoteExplosionBudget--;
        return true;
    }

    // 发射流程的兜底：SyncLaunch 会打开"Sending launch request to server..."全屏加载屏，
    // 但上游代码里没有配对的 Close（ClientManager 的 Close 只服务于连接流程）。
    // 加载屏一旦残留，就盖住了整个世界：表现为"造的火箭完全没创建出来"、"点不进火箭控制权"。
    // 这里在收到服务端回执时关闭；5 秒没回执也强制关闭（火箭照常在回执到达后建出）。
    private const float LaunchTimeoutSeconds = 5f;
    private static float launchRequestTime;

    public static void NotifyLaunchRequestSent()
    {
        launchRequestTime = Time.unscaledTime;
    }

    private static void CloseLaunchLoadingScreen()
    {
        launchRequestTime = 0f;
        SFS.UI.Menu.loading.Close();
    }

    // 加入流程的加载屏兜底（与发射同款：上游这条路径只有 Open 没有 Close）
    private const float WorldLoadingTimeoutSeconds = 10f;
    private static float worldLoadingStartedTime;

    public static void NotifyWorldLoadingStarted()
    {
        worldLoadingStartedTime = Time.unscaledTime;
    }

    private static void CloseWorldLoadingScreen()
    {
        worldLoadingStartedTime = 0f;
        SFS.UI.Menu.loading.Close();
    }

    // 进入世界时必须绕过 prefab 解析冷却：否则上一帧的解析失败会让整批火箭建不出来
    private static void ResetPrefabLookupCooldown()
    {
        nextPrefabLookupTime = 0f;
    }

    // 绑定成功后必须做与另外两个创建分支相同的初始化：否则 currentUpdate 恒为 null，
    // Interpolator.Update() 每帧直接 return —— 这枚火箭再也不会被任何远端包驱动。
    private static void PrimeRocketInterpolator(LocalRocket localRocket, Packet_CreateRocket packet)
    {
        if (localRocket == null || localRocket.interpolator == null) return;
        localRocket.interpolator.updateBuffer.Clear();
        localRocket.interpolator.packetBuffer.Clear();
        if (localRocket.rocket != null)
        {
            localRocket.interpolator.currentUpdate = localRocket.rocket.ToUpdatePacketPrimary(packet.GlobalId);
            if (localRocket.interpolator.currentUpdate != null)
                localRocket.interpolator.currentUpdate.WorldTime = 0.0;
        }
        localRocket.interpolator.isNewlyCreated = true;
    }

    // 放弃重发时清理本地临时火箭：服务端没认领它，本地再留着就是"别人看不到、
    // 自己控制不了、也毁不掉"的幽灵（三处补丁按未登记直接把物理/销毁/控制全拦死）。
    private static void AbandonUnsyncedRocket(int localId)
    {
        if (unsyncedRockets == null || !unsyncedRockets.TryGetValue(localId, out LocalRocket localRocket)) return;
        unsyncedRockets.Remove(localId);
        pendingControlLocalIds.Remove(localId);
        if (unsyncedToControl == localId) unsyncedToControl = -1;
        if (localRocket == null || localRocket.rocket == null) return;
        UnityEngine.Debug.LogWarning($"[SFS-MP] 服务端未认领临时火箭（localId={localId}），本地回收，避免留下不可控幽灵。");
        // 用 (DestructionReason)4 这条"自定义原因"通道绕过联机的销毁拦截（与 DestroyLocalRocket 同款）
        SFS.World.RocketManager.DestroyRocket(localRocket.rocket, (SFS.World.DestructionReason)4);
    }

    // 发射请求的自动重发。实测症状："第一次容易失败，第二次百分百成功"——
    // 会话刚建立时首个业务包容易丢（和之前 KCP 连接首次失败、重试即成功同源）。
    // 重发用同一个 LocalId，服务端按 (玩家, LocalId) 去重，所以重发不会多造一枚火箭。
    private const float LaunchResendIntervalSeconds = 1.5f;
    private const int LaunchResendMaxTries = 4;

    private class PendingLaunch
    {
        public Packet_CreateRocket Packet;
        public float NextSendTime;
        public int Tries;
    }

    private static readonly Dictionary<int, PendingLaunch> pendingLaunches = new Dictionary<int, PendingLaunch>();

    // 发出 CreateRocket 后登记，等回执；超时未回执就在 Update 里重发
    public static void RegisterLaunchRequest(int localId, Packet_CreateRocket packet)
    {
        pendingLaunches[localId] = new PendingLaunch
        {
            Packet = packet,
            NextSendTime = Time.unscaledTime + LaunchResendIntervalSeconds,
            Tries = 1
        };
    }

    // 每次在本端生成/发射新火箭之前，先把发射台上所有没人操控的影子火箭清掉。
    // 用户实测：这些幽灵在本端世界里是带碰撞箱的实体，新火箭一生成就和它们叠在一起，
    // 发射流程/物理会把主线程卡死（程序未响应 = 这次要修的 bug）。原生单人行为也是"新火箭顶掉发射台上的旧火箭"。
    public static void ClearUnmannedRocketsOnLaunchpad()
    {
        if (syncedRockets == null || syncedRockets.Count == 0) return;
        List<int> doomed = null;
        foreach (var kvp in syncedRockets)
        {
            int id = kvp.Key;
            var localRocket = kvp.Value;
            if (localRocket == null || localRocket.rocket == null) continue;
            // 只处理落在发射台半径内的（其它天体的火箭一律不动）
            bool withinPadZone;
            if (!TryGetRocketPadProximity(localRocket, out withinPadZone) || !withinPadZone) continue;
            // 有人在开的一律不动（含本端正在开的那枚）
            if (controlledRocketIdsScratch.Contains(id)) continue;
            if (LocalManager.Player != null && LocalManager.Player.controlledRocket.Value == id) continue;
            // 游戏侧"有人正在操控"的硬标志：正在飞的一律不碰（含玩家本端刚点燃的那枚）
            if (localRocket.rocket.hasControl.Value) continue;
            if (doomed == null) doomed = new List<int>();
            doomed.Add(id);
        }
        if (doomed == null) return;
        for (int i = 0; i < doomed.Count; i++)
        {
            int id = doomed[i];
            UnityEngine.Debug.Log($"[SFS-MP] 清理发射台上的无主火箭: id={id}");
            // 本地收尸：复用远端销毁的既有路径，同步表和插值器一起清干净
            DestroyLocalRocket(id);
            // 通知服务端，让别的客户端也把它删掉
            ClientManager.SendPacket(new Packet_DestroyRocket
            {
                WorldTime = ClientManager.world != null ? ClientManager.world.WorldTime : double.NaN,
                RocketId = id,
                Reason = DestructionReason.Intentional
            });
        }
    }

    private static void RetryPendingLaunches()
    {
        if (pendingLaunches.Count == 0) return;
        List<int> done = null;
        foreach (var kv in pendingLaunches)
        {
            PendingLaunch pending = kv.Value;
            if (Time.unscaledTime < pending.NextSendTime) continue;
            if (pending.Tries >= LaunchResendMaxTries)
            {
                UnityEngine.Debug.LogWarning($"[SFS-MP] 发射/创建请求重发 {pending.Tries} 次仍无回执，放弃（localId={kv.Key}）。");
                // 放弃就必须收尸：否则这枚本地火箭既没同步也删不掉（三处补丁按未登记拦死），
                // 变成"别人看不到、自己控制不了、物理也不跑"的幽灵 —— 用户看到的"完全没创建出来"。
                AbandonUnsyncedRocket(kv.Key);
                (done ?? (done = new List<int>())).Add(kv.Key);
                continue;
            }
            pending.Tries++;
            pending.NextSendTime = Time.unscaledTime + LaunchResendIntervalSeconds;
            UnityEngine.Debug.Log($"[SFS-MP] 创建请求未收到回执，重发第 {pending.Tries} 次（localId={kv.Key}）");
            ClientManager.SendPacket(pending.Packet);
        }
        if (done != null)
        {
            foreach (int key in done) pendingLaunches.Remove(key);
        }
    }

    // 碰撞隔离重算节流：Update 里原来每帧调 UpdateLaunchpadCollisionIsolation()，
    // 而它内部是对"每一对火箭"调用 Physics2D.IgnoreCollision —— 18 枚火箭 = 153 对/帧。
    // 单人只有 1 枚火箭所以看不出来，联机火箭一多就把主线程 CPU 吃满（实测 90%）。
    // 节流到 0.1 秒一次：隔离效果无差别，主线程开销降到 1/6 以下。
    private static float nextCollisionIsolationTime;



    public static void Update()
    {
        // 远端爆炸特效预算每帧重置（见 AllowRemoteExplosion）
        ResetRemoteExplosionBudget();

        // 发射加载屏兜底：超过 5 秒没等到服务端回执就关掉，避免整屏遮挡
        if (launchRequestTime > 0f && Time.unscaledTime - launchRequestTime >= LaunchTimeoutSeconds)
        {
            UnityEngine.Debug.LogWarning("[SFS-MP] 发射请求 5 秒未收到服务端回执，强制关闭加载屏（火箭仍会在回执到达后建出）。");
            CloseLaunchLoadingScreen();
        }

        // 发射请求重发兜底（首包容易丢，重发走同一 LocalId，服务端去重）
        RetryPendingLaunches();

        // 控制请求超时重发：Pending 卡死会让那枚火箭永久接不进控制权
        ClientManager.RetryPendingControlRequest();

        // 加入流程的加载屏兜底：超时未进世界就强制关掉（避免整屏永久遮挡）
        if (worldLoadingStartedTime > 0f && Time.unscaledTime - worldLoadingStartedTime >= WorldLoadingTimeoutSeconds)
        {
            UnityEngine.Debug.LogWarning("[SFS-MP] 进入世界超时，强制关闭加载屏。");
            CloseWorldLoadingScreen();
        }

        // 内存保护：清理过期/无效条目。每秒一次即可 —— 内部有 LINQ 分配（HashSet/ToList），
        // 每帧跑会持续给 GC 施压，表现为周期性短暂卡顿。
        if (Time.unscaledTime >= nextMaintenanceTime)
        {
            nextMaintenanceTime = Time.unscaledTime + MaintenanceIntervalSeconds;
            CleanupStaleEntries();
        }

        // 发射台碰撞隔离：火箭之间互不碰撞，与行星/地形保持碰撞
        if (Time.unscaledTime >= nextCollisionIsolationTime)
        {
            nextCollisionIsolationTime = Time.unscaledTime + 0.1f;
            UpdateLaunchpadCollisionIsolation();
        }

        // 重试 prefab 未就绪时缓存的 CreateRocket 包。必须限流：重试要解析 Rocket prefab
        // （FindObjectOfType + FindObjectsOfTypeAll 全场景扫描），每帧重试会把主线程卡死。
        // 收到包时本来就会立刻尝试一次，这里只做兜底，半秒一次足够。
        if (pendingCreateRockets.Count > 0 && Time.unscaledTime >= nextPendingRetryTime)
        {
            nextPendingRetryTime = Time.unscaledTime + pendingRetryInterval;

            // 每轮只取【一枚】，不再一次 Instantiate 十几枚（那是 fps=1 的直接原因）
            int retryRocketId = -1;
            Packet_CreateRocket retryPacket = null;
            foreach (var kvp in pendingCreateRockets)
            {
                retryRocketId = kvp.Key;
                retryPacket = kvp.Value;
                break;
            }

            if (retryPacket != null)
            {
                pendingCreateRockets.Remove(retryRocketId);
                int attempts;
                pendingCreateAttempts.TryGetValue(retryRocketId, out attempts);
                attempts++;
                pendingCreateAttempts[retryRocketId] = attempts;

                OnPacket_CreateRocket(retryPacket);

                if (pendingCreateRockets.ContainsKey(retryRocketId))
                {
                    // 又失败了：要么放弃，要么按次数退避（0.5→1→2→4→8 秒）
                    if (attempts >= MaxPendingCreateAttempts)
                    {
                        pendingCreateRockets.Remove(retryRocketId);
                        pendingCreateAttempts.Remove(retryRocketId);
                        UnityEngine.Debug.LogError($"[SFS-MP] 火箭 GlobalId={retryRocketId} 连续 {attempts} 次创建失败，已放弃重试（避免每秒整枚 Instantiate 卡死主线程）。");
                    }
                    else
                    {
                        pendingRetryInterval = Mathf.Min(0.5f * Mathf.Pow(2f, attempts), 8f);
                    }
                }
                else
                {
                    pendingRetryInterval = PendingRetryIntervalSeconds;   // 建出来了：恢复基础间隔
                    pendingCreateAttempts.Remove(retryRocketId);
                }
            }
        }
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

    // ============================================================
    // 发射台碰撞隔离
    //
    // 关键：绝对不能改 Layer。改 Layer（把火箭整体挪到另一个层）会连
    // 火箭-行星/发射台结构的碰撞一起改掉，火箭会直接沉进发射台再卡进地里。
    // 这里只对"火箭对"调用 Physics2D.IgnoreCollision，行星/地形不受影响。
    // ============================================================

    // 每枚火箭当前的"是否在发射台"状态（按实例 ID 记录）
    private static readonly Dictionary<int, bool> launchpadStateByInstance = new Dictionary<int, bool>();

    // 当前已互相忽略碰撞的火箭对（实例 ID 组合）
    private static readonly HashSet<long> ignoredRocketPairs = new HashSet<long>();

    private static bool launchpadStateDirty = true;
    private static int collisionRefreshCountdown;

    private static long RocketPairKey(int a, int b)
    {
        long lo = Math.Min(a, b);
        long hi = Math.Max(a, b);
        return (lo << 32) | (hi & 0xFFFFFFFFL);
    }

    private static List<Collider2D> CollectRocketColliders(LocalRocket localRocket)
    {
        var list = new List<Collider2D>();
        if (localRocket == null || localRocket.parts == null) return list;
        foreach (var kvp in localRocket.parts)
        {
            var part = kvp.Value;
            if (part == null) continue;
            var cols = part.GetComponentsInChildren<Collider2D>(true);
            for (int i = 0; i < cols.Length; i++)
            {
                if (cols[i] != null) list.Add(cols[i]);
            }
        }
        return list;
    }

    // 部件指纹：零件增删（对接/分离/被摧毁）会改变它。只有它变了，"忽略碰撞"才需要重跑。
    private static long RocketColliderSignature(LocalRocket localRocket)
    {
        long signature = 17L;
        if (localRocket == null || localRocket.parts == null) return signature;
        signature = signature * 31 + localRocket.parts.Count;
        foreach (var kvp in localRocket.parts)
        {
            var part = kvp.Value;
            if (part == null) continue;
            signature = signature * 31 + part.GetInstanceID();
        }
        return signature;
    }

    private static void ApplyPairIgnore(LocalRocket a, LocalRocket b, bool ignore)
    {
        var ca = CollectRocketColliders(a);
        var cb = CollectRocketColliders(b);
        for (int i = 0; i < ca.Count; i++)
        {
            for (int j = 0; j < cb.Count; j++)
            {
                if (ca[i] == null || cb[j] == null) continue;
                Physics2D.IgnoreCollision(ca[i], cb[j], ignore);
            }
        }
        // 记录本次应用时的部件指纹：重放时指纹未变就整对跳过
        if (a != null) a.colliderSignature = RocketColliderSignature(a);
        if (b != null) b.colliderSignature = RocketColliderSignature(b);
    }

    // 用游戏本体同一套判定（GameManager.IsOnLaunchpad 的 ReversePatch）
    // 碰撞豁免范围（用户明确规格）：发射台 200 米半径内火箭之间不碰撞，
    // 一超过 200 米立即恢复碰撞。半径按发射台坐标的水平距离算 —— 与游戏
    // GameManager.IsOnLaunchpad 用的是同一处坐标 Base.planetLoader.spaceCenter.LaunchPadLocation。
    private const float PadCollisionFreeRadiusMeters = 200f;

    private static bool TryGetRocketPadProximity(LocalRocket localRocket, out bool withinPadZone)
    {
        withinPadZone = false;
        if (localRocket == null || localRocket.rocket == null) return false;
        var locationRef = localRocket.rocket.location;
        if (locationRef == null) return false;
        var location = locationRef.Value;
        if (location == null || location.planet == null) return false;
        double padX, padY;
        string padAddress;
        if (!LaunchpadReference.TryGetPosition(out padX, out padY, out padAddress)) return false;
        // 只在发射台所在天体上做半径判定；其它天体一律算不在豁免区
        if (location.planet.codeName != padAddress) return true;
        double dx = location.position.x - padX;
        double dy = location.position.y - padY;
        withinPadZone = (dx * dx + dy * dy) <= (double)PadCollisionFreeRadiusMeters * PadCollisionFreeRadiusMeters;
        return true;
    }

    private static LocalRocket FindRocketByInstance(int instanceId)
    {
        if (syncedRockets == null) return null;
        foreach (var kvp in syncedRockets)
        {
            var lr = kvp.Value;
            if (lr != null && lr.rocket != null && lr.rocket.GetInstanceID() == instanceId) return lr;
        }
        return null;
    }

    private static void UpdateLaunchpadCollisionIsolation()
    {
        if (syncedRockets == null || syncedRockets.Count == 0) return;

        // 1) 刷新每枚火箭的发射台状态；状态变化时置脏
        foreach (var kvp in syncedRockets)
        {
            var lr = kvp.Value;
            bool on;
            if (!TryGetRocketPadProximity(lr, out on)) continue;
            int instanceId = lr.rocket.GetInstanceID();
            bool previous;
            if (!launchpadStateByInstance.TryGetValue(instanceId, out previous) || previous != on)
            {
                launchpadStateByInstance[instanceId] = on;
                launchpadStateDirty = true;
            }
        }

        // 2) 状态变化时重算所有火箭对（只在变化时执行，避免每帧 O(n²) 忽略调用）
        if (launchpadStateDirty)
        {
            launchpadStateDirty = false;
            var rockets = new List<LocalRocket>();
            foreach (var kvp in syncedRockets)
            {
                if (kvp.Value != null && kvp.Value.rocket != null) rockets.Add(kvp.Value);
            }

            for (int i = 0; i < rockets.Count; i++)
            {
                for (int j = i + 1; j < rockets.Count; j++)
                {
                    var a = rockets[i];
                    var b = rockets[j];
                    bool onA, onB;
                    launchpadStateByInstance.TryGetValue(a.rocket.GetInstanceID(), out onA);
                    launchpadStateByInstance.TryGetValue(b.rocket.GetInstanceID(), out onB);
                    // 用户规格：任意一枚在发射台 200 米内 -> 这一对不碰撞；出圈立即恢复碰撞
                    bool desired = onA || onB;
                    long key = RocketPairKey(a.rocket.GetInstanceID(), b.rocket.GetInstanceID());
                    bool applied = ignoredRocketPairs.Contains(key);
                    if (applied == desired) continue;
                    ApplyPairIgnore(a, b, desired);
                    if (desired) ignoredRocketPairs.Add(key);
                    else ignoredRocketPairs.Remove(key);
                }
            }
        }

        // 3) 周期性重放已忽略的对：只有零件真的增删过（部件指纹变化）才需要重放，
        //    因为新零件会带来新碰撞体、需要重新忽略一次。
        //    旧代码每 12 秒对每一对已忽略火箭【无条件】重跑 ApplyPairIgnore，
        //    而它内部是"火箭 A 全部碰撞体 × 火箭 B 全部碰撞体"的 Physics2D.IgnoreCollision 双重循环
        //    （18 枚火箭 = 单对最多上万次原生调用 + 每部件一次 GetComponentsInChildren 分配）。
        //    这就是"只有联机才卡、CPU 拉满、还有 6 秒级卡帧"的来源：单人没有火箭对，永远跑不到这里。
        if (ignoredRocketPairs.Count > 0 && ++collisionRefreshCountdown >= 120)
        {
            collisionRefreshCountdown = 0;
            foreach (var key in ignoredRocketPairs)
            {
                int instanceA = (int)(key >> 32);
                int instanceB = (int)(key & 0xFFFFFFFFL);
                var rocketA = FindRocketByInstance(instanceA);
                var rocketB = FindRocketByInstance(instanceB);
                if (rocketA == null || rocketB == null) continue;
                // 零件没增删 -> 碰撞体集合没变 -> 这一对不需要重放，直接跳过
                if (rocketA.colliderSignature == RocketColliderSignature(rocketA) &&
                    rocketB.colliderSignature == RocketColliderSignature(rocketB))
                {
                    continue;
                }
                ApplyPairIgnore(rocketA, rocketB, true);
            }
        }
    }

    // 内存保护：清理过期/无效条目
    private static void CleanupStaleEntries()
    {
        // 未进世界时 syncedRockets/unsyncedRockets 还没初始化，直接跳过。
        // 这两个集合为空时本来就没有陈旧条目可清；此前缺这层保护导致每帧空引用抛异常刷屏
        // （Unity 每个异常都会抓栈写日志并尝试上传崩溃报告，直接把帧率拖成 PPT）。
        if (syncedRockets == null || unsyncedRockets == null) return;

        // 清理 pendingCreateRockets：超过上限时移除最旧
        if (pendingCreateRockets.Count > MaxPendingCreates)
        {
            var toRemove = pendingCreateRockets.Keys.Take(pendingCreateRockets.Count - MaxPendingCreates).ToList();
            foreach (var k in toRemove) pendingCreateRockets.Remove(k);
        }

        // 清理 lastRocketStateSend/lastRocketInputSend：移除不在 syncedRockets 中的
        var validRocketIds = new HashSet<int>(syncedRockets.Keys);
        var staleStateKeys = lastRocketStateSend.Keys.Where(k => !validRocketIds.Contains(k)).ToList();
        foreach (var k in staleStateKeys) lastRocketStateSend.Remove(k);
        var staleInputKeys = lastRocketInputSend.Keys.Where(k => !validRocketIds.Contains(k)).ToList();
        foreach (var k in staleInputKeys) lastRocketInputSend.Remove(k);

        // 限制 lastRocketStateSend/lastRocketInputSend 大小
        if (lastRocketStateSend.Count > MaxTrackedRockets)
        {
            var toRemove = lastRocketStateSend.Keys.Take(lastRocketStateSend.Count - MaxTrackedRockets).ToList();
            foreach (var k in toRemove) lastRocketStateSend.Remove(k);
        }
        if (lastRocketInputSend.Count > MaxTrackedRockets)
        {
            var toRemove = lastRocketInputSend.Keys.Take(lastRocketInputSend.Count - MaxTrackedRockets).ToList();
            foreach (var k in toRemove) lastRocketInputSend.Remove(k);
        }

        // 清理 prevResourcePercents：移除不存在的 ResourceModule，限制大小
        var staleResourceKeys = prevResourcePercents.Keys.Where(rm => rm == null).ToList();
        foreach (var k in staleResourceKeys) prevResourcePercents.Remove(k);
        if (prevResourcePercents.Count > MaxResourceModules)
        {
            var toRemove = prevResourcePercents.Keys.Take(prevResourcePercents.Count - MaxResourceModules).ToList();
            foreach (var k in toRemove) prevResourcePercents.Remove(k);
        }

        // 清理 unsyncedRockets 中为 null 的条目
        var staleUnsynced = unsyncedRockets.Where(kvp => kvp.Value == null || kvp.Value.rocket == null).Select(kvp => kvp.Key).ToList();
        foreach (var k in staleUnsynced) unsyncedRockets.Remove(k);

        // 限制 unsyncedRockets 大小
        if (unsyncedRockets.Count > MaxTrackedRockets)
        {
            var toRemove = unsyncedRockets.Keys.Take(unsyncedRockets.Count - MaxTrackedRockets).ToList();
            foreach (var k in toRemove) unsyncedRockets.Remove(k);
        }
    }

    public static void DisableUpdateTimer()
    {
        updateTimer?.Close();
        updateTimer = null;
        lastRocketStateSend.Clear();
        lastRocketInputSend.Clear();
        prevResourcePercents.Clear();
        pendingCreateRockets.Clear();
        pendingLaunches.Clear();
        pendingControlLocalIds.Clear();
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
            ClientManager.client?.RequestRocketSnapshot(rocketId);
            lastRocketStateSend[rocketId] = DateTime.UtcNow;
            sent++;
        }
        if (sent > 0)
        {
            MsgDrawer.main?.Log("Forced position sync for " + sent + " rocket(s).");
        }
    }

    public static void SendUpdatePackets()
    {
        DateTime now = DateTime.UtcNow;
        NetworkAdaptiveProfile adaptive = ClientManager.client?.AdaptiveProfile ?? NetworkAdaptationPolicy.Evaluate(0, 0, 0);

        // 一次性收集"有人控制的火箭"，替代每枚火箭跑一次 players.Values.Any(...)（每轮都要分配委托）
        controlledRocketIdsScratch.Clear();
        if (players != null)
        {
            foreach (var player in players.Values)
            {
                int controlledId = player.controlledRocket.Value;
                if (controlledId >= 0) controlledRocketIdsScratch.Add(controlledId);
            }
        }
        // 原来每个 tick 都做 prevResourcePercents.Keys.ToList()（字典可涨到 1024 键 = 每次~8KB 垃圾）。
        // 这里改为每秒做一次，并用复用容器，去掉热路径上的分配。
        if (Time.realtimeSinceStartup - lastResourceKeySweepTime >= 1f)
        {
            lastResourceKeySweepTime = Time.realtimeSinceStartup;
            resourceKeySweepScratch.Clear();
            foreach (ResourceModule item in prevResourcePercents.Keys)
            {
                if (item == null) resourceKeySweepScratch.Add(item);
            }
            for (int i = 0; i < resourceKeySweepScratch.Count; i++)
            {
                prevResourcePercents.Remove(resourceKeySweepScratch[i]);
            }
        }
        staleRocketIdsScratch.Clear();
        var staleRocketIds = staleRocketIdsScratch;   // 复用容器，去掉每 tick 的 new List
        foreach (int item2 in updateAuthority)
        {
            if (!syncedRockets.TryGetValue(item2, out var localRocket))
            {
                staleRocketIds.Add(item2);
                continue;
            }
            Rocket rocket = localRocket.rocket;
            if (rocket == null || rocket.rb2d == null || rocket.location == null)
            {
                staleRocketIds.Add(item2);
                continue;
            }
            bool controlled = controlledRocketIdsScratch.Contains(item2);
            bool moving = rocket.location.Value.velocity.sqrMagnitude > 0.00000225 || Math.Abs(rocket.rb2d.angularVelocity) > 0.015f;
            if (controlled || moving)
            {
                int inputInterval = adaptive.ControlledIntervalMilliseconds;
                if (!lastRocketInputSend.TryGetValue(item2, out DateTime lastInput) ||
                    (now - lastInput).TotalMilliseconds >= inputInterval)
                {
                    lastRocketInputSend[item2] = now;
                    Packet_UpdateRocketSecondary packet2 = rocket.ToUpdatePacketSecondary(item2);
                    if (ClientManager.world.rockets.TryGetValue(item2, out var state)) state.UpdateRocketSecondary(packet2);
                    ClientManager.SendPacket(packet2, (NetDeliveryMethod)67);
                }
            }
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
            P2PConnectionManager.SendRocketState(packet);
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
                        // 原来是 4 段 LINQ（Select/Where/Select/Where + ToHashSet），
                        // 每次资源变化都产生多个迭代器与临时集合；改成手写循环 + 复用 HashSet。
                        PartIds = BuildPartIdsScratch(resourceModule, localRocket),
                        ResourcePercent = resourceModule.resourcePercent.Value
                    }, (NetDeliveryMethod)67);
                }
                prevResourcePercents[resourceModule] = resourceModule.resourcePercent.Value;
            }
        }

        // 清理过期的 rocketId
        foreach (var id in staleRocketIds)
        {
            updateAuthority.Remove(id);
            lastRocketStateSend.Remove(id);
            lastRocketInputSend.Remove(id);
        }
    }

    public static int GetSyncedRocketID(Rocket rocket)
    {
        // 手写遍历替代 syncedRockets.First(lambda)：原写法每次调用都要分配闭包 + 委托，
        // 而且找不到时抛 InvalidOperationException 再被 catch 吞掉（异常的构造+抛接开销远大于一次线性遍历）。
        // 本函数位于热路径上：Rocket_OnFixedUpdate 是【每个物理步 × 每枚火箭】都要调一次，
        // 另外 Part.DestroyPart / RocketManager.DestroyRocket / 各模块包也会调。
        int found = -1;
        foreach (var kvp in syncedRockets)
        {
            if (kvp.Value != null && kvp.Value.rocket == rocket)
            {
                found = kvp.Key;
                break;
            }
        }
        return found;
    }

    public static int GetUnsyncedRocketID(Rocket rocket)
    {
        // 同上：去掉 LINQ + 异常路径
        int found = -1;
        foreach (var kvp in unsyncedRockets)
        {
            if (kvp.Value != null && kvp.Value.rocket == rocket)
            {
                found = kvp.Key;
                break;
            }
        }
        return found;
    }

    public static int GetLocalPartID(int rocketId, Part part)
    {
        // 同上：原写法是 syncedRockets[...].parts.First(lambda) + catch，既分配又可能抛异常
        if (syncedRockets == null || part == null || !syncedRockets.TryGetValue(rocketId, out LocalRocket localRocket) ||
            localRocket == null || localRocket.parts == null)
        {
            return -1;
        }
        foreach (var kvp in localRocket.parts)
        {
            if (kvp.Value == part)
            {
                return kvp.Key;
            }
        }
        return -1;
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

    public static LocalRocket SpawnLocalRocket(RocketState state, int globalId = -1)
    {
        Rocket prefab = RocketPrefab;
        if (prefab == null)
        {
            UnityEngine.Debug.Log($"[SFS-MP] SpawnLocalRocket: prefab 为 null，返回 null（由 OnPacket_CreateRocket 缓存重试）");
            return null;
        }
        UnityEngine.Debug.Log($"[SFS-MP] SpawnLocalRocket: Instantiate prefab={prefab.GetInstanceID()}, GlobalId={globalId}");
        Rocket rocket = UnityEngine.Object.Instantiate(prefab);
        // 从 Instantiate 到构造出 LocalRocket 之间任何一步失败（零件缺失、关节、放置、分级……），
        // 都会留下一枚"半成品火箭"：它已经被 physicsMode:true 放进世界，游戏自己的气动/物理会对它
        // 每物理步抛 NullReferenceException（Aero_Rocket.GetDragSurfaces），并反复触发崩溃上报
        // （主线程做 I/O）—— 实测表现就是窗口"未响应"、fps=1。所以整段包进 try，失败一律销毁实例。
        try
        {
        rocket.rocketName = state.rocketName;
        rocket.throttle.throttleOn.Value = state.throttleOn;
        rocket.throttle.throttlePercent.Value = state.throttlePercent;
        rocket.arrowkeys.rcs.Value = state.RCS;
        Dictionary<int, Part> parts = new Dictionary<int, Part>(state.parts.Count);
        foreach (KeyValuePair<int, PartState> part3 in state.parts)
        {
            OwnershipState ownershipState;
            Part value = PartsLoader.CreatePart(part3.Value.part, null, null, OnPartNotOwned.Allow, out ownershipState);
            // null = 本机没有这个零件（对端装了本地未装的模组/零件）。以前会把 null 塞进 parts，
            // 造出一枚缺零件的半成品火箭并放进世界 → 游戏侧每物理步 NRE + 崩溃上报 → 窗口"未响应"。
            if (value == null)
            {
                UnityEngine.Debug.LogError($"[SFS-MP] 火箭 GlobalId={globalId} 含本机缺失的零件（{part3.Value.part}），放弃创建并销毁实例。");
                UnityEngine.Object.Destroy(rocket.gameObject);
                return null;
            }
            parts.Add(part3.Key, value);
        }
        List<PartJoint> list = new List<PartJoint>(state.joints.Count);
        foreach (JointState joint in state.joints)
        {
            if (joint.id_A != -1 && joint.id_B != -1)
            {
                if (!parts.TryGetValue(joint.id_A, out var part) || !parts.TryGetValue(joint.id_B, out var part2))
                    continue; // 关节引用的零件缺失时跳过该关节，避免单点缺失导致整枚火箭建不出
                list.Add(new PartJoint(part, part2, part2.Position - part.Position));
            }
        }
        rocket.SetJointGroup(new JointGroup(list, parts.Values.ToList()));
        rocket.rb2d.transform.eulerAngles = new Vector3(0f, 0f, state.rotation);
        rocket.physics.SetLocationAndState(state.location.ToVanillaLocation(), physicsMode: true);
        rocket.rb2d.angularVelocity = state.angularVelocity;
        foreach (StageState stage in state.stages)
        {
            List<Part> parts2 = stage.partIDs.Where(id => parts.ContainsKey(id)).Select(id => parts[id]).ToList();
            rocket.staging.InsertStage(new Stage(stage.stageID, parts2), record: false);
        }

        // 发射台碰撞隔离由 Update() 每帧统一处理（不改 Layer）
        launchpadStateDirty = true;

        return new LocalRocket(rocket, parts);
        }
        catch (System.Exception ex)
        {
            // 任何异常都不允许留下半成品火箭进世界
            UnityEngine.Debug.LogError($"[SFS-MP] 火箭 GlobalId={globalId} 创建失败：{ex}");
            if (rocket != null && rocket.gameObject != null) UnityEngine.Object.Destroy(rocket.gameObject);
            return null;
        }
    }

    public static void DestroyLocalRocket(int id)
    {
        if (syncedRockets.TryGetValue(id, out var value) && value.rocket != null)
        {
            // 这里不能无条件覆写原因：调用方（OnPacket_DestroyRocket）刚把远端原因写进
            // TrueDestructionReason，覆写会让远端销毁原因永远是"玩家主动销毁"（破坏/爆炸表现不一致）。
            // 改成用完即复位。
            RocketManager.DestroyRocket(value.rocket, (DestructionReason)4);
            TrueDestructionReason = DestructionReason.Intentional;
        }
        syncedRockets.Remove(id);
        lastRocketStateSend.Remove(id);
        lastRocketInputSend.Remove(id);
        launchpadStateDirty = true;
    }

    // 复用 partIdsScratch 收集部件 id（调用方立即把集合交给发包对象，因此返回一个新集合是必要的，
    // 但这里至少去掉了 4 段 LINQ 的迭代器与中间集合分配）。
    private static HashSet<int> BuildPartIdsScratch(ResourceModule resourceModule, LocalRocket localRocket)
    {
        var set = new HashSet<int>();
        var children = resourceModule.children;
        if (children != null)
        {
            for (int i = 0; i < children.Count; i++)
            {
                Part part = children[i] != null ? children[i].GetComponentInParent<Part>() : null;
                if (part == null) continue;
                int id = localRocket.GetPartID(part);
                if (id >= 0) set.Add(id);
            }
        }
        return set;
    }

    public static void OnLoadWorld()
    {
        unsyncedRockets.Clear();
        // 世界已就绪：关掉加入流程的加载屏（与发射同款兜底，避免永久遮挡）
        ResetPrefabLookupCooldown();
        CloseWorldLoadingScreen();
        foreach (KeyValuePair<int, RocketState> rocket in ClientManager.world.rockets)
        {
            // 已有活实例就别销毁重建：无条件重建会在每次进入世界/重连时把全世界火箭整枚重新
            // Instantiate（日志里 GlobalId=-1 连发 9~10 条即此来源），造成数百毫秒卡顿、巨量分配
            //（每秒 GC），并把没真正销毁的旧实例留成继续每帧吃物理的孤儿。
            if (syncedRockets.TryGetValue(rocket.Key, out LocalRocket existing)
                && existing != null && existing.rocket != null)
            {
                continue;
            }
            DestroyLocalRocket(rocket.Key);
            // 加载期 prefab 可能尚未就绪（进入世界的瞬间 RocketManager 还不存在，解析失败会写 1 秒冷却）。
            // 这里绝不能抛异常：异常发生在场景加载回调栈里、TryConnect 的 try 之外 ——
            // 会让后续初始化（相机/地图/玩家/其余火箭）全部跳过，而加载屏没人关，
            // 表现就是"进入世界被永久遮挡 + 该同步的火箭缺失"。改为排队重试。
            LocalRocket value = SpawnLocalRocket(rocket.Value);
            if (value == null)
            {
                UnityEngine.Debug.Log($"[SFS-MP] OnLoadWorld: prefab 未就绪，GlobalId={rocket.Key} 排队重试");
                pendingCreateRockets[rocket.Key] = new Packet_CreateRocket
                {
                    WorldTime = ClientManager.world.WorldTime,
                    GlobalId = rocket.Key,
                    Rocket = rocket.Value
                };
                continue;
            }
            syncedRockets[rocket.Key] = value;   // 索引器：上一会话残留同 id 时 Add 会抛重复键
        }
        launchpadStateDirty = true;
    }

    public static Location ToVanillaLocation(this NetLocation loc)
    {
        return new Location(loc.address.GetPlanet(), loc.position, loc.velocity);
    }

    public static NetLocation ToNetLocation(this Location loc)
    {
        return new NetLocation(loc.position, loc.velocity, loc.planet.codeName);
    }

    public static void RequestControlForLocalRocket(int localId)
    {
        pendingControlLocalIds.Add(localId);
        unsyncedToControl = localId;
    }

    public static bool ConsumePendingControlLocalId(int localId)
    {
        bool pending = pendingControlLocalIds.Remove(localId);
        if (unsyncedToControl == localId) unsyncedToControl = -1;
        return pending;
    }

    public static void OnPacket_CreateRocket(Packet_CreateRocket packet)
    {
        if (syncedRockets.TryGetValue(packet.GlobalId, out var existing))
        {
            // 已同步的火箭：这类包是"整枚状态覆盖"（客户端请求快照后服务端重发）。
            //   - 绝不能再销毁重建：75 部件 Instantiate 一次几百毫秒，重建又让插值更陈旧、
            //     触发下次快照请求 -> 自激循环（实测 fps=1、每帧 1.5 秒），
            //     而且重建期间火箭会瞬间消失（表现成"火箭变空气"）。
            //   - 结构变化（部件被打掉）本来就由 DestroyPart 包负责，不靠这个包；
            //     所以这里【只在物体真的丢了】才重建，其余一律原地按状态刷新。
            bool needsRebuild = existing == null || existing.rocket == null || packet.Rocket == null;
            if (!needsRebuild)
            {
                // 原地刷新：把整枚状态当成一个普通状态包塞给插值器，平滑对齐（不闪、不消失）
                if (packet.Rocket.location != null)
                {
                    var refresh = new Packet_UpdateRocketPrimary
                    {
                        WorldTime = packet.WorldTime,
                        RocketId = packet.GlobalId,
                        Location = packet.Rocket.location,
                        Rotation = packet.Rocket.rotation,
                        AngularVelocity = packet.Rocket.angularVelocity
                    };
                    Interpolator.AddPacketToQueue(refresh, packet.GlobalId, packet.WorldTime);
                }
                // 重发回执可能落到这条路（火箭已由广播建立）：控制登记必须在这里也兑现，
                // 否则"发射后自动接管"永远等不到（Path B 命中不了），登记会变成死 id。
                if (packet.LocalId >= 0 && ConsumePendingControlLocalId(packet.LocalId))
                {
                    ClientManager.RequestPlayerControl(packet.GlobalId, ControlRequestOrigin.NativeSelection);
                }
                return;
            }

            DestroyLocalRocket(packet.GlobalId);
            LocalRocket localRocket = SpawnLocalRocket(packet.Rocket, packet.GlobalId);
            if (localRocket == null)
            {
                UnityEngine.Debug.Log($"[SFS-MP] OnPacket_CreateRocket (synced exists): SpawnLocalRocket 返回 null，缓存重试 GlobalId={packet.GlobalId}（第 {(pendingCreateAttempts.TryGetValue(packet.GlobalId, out var __attempts) ? __attempts : 0)} 次）");
                if (pendingCreateRockets.Count >= MaxPendingCreates)
                {
                    var oldestKey = pendingCreateRockets.Keys.First();
                    pendingCreateRockets.Remove(oldestKey);
                }
                pendingCreateRockets[packet.GlobalId] = packet;
                return;
            }
            UnityEngine.Debug.Log($"[SFS-MP] OnPacket_CreateRocket (synced exists): 成功建出 LocalRocket GlobalId={packet.GlobalId}");
            syncedRockets.Add(packet.GlobalId, localRocket);
            if (localRocket.interpolator != null)
            {
                localRocket.interpolator.updateBuffer.Clear();
                localRocket.interpolator.packetBuffer.Clear();
                localRocket.interpolator.currentUpdate = localRocket.rocket.ToUpdatePacketPrimary(packet.GlobalId);
                localRocket.interpolator.currentUpdate.WorldTime = 0.0; // 清零初始基准，避免重建瞬间 WorldTime 偏高丢弃后续包
                localRocket.interpolator.isNewlyCreated = true;
            }
            if (Player != null && (int)Player.controlledRocket == packet.GlobalId)
            {
                ClientManager.ApplyConfirmedLocalPlayerControl(packet.GlobalId);
            }
            launchpadStateDirty = true;
            return;
        }
        DestroyLocalRocket(packet.GlobalId);
        if (unsyncedRockets.TryGetValue(packet.LocalId, out var value2))
        {
            unsyncedRockets.Remove(packet.LocalId);
            syncedRockets[packet.GlobalId] = value2;   // 索引器：残留同 id 时 Add 会抛重复键
            // 绑定成功必须做与 Path A/C 相同的初始化：否则 currentUpdate 恒为 null，
            // Interpolator.Update() 每帧直接 return —— 这枚火箭再也不会被任何远端包驱动。
            PrimeRocketInterpolator(value2, packet);
            launchpadStateDirty = true;
            if (packet.ForLaunch)
            {
                // 发射回执到达：关掉"Sending launch request to server..."加载屏。
                // 上游只 Open 不 Close，屏会一直盖着，看起来就像火箭没造出来。
                CloseLaunchLoadingScreen();
            }
            // 回执到了就撤销重发登记
            pendingLaunches.Remove(packet.LocalId);
            if (packet.LocalId >= 0 && ConsumePendingControlLocalId(packet.LocalId))
            {
                ClientManager.RequestPlayerControl(packet.GlobalId, ControlRequestOrigin.NativeSelection);
            }
        }
        else
        {
            // 不再依赖 GameManager.main 判空：联机进入/场景切换间隙 GameManager.main 可能为 null，
            // 一旦此处被跳过，LocalRocket 永远建不出，导致对方火箭"完全不动"。无条件建 LocalRocket 才能兜住。
            LocalRocket localRocket2 = SpawnLocalRocket(packet.Rocket, packet.GlobalId);
            if (localRocket2 == null)
            {
                UnityEngine.Debug.Log($"[SFS-MP] OnPacket_CreateRocket (new): prefab 未就绪，缓存 GlobalId={packet.GlobalId}");
                if (pendingCreateRockets.Count >= MaxPendingCreates)
                {
                    var oldestKey = pendingCreateRockets.Keys.First();
                    pendingCreateRockets.Remove(oldestKey);
                }
                pendingCreateRockets[packet.GlobalId] = packet;
                return;
            }
            UnityEngine.Debug.Log($"[SFS-MP] OnPacket_CreateRocket (new): 成功建出 LocalRocket GlobalId={packet.GlobalId}");
            syncedRockets[packet.GlobalId] = localRocket2;   // 索引器：残留同 id 时 Add 会抛重复键
            // 控制登记在这里也要兑现（Path B 可能因为 unsynced 条目已被清理而命中不了）
            if (packet.LocalId >= 0 && ConsumePendingControlLocalId(packet.LocalId))
            {
                ClientManager.RequestPlayerControl(packet.GlobalId, ControlRequestOrigin.NativeSelection);
            }
            if (localRocket2.interpolator != null)
            {
                localRocket2.interpolator.updateBuffer.Clear();
                localRocket2.interpolator.packetBuffer.Clear();
                localRocket2.interpolator.currentUpdate = localRocket2.rocket.ToUpdatePacketPrimary(packet.GlobalId);
                localRocket2.interpolator.currentUpdate.WorldTime = 0.0; // 清零初始基准，避免建立瞬间的 WorldTime 偏高把所有后续包丢弃导致冻结
                localRocket2.rocket.physics.SetLocationAndState(packet.Rocket.location.ToVanillaLocation(), physicsMode: true);
                localRocket2.rocket.rb2d.transform.eulerAngles = new Vector3(0f, 0f, packet.Rocket.rotation);
                localRocket2.rocket.rb2d.angularVelocity = packet.Rocket.angularVelocity;
            }
            LocalPlayer localPlayer = Player;
            if (localPlayer != null && (int)localPlayer.controlledRocket == packet.GlobalId)
            {
                ClientManager.ApplyConfirmedLocalPlayerControl(packet.GlobalId);
            }
            launchpadStateDirty = true;
        }
    }
}

} // namespace MultiplayerSFS.Mod
