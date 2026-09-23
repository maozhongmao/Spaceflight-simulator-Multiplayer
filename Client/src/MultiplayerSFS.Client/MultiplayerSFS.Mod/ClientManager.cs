// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Lidgren.Network;
using MultiplayerSFS.Common;
using SFS;
using SFS.UI;
using SFS.Variables;
using SFS.World;
using SFS.WorldBase;
using UnityEngine;

namespace MultiplayerSFS.Mod
{
    public static class ClientManager
    {
        public static Bool_Local multiplayerEnabled = new Bool_Local() { Value = false };
        public static TcpClientTransport client;
        public static WorldState world;
        public static int playerId;
        private static readonly ControlOwnershipCoordinator controlOwnership = new ControlOwnershipCoordinator();

        // 断线重连：上次的连接信息 + 服务端发的 resume 凭据（服务端支持按 token 恢复身份）
        private static JoinInfo lastJoinInfo;
        private static string resumeToken = string.Empty;
        private static int resumePlayerId = -1;
        private static bool reconnecting;
        // 防重连风暴：刚重连成功就又掉线时（服务端 supersede / 隧道抖动会连发 Disconnect），先冷静一会儿再动
        private static float lastReconnectSuccessTime = float.NegativeInfinity;

        public static async Task TryConnect(JoinInfo info)
        {
            Disconnect("Re-attempting join request");
            lastJoinInfo = info;
            Menu.loading.Open("Waiting for server response...");
            try
            {
                client = new TcpClientTransport();
                Packet_JoinResponse response = await client.ConnectAsync(info.address, info.port, new Packet_JoinRequest
                {
                    Username = info.username,
                    Password = info.password,
                    SolarSystemName = string.Empty
                });
                Menu.loading.Close();
                resumePlayerId = response.PlayerId;
                resumeToken = response.ResumeToken;
                LoadWorld(response);
            }
            catch (Exception ex)
            {
                Menu.loading.Close();
                MsgDrawer.main?.Log(ex.Message);
                Disconnect("Connection failed");
            }
        }

        // 断线自动重连。玩家没主动退出的情况下一直重试（1s/2s/3s/5s…），
        // 成功后带 resume token 恢复身份、刷新世界基准，继续留在当前场景里飞。
        public static void BeginReconnect(string reason)
        {
            if (reconnecting) return;
            if (UnityEngine.Time.unscaledTime - lastReconnectSuccessTime < 5f)
            {
                // 刚重连成功又被踢：立刻再连只会和服务端"supersede"互相踩（板上日志实证会形成风暴）
                UnityEngine.Debug.Log($"[SFS-MP] reconnect skipped (cooldown) reason={reason}");
                return;
            }
            UnityEngine.Debug.Log($"[SFS-MP] BeginReconnect reason={reason}");
            if (lastJoinInfo == null || lastJoinInfo.address == null)
            {
                // 没有可用的连接信息（例如从没成功连过）：退回旧行为
                SceneLoader.ExitToMainMenu();
                Disconnect("Disconnected by server");
                return;
            }
            reconnecting = true;
            _ = ReconnectLoop(reason);
        }

        private static async Task ReconnectLoop(string reason)
        {
            const int giveUpAfterAttempts = 30;   // 约两分钟
            try
            {
                for (int attempt = 1; attempt <= giveUpAfterAttempts; attempt++)
                {
                    MsgDrawer.main?.Log($"Connection lost ({reason}) — reconnecting… #{attempt}");
                    UnityEngine.Debug.Log($"[SFS-MP] reconnect attempt #{attempt} (reason={reason})");
                    double waitSeconds = attempt <= 3 ? attempt : 5;
                    await Task.Delay(TimeSpan.FromSeconds(waitSeconds));
                    if (!reconnecting) return;

                    try
                    {
                        Disconnect("Reconnecting");
                        var transport = new TcpClientTransport();
                        Packet_JoinResponse response = await transport.ConnectAsync(
                            lastJoinInfo.address, lastJoinInfo.port, new Packet_JoinRequest
                            {
                                Username = lastJoinInfo.username,
                                Password = lastJoinInfo.password,
                                ResumePlayerId = resumePlayerId,
                                ResumeToken = resumeToken,
                            });
                        client = transport;
                        lastReconnectSuccessTime = UnityEngine.Time.unscaledTime;
                        UnityEngine.Debug.Log($"[SFS-MP] reconnected ok (attempt {attempt})");
                        playerId = response.PlayerId;
                        resumePlayerId = response.PlayerId;
                        resumeToken = response.ResumeToken;
                        LocalManager.updateRocketsPeriod = response.UpdateRocketsPeriod;
                        // 只刷新世界时间基准，不重进场景：玩家还在飞，重新加载会把人踢出世界。
                        // 服务端握手后会重发初始状态，远端火箭随之重建。
                        world = new WorldState
                        {
                            initWorldTime = response.WorldTime,
                            difficulty = response.Difficulty
                        };
                        MsgDrawer.main?.Log("Reconnected.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (attempt == giveUpAfterAttempts) MsgDrawer.main?.Log("Reconnect failed: " + ex.Message);
                    }
                }
            }
            finally
            {
                reconnecting = false;
            }
            // 认输：回到主菜单（旧行为）
            SceneLoader.ExitToMainMenu();
        }

        public static void LoadWorld(Packet_JoinResponse response)
        {
            Menu.loading.Open("Loading multiplayer world...");
            // 记录时间：LocalManager.Update 会在超时后强制关屏（上游这条路径没有配对 Close，
            // 加载中途失败会让整屏永久遮挡）。
            LocalManager.NotifyWorldLoadingStarted();
            controlOwnership.Clear();
            playerId = response.PlayerId;
            LocalManager.updateRocketsPeriod = response.UpdateRocketsPeriod;
            LocalManager.Initialize();
            ChatWindow.CreateCooldownTimer(response.ChatMessageCooldown);
            world = new WorldState
            {
                initWorldTime = response.WorldTime,
                difficulty = response.Difficulty
            };
            WorldSettings settings = new WorldSettings(
                new SolarSystemReference(""),
                new Difficulty() { difficulty = world.difficulty },
                new WorldMode(WorldMode.Mode.Sandbox) { allowQuicksaves = false },
                new WorldPlaytime(),
                new SandboxSettings.Data());
            typeof(WorldBaseManager).GetMethod("EnterWorld", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(
                Base.worldBase,
                new object[] { null, settings, (Action)Base.sceneLoader.LoadHubScene });
        }

        public static void UpdateNetwork()
        {
            if (!CanPumpNetworkPackets()) return;
            TcpFrame frame;
            while (client.TryReceive(out frame))
            {
                if (frame.Kind == TcpFrameKind.Packet)
                {
                    HandlePacket(frame.Payload, frame.PayloadBits);
                }
                else if (frame.Kind == TcpFrameKind.Disconnect)
                {
                    // 【不要直接回主菜单】服务端判死（例如 10 秒收不到包）时会发这一帧，
                    // 网络抖一下就退出世界，玩家看到的是"别人的火箭突然全冻在原地"。
                    // 改成死命重连：成功就带 resume token 恢复身份继续玩，一直失败才认输。
                    BeginReconnect("server closed the session");
                    return;
                }
            }
        }

        // The network reader can enqueue initial state before LoadWorld finishes.
        // Leave those frames queued until every packet-handler dependency exists.
        private static bool CanPumpNetworkPackets()
        {
            return CanPumpNetworkPackets(
                client != null,
                world != null,
                LocalManager.players != null,
                LocalManager.syncedRockets != null,
                LocalManager.unsyncedRockets != null,
                LocalManager.updateAuthority != null);
        }

        private static bool CanPumpNetworkPackets(
            bool hasClient,
            bool hasWorld,
            bool hasPlayers,
            bool hasSyncedRockets,
            bool hasUnsyncedRockets,
            bool hasUpdateAuthority)
        {
            return hasClient && hasWorld && hasPlayers && hasSyncedRockets &&
                hasUnsyncedRockets && hasUpdateAuthority;
        }

        public static void RequestTimeScale(double scale)
        {
            if (client != null) SendPacket(new Packet_TimeWarp { Operation = TimeWarpOperation.Request, Multiplier = scale, WorldTime = world?.WorldTime ?? 0.0 });
        }

        public static void RequestExperimentalAccess(string passphrase = "")
        {
            if (client != null) SendPacket(new Packet_ExperimentalAccess { Request = true, Passphrase = passphrase });
        }

        public static bool ExperimentalAccessGranted { get; private set; }

        // 最近一次服务端命令输出（debug 弹窗显示用）
        public static string LastServerCommandOutput { get; private set; } = string.Empty;

        // 发送一条服务端控制台命令（服务端要求已获得实验性访问权限）
        public static void RequestServerCommand(string commandLine)
        {
            if (client == null || string.IsNullOrWhiteSpace(commandLine)) return;
            LastServerCommandOutput = "(等待服务端返回...)";
            SendPacket(new Packet_ServerCommand { Command = commandLine.Trim() });
        }

        // ---- 日志上传（debug 弹窗按钮）----
        public static string LastLogUploadStatus { get; private set; } = "-";

        private const int LogTailBytes = 4 * 1024 * 1024;   // 只取尾部，控制上传体积

        public static void UploadLog(string comment)
        {
            if (client == null || !client.Connected)
            {
                LastLogUploadStatus = "未连接，无法上传";
                return;
            }
            try
            {
                string path = System.IO.Path.Combine(Application.persistentDataPath, "Player.log");
                if (!System.IO.File.Exists(path))
                {
                    LastLogUploadStatus = "找不到 Player.log";
                    return;
                }
                byte[] raw = ReadLogTail(path, LogTailBytes);
                byte[] gz;
                using (var ms = new System.IO.MemoryStream())
                {
                    using (var gzip = new System.IO.Compression.GZipStream(
                        ms, System.IO.Compression.CompressionMode.Compress, true))
                    {
                        gzip.Write(raw, 0, raw.Length);
                    }
                    gz = ms.ToArray();
                }
                SendPacket(new Packet_UploadLog
                {
                    FileName = "Player.log",
                    Comment = comment ?? string.Empty,
                    Data = gz
                });
                LastLogUploadStatus = string.Format("已上传 {0:F1} KB（原始 {1:F1} KB）",
                    gz.Length / 1024.0, raw.Length / 1024.0);
                Debug.Log($"[SFS-MP] {LastLogUploadStatus}");
            }
            catch (Exception ex)
            {
                LastLogUploadStatus = "上传失败: " + ex.Message;
                Debug.LogWarning("[SFS-MP] 日志上传失败: " + ex);
            }
        }

        // 读日志尾部（游戏正在写这个文件，用 FileShare.ReadWrite 共享读）
        private static byte[] ReadLogTail(string path, int maxBytes)
        {
            using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open,
                       System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
            {
                long length = fs.Length;
                long start = length > maxBytes ? length - maxBytes : 0;
                fs.Seek(start, System.IO.SeekOrigin.Begin);
                byte[] buffer = new byte[(int)(length - start)];
                int offset = 0;
                while (offset < buffer.Length)
                {
                    int read = fs.Read(buffer, offset, buffer.Length - offset);
                    if (read <= 0) break;
                    offset += read;
                }
                if (offset == buffer.Length) return buffer;
                byte[] trimmed = new byte[offset];
                Array.Copy(buffer, trimmed, offset);
                return trimmed;
            }
        }

        // 联机下绝不能让游戏被暂停：
        // SFS 的 ScreenManager 会把 Time.timeScale 置 0（打开带 PauseWhileOpen 的界面），
        // 而 DisablePausing 的 transpiler 删掉了原版在 Awake/OpenScreen 里的恢复赋值，
        // 它自己的恢复逻辑又只在「非联机」时执行 —— 于是联机下一次暂停就永久冻结：
        // 网络收发包、时间包全部正常，但画面/时间完全不动（Time.timeScale=0 时 deltaTime 恒为 0）。
        // 这里每帧兜底恢复，取值与游戏原版一致（WorldTime.main.TimeScale）。
        public static void EnforceRunningTimeScale()
        {
            if (!multiplayerEnabled.Value) return;
            // 游戏自己的时间轴也要兜底：WorldTime.TimeScale 直接就是 timewarpSpeed 字段，
            // 一旦为 -1，游戏会把它写进 Time.timeScale 并让 worldTime 朝负数积分
            //（现场表现：unityTimeScale=1.000 但 gameTimeScale=-1.000，世界时间不动）。
            // 联机模型是不做时间加速，所以这里把它拉回 1.0 + realtimePhysics。
            if (WorldTime.main != null && WorldTime.main.TimeScale <= 0f)
            {
                try { WorldTime.main.SetState(1.0, true, false); }
                catch (System.Exception ex) { UnityEngine.Debug.LogWarning("[SFS-MP] 恢复游戏时间轴失败: " + ex.Message); }
            }
            if (Time.timeScale > 0f) return;
            Time.timeScale = (WorldTime.main != null && WorldTime.main.TimeScale > 0f)
                ? WorldTime.main.TimeScale
                : 1f;
        }

        // 控制请求的超时重发。Pending 一旦卡住，客户端再也不会为那枚火箭发包（Request 会直接吞掉），
        // 表现就是"某枚火箭永久接不进控制权，只能退出重进"。
        private const float ControlRequestTimeoutSeconds = 2f;
        private const int ControlRequestMaxTries = 5;
        private static int controlRequestTries;
        private static float controlRequestSentTime;

        public static void RequestPlayerControl(int rocketId, ControlRequestOrigin origin = ControlRequestOrigin.NativeSelection)
        {
            if (client == null || !client.Connected) return;   // 未连接时发送是静默丢弃，先挡住
            if (!controlOwnership.Request(rocketId, origin)) return;
            controlRequestTries = 1;
            controlRequestSentTime = Time.unscaledTime;
            SendPacket(new Packet_UpdatePlayerControl { PlayerId = playerId, RocketId = rocketId });
        }

        // 由 LocalManager.Update 每帧调用：Pending 超过 2 秒没回执就重发（首包容易丢）
        public static void RetryPendingControlRequest()
        {
            if (client == null || !client.Connected) return;
            int pending = controlOwnership.PendingRocketId;
            if (pending < 0) return;
            if (Time.unscaledTime - controlRequestSentTime < ControlRequestTimeoutSeconds) return;
            if (controlRequestTries >= ControlRequestMaxTries)
            {
                Debug.LogWarning($"[SFS-MP] 控制请求重发 {controlRequestTries} 次仍无回执，放弃（rocketId={pending}）");
                controlOwnership.ClearPending();   // 清掉，避免永久卡死导致该火箭再也点不进
                return;
            }
            controlRequestTries++;
            controlRequestSentTime = Time.unscaledTime;
            Debug.Log($"[SFS-MP] 控制请求未收到回执，重发第 {controlRequestTries} 次（rocketId={pending}）");
            SendPacket(new Packet_UpdatePlayerControl { PlayerId = playerId, RocketId = pending });
        }

        public static void ApplyConfirmedLocalPlayerControl(int rocketId)
        {
            controlOwnership.ApplyServerConfirmation(rocketId);
            LocalPlayer localPlayer = LocalManager.Player;
            if (localPlayer != null) localPlayer.controlledRocket.Value = rocketId;

            if (PlayerController.main == null) return;
            if (rocketId < 0)
            {
                PlayerController.main.player.Value = null;
                return;
            }
            if (LocalManager.syncedRockets == null ||
                !LocalManager.syncedRockets.TryGetValue(rocketId, out LocalRocket localRocket) ||
                localRocket.rocket == null ||
                ReferenceEquals(PlayerController.main.player.Value, localRocket.rocket))
                return;

            // The native control request was deliberately deferred until the server
            // confirmed ownership. Re-enter it only after the local ownership record
            // reflects that confirmation, so the selection patch permits the switch.
            PlayerController.main.SmoothChangePlayer(localRocket.rocket);
        }

        public static void Disconnect(string reason)
        {
            controlOwnership.Clear();
            LastServerCommandOutput = string.Empty;
            if (client == null) return;
            client.Disconnect(reason);
            client.Dispose();
            client = null;
        }

        public static void Disconnect()
        {
            Disconnect("Disconnect requested");
        }

        public static void HandlePacket(byte[] data, int bitLength)
        {
            var msg = NetPayloadCodec.ToIncoming(data, bitLength);
            PacketType packetType = (PacketType) msg.ReadByte();
            if (Packet.ShouldDebug(packetType))
                Debug.Log($"Recieved packet of type {packetType}.");
            switch (packetType)
            {
                // * Player/server Info Packets
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
                case PacketType.TimeWarp:
                    TimeWarpVoteUI.Receive(msg.Read<Packet_TimeWarp>());
                    break;
                case PacketType.ExperimentalAccess:
                    {
                        var access = msg.Read<Packet_ExperimentalAccess>();
                        ExperimentalAccessGranted = access.Granted;
                        break;
                    }
                case PacketType.P2PPeerOffer:
                    P2PConnectionManager.ApplyOffer(msg.Read<Packet_P2PPeerOffer>());
                    break;
                case PacketType.ServerCommand:
                    {
                        var commandResult = msg.Read<Packet_ServerCommand>();
                        LastServerCommandOutput = string.IsNullOrEmpty(commandResult.Output)
                            ? "(服务端返回空输出)"
                            : commandResult.Output;
                        break;
                    }

                // * Rocket Packets
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

                // * Part & Staging Packets
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

                // * Invalid Packets
                case PacketType.JoinResponse:
                    Debug.LogWarning($"Recieved server info packet outside of connection attempt.");
                    break;
                case PacketType.JoinRequest:
                    Debug.LogWarning($"Recieved packet (of type {packetType}) intended for the server.");
                    break;
                default:
                    Debug.LogWarning($"Unhandled packet type ({packetType})!");
                    break;
            }
        }

        public static void SendPacket(Packet packet)
        {
            // 守卫：Disconnect 会把 client 置空，而被踢/掉线时场景卸载钩子仍会发包
            // （曾因此每次退出世界必抛 NullReferenceException）。
            if (packet == null || client == null || !client.Connected) return;
            if (Packet.ShouldDebug(packet.Type))
                Debug.Log($"Sending packet of type {packet.Type}.");

            client.Send(packet);
        }

        public static void SendPacket(Packet packet, NetDeliveryMethod _)
        {
            SendPacket(packet);
        }
        static void OnPacket_PlayerConnected(NetIncomingMessage msg)
        {
            Packet_PlayerConnected packet = msg.Read<Packet_PlayerConnected>();
            if (LocalManager.players != null)
            {
                // 用索引器而不是 Add：断线重连 / 重复广播时同一 player 会再来一次，
                // Add 会抛 ArgumentException（曾触发崩溃报告上传）
                LocalManager.players[packet.PlayerId] = new LocalPlayer(packet.Username, packet.IconColor);
            }
            if (packet.PrintMessage)
            {
                string message = $"{packet.Username} connected";
                MsgDrawer.main.Log(message);
                ChatWindow.AddMessage(new ChatMessage(message));
            }
        }

        static void OnPacket_PlayerDisconnected(NetIncomingMessage msg)
        {
            Packet_PlayerDisconnected packet = msg.Read<Packet_PlayerDisconnected>();
            if (LocalManager.players.TryGetValue(packet.PlayerId, out LocalPlayer player))
            {
                string message = $"{player.username} disconnected";
                MsgDrawer.main.Log(message);
                ChatWindow.AddMessage(new ChatMessage(message));
                LocalManager.players.Remove(packet.PlayerId);
            }
        }

        static void OnPacket_UpdatePlayerControl(NetIncomingMessage msg)
        {
            Packet_UpdatePlayerControl packet = msg.Read<Packet_UpdatePlayerControl>();
            if (LocalManager.players.TryGetValue(packet.PlayerId, out LocalPlayer player))
            {
                player.controlledRocket.Value = packet.RocketId;
            }
            else
            {
                Debug.LogError("Missing player while trying to update controlled rocket!");
            }
            if (packet.PlayerId != playerId) return;

            // 服务端用同一种包回"批准"与"拒绝"（拒绝时回的是你当前实际控制的目标）。
            // 只有与本次请求目标一致的才算批准；否则无条件确认会把自己从正在开的火箭里踢出去，
            // 而且 Confirmed 被写成服务端的值后，重发/重试会被守卫吞掉（该火箭再也点不进去）。
            int pending = controlOwnership.PendingRocketId;
            if (pending >= 0 && packet.RocketId != pending)
            {
                Debug.Log($"[SFS-MP] 控制请求被拒绝：请求={pending} 服务端仍为={packet.RocketId}");
                controlOwnership.ClearPending();
                return;
            }

            ApplyConfirmedLocalPlayerControl(packet.RocketId);
        }

        static void OnPacket_UpdatePlayerAuthority(NetIncomingMessage msg)
        {
            Packet_UpdatePlayerAuthority packet = msg.Read<Packet_UpdatePlayerAuthority>();
            LocalManager.updateAuthority = packet.RocketIds ?? new System.Collections.Generic.HashSet<int>();

            foreach (int id in LocalManager.updateAuthority)
            {
                if (LocalManager.syncedRockets.TryGetValue(id, out LocalRocket rocket))
                {

                }
            }
        }

        static void OnPacket_UpdateWorldTime(NetIncomingMessage msg)
        {
            Packet_UpdateWorldTime packet = msg.Read<Packet_UpdateWorldTime>();
            if (WorldTime.main != null)
            {
                WorldTime.main.worldTime = packet.WorldTime;
            }
        }

        static void OnPacket_UpdatePlayerColor(NetIncomingMessage msg)
        {
            Packet_UpdatePlayerColor packet = msg.Read<Packet_UpdatePlayerColor>();
            if (LocalManager.players.TryGetValue(packet.PlayerId, out LocalPlayer player))
            {
                player.iconColor = packet.Color;
                ChatWindow.OnPlayerColorChange(packet.PlayerId, packet.Color);
            }
        }

        static void OnPacket_SendChatMessage(NetIncomingMessage msg)
        {
            Packet_SendChatMessage packet = msg.Read<Packet_SendChatMessage>();
            ChatWindow.AddMessage(new ChatMessage(packet.Message, packet.SenderId));
        }

        static void OnPacket_CreateRocket(NetIncomingMessage msg)
        {
            Packet_CreateRocket packet = msg.Read<Packet_CreateRocket>();
            // 回执（LocalId >= 0）在本地已有该火箭时不要覆盖：那是客户端重发带回来的"创建那一刻"旧状态，
            // 覆盖会让火箭瞬移/贴回发射台。权威覆盖包（LocalId < 0，服务端快照/状态覆盖）照常覆盖。
            if (packet.LocalId < 0 || !world.rockets.ContainsKey(packet.GlobalId))
                world.rockets[packet.GlobalId] = packet.Rocket;
            LocalManager.OnPacket_CreateRocket(packet);
        }

        static void OnPacket_DestroyRocket(NetIncomingMessage msg)
        {
            Packet_DestroyRocket packet = msg.Read<Packet_DestroyRocket>();
            world.rockets.Remove(packet.RocketId);
            LocalManager.TrueDestructionReason = packet.Reason;
            LocalManager.DestroyLocalRocket(packet.RocketId);
        }

        static void OnPacket_UpdateRocketPrimary(NetIncomingMessage msg)
        {
            Packet_UpdateRocketPrimary packet = msg.Read<Packet_UpdateRocketPrimary>();
            if (world.rockets.TryGetValue(packet.RocketId, out RocketState state))
            {
                state.UpdateRocketPrimary(packet);
                Interpolator.AddPacketToQueue(packet, packet.RocketId, packet.WorldTime);
            }
            else
            {
                Debug.Log("Missing rocket from world state!!!");
            }
        }

        static void OnPacket_UpdateRocketSecondary(NetIncomingMessage msg)
        {
            Packet_UpdateRocketSecondary packet = msg.Read<Packet_UpdateRocketSecondary>();
            if (world.rockets.TryGetValue(packet.RocketId, out RocketState state))
            {
                state.UpdateRocketSecondary(packet);
                Interpolator.AddPacketToQueue(packet, packet.RocketId, packet.WorldTime);
            }
        }

        static void OnPacket_DestroyPart(NetIncomingMessage msg)
        {
            Packet_DestroyPart packet = msg.Read<Packet_DestroyPart>();
            if (world.rockets.TryGetValue(packet.RocketId, out RocketState state))
            {
                state.RemovePart(packet.PartId);
                Interpolator.AddPacketToQueue(packet, packet.RocketId, packet.WorldTime);
            }
        }

        static void OnPacket_UpdateStaging(NetIncomingMessage msg)
        {
            Packet_UpdateStaging packet = msg.Read<Packet_UpdateStaging>();
            if (world.rockets.TryGetValue(packet.RocketId, out RocketState state))
            {
                state.stages = packet.Stages;
                Interpolator.AddPacketToQueue(packet, packet.RocketId, packet.WorldTime);
            }
        }

        static void OnPacket_UpdatePart_EngineModule(NetIncomingMessage msg)
        {
            Packet_UpdatePart_EngineModule packet = msg.Read<Packet_UpdatePart_EngineModule>();
            if (world.rockets.TryGetValue(packet.RocketId, out RocketState rocketState))
            {
                if (rocketState.parts.TryGetValue(packet.PartId, out PartState partState))
                {
                    partState.part.TOGGLE_VARIABLES["engine_on"] = packet.EngineOn;
                }
                Interpolator.AddPacketToQueue(packet, packet.RocketId, packet.WorldTime);
            }
        }

        static void OnPacket_UpdatePart_WheelModule(NetIncomingMessage msg)
        {
            Packet_UpdatePart_WheelModule packet = msg.Read<Packet_UpdatePart_WheelModule>();
            if (world.rockets.TryGetValue(packet.RocketId, out RocketState rocketState))
            {
                if (rocketState.parts.TryGetValue(packet.PartId, out PartState partState))
                {
                    partState.part.TOGGLE_VARIABLES["wheel_on"] = packet.WheelOn;
                }
                Interpolator.AddPacketToQueue(packet, packet.RocketId, packet.WorldTime);
            }
        }

        static void OnPacket_UpdatePart_BoosterModule(NetIncomingMessage msg)
        {
            Packet_UpdatePart_BoosterModule packet = msg.Read<Packet_UpdatePart_BoosterModule>();
            if (world.rockets.TryGetValue(packet.RocketId, out RocketState rocketState))
            {
                if (rocketState.parts.TryGetValue(packet.PartId, out PartState partState))
                {
                    partState.part.NUMBER_VARIABLES["fuel_percent"] = packet.FuelPercent;
                }
                Interpolator.AddPacketToQueue(packet, packet.RocketId, packet.WorldTime);
            }
        }

        static void OnPacket_UpdatePart_ParachuteModule(NetIncomingMessage msg)
        {
            Packet_UpdatePart_ParachuteModule packet = msg.Read<Packet_UpdatePart_ParachuteModule>();
            if (world.rockets.TryGetValue(packet.RocketId, out RocketState rocketState))
            {
                if (rocketState.parts.TryGetValue(packet.PartId, out PartState partState))
                {
                    partState.part.NUMBER_VARIABLES["animation_state"] = packet.State;
                    partState.part.NUMBER_VARIABLES["deploy_state"] = packet.TargetState;
                }
                Interpolator.AddPacketToQueue(packet, packet.RocketId, packet.WorldTime);
            }
        }

        static void OnPacket_UpdatePart_MoveModule(NetIncomingMessage msg)
        {
            Packet_UpdatePart_MoveModule packet = msg.Read<Packet_UpdatePart_MoveModule>();
            if (world.rockets.TryGetValue(packet.RocketId, out RocketState rocketState))
            {
                if (rocketState.parts.TryGetValue(packet.PartId, out PartState partState))
                {
                    partState.part.NUMBER_VARIABLES["state"] = packet.Time;
                    partState.part.NUMBER_VARIABLES["state_target"] = packet.TargetTime;
                }
                Interpolator.AddPacketToQueue(packet, packet.RocketId, packet.WorldTime);
            }
        }

        static void OnPacket_UpdatePart_ResourceModule(NetIncomingMessage msg)
        {
            Packet_UpdatePart_ResourceModule packet = msg.Read<Packet_UpdatePart_ResourceModule>();
            if (world.rockets.TryGetValue(packet.RocketId, out RocketState rocketState))
            {
                foreach (int partId in packet.PartIds)
                {
                    if (rocketState.parts.TryGetValue(partId, out PartState partState))
                    {
                        // TODO! A lot of these save variable names will most likely be different for non-vanilla parts, but currently idk what the best way to properly get them is.
                        // TODO! I might need some form of register that associates a part's name and module variable names to their save variable names.
                        partState.part.NUMBER_VARIABLES["fuel_percent"] = packet.ResourcePercent;
                    }
                }
                Interpolator.AddPacketToQueue(packet, packet.RocketId, packet.WorldTime);
            }
        }

        public static void RequestRocketSnapshot(int rocketId)
        {
            if (rocketId >= 0) client?.RequestRocketSnapshot(rocketId);
        }
    }
}