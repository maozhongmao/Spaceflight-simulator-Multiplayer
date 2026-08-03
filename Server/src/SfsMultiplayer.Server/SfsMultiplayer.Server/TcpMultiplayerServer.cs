using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Lidgren.Network;
using SfsMultiplayer.Protocol;

namespace SfsMultiplayer.Server;

public sealed class TcpMultiplayerServer : IAsyncDisposable
{
    private readonly ServerSettings _settings;
    private readonly WorldSnapshot _world;
    private readonly object _worldLock = new();
    private readonly TcpListener _listener;
    private readonly UdpStateTransport _udp;
    private readonly ConcurrentDictionary<int, TcpSession> _players = new();
    private readonly Dictionary<(int KeepRocket, int RemoveRocket, int KeepPart, int RemovePart), PendingDock> _pendingDocks = new();
    private PendingTimeWarpVote? _pendingTimeWarpVote;
    private readonly ConcurrentBag<Task> _clientTasks = new();
    private readonly Stopwatch _worldClock = Stopwatch.StartNew();
    private readonly Stopwatch _saveClock = Stopwatch.StartNew();
    private readonly Stopwatch _debugClock = Stopwatch.StartNew();
    private readonly Stopwatch _heartbeatClock = Stopwatch.StartNew();

    private double _timeScale = 1;
    private int _nextPlayerId;
    private int _sequence;
    private int _nextTimeWarpVoteId;
    private bool _started;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int PlayerCount => _players.Count;
    public double TimeScale { get { lock (_worldLock) return _timeScale; } }
    public double WorldTime
    {
        get { lock (_worldLock) return _world.WorldTime + _worldClock.Elapsed.TotalSeconds * _timeScale; }
    }

    public TcpMultiplayerServer(ServerSettings settings, WorldSnapshot world)
    {
        settings.Validate(allowEphemeralPort: true);
        _settings = settings;
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _listener = new TcpListener(IPAddress.Any, settings.Port);
        _udp = new UdpStateTransport(settings.Port, HandleUdpDatagram);
    }

    public void Start()
    {
        if (_started) throw new InvalidOperationException("Server is already started.");
        _listener.Start(_settings.MaxConnections);
        _udp.Start();
        _started = true;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!_started) throw new InvalidOperationException("Start must be called before RunAsync.");
        using var registration = cancellationToken.Register(() => _listener.Stop());
        var maintenance = MaintenanceLoopAsync(cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                client.NoDelay = true;
                client.ReceiveBufferSize = 256 * 1024;
                client.SendBufferSize = 256 * 1024;
                var task = HandleClientAsync(client, cancellationToken);
                _clientTasks.Add(task);
            }
        }
        finally
        {
            try { await maintenance.ConfigureAwait(false); } catch (OperationCanceledException) { }
            foreach (var session in _players.Values) session.Close();
            try { await Task.WhenAll(_clientTasks.ToArray()).ConfigureAwait(false); } catch { }
            SaveState();
        }
    }

    private bool HandleUdpDatagram(string token, IPEndPoint endpoint, byte[] payload)
    {
        var session = _players.Values.FirstOrDefault(player => player.UdpToken == token);
        if (session is null) return false;
        session.UdpEndpoint = endpoint;
        session.LastUdpReceiveUtc = DateTime.UtcNow;
        if (payload.Length == 0) return true;
        try
        {
            lock (_worldLock) HandleData(session, new TcpFrame(TcpFrameKind.Packet, 0, payload, payload.Length * 8));
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NetException)
        {
            if (_settings.Debug) Console.WriteLine($"[UDP拒绝] {endpoint}: {ex.Message}");
            return false;
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        TcpSession? session = null;
        var handlerGeneration = -1;
        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        try
        {
            using (client)
            using (var stream = client.GetStream())
            using (var joinTimeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken))
            {
                Console.WriteLine($"[MP-CONNECT] TCP_ACCEPTED {endpoint}");
                joinTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var hello = await TcpFrameCodec.ReadAsync(stream, joinTimeout.Token).ConfigureAwait(false);
                Console.WriteLine($"[MP-CONNECT] HELLO_FRAME_RECEIVED {endpoint} kind={hello.Kind} sequence={hello.Sequence} bytes={hello.Payload.Length}");
                if (hello.Kind != TcpFrameKind.Hello)
                    throw new InvalidDataException("First TCP frame must be Hello.");
                if (hello.Sequence != SessionHandshakeCodec.Version)
                {
                    await SendDisconnectDirectAsync(stream,
                        $"Handshake mismatch: server={SessionHandshakeCodec.Version}, client={hello.Sequence}.", serverToken)
                        .ConfigureAwait(false);
                    return;
                }

                var request = SessionHandshakeCodec.DecodeHello(hello.Payload);
                var username = request.Username.Trim();
                Console.WriteLine($"[MP-CONNECT] HELLO_DECODED {endpoint} user={username}");
                var isResume = request.ResumePlayerId >= 0 &&
                    _players.TryGetValue(request.ResumePlayerId, out var resumableSession) &&
                    resumableSession.CanResume(request.ResumeToken);
                var denial = ValidateJoin(username, request.Password, isResume);
                if (denial is not null)
                {
                    await SendDisconnectDirectAsync(stream, denial, serverToken).ConfigureAwait(false);
                    return;
                }

                if (request.ResumePlayerId >= 0 &&
                    _players.TryGetValue(request.ResumePlayerId, out var existing) &&
                    existing.CanResume(request.ResumeToken))
                {
                    session = existing;
                    session.ReplaceConnection(client, stream);
                    Console.WriteLine($"[TCP恢复] {username} @ {endpoint}");
                }
                else
                {
                    session = new TcpSession(
                        Interlocked.Increment(ref _nextPlayerId), username, RandomColor(), client, stream);
                    if (!_players.TryAdd(session.Id, session))
                        throw new InvalidOperationException("Could not register TCP player.");
                }

                var responsePayload = SessionHandshakeCodec.EncodeAck(new JoinResponsePacket
                {
                    PlayerId = session.Id,
                    UpdateRocketsPeriod = 50,
                    ChatMessageCooldown = _settings.ChatMessageCooldown,
                    WorldTime = WorldTime,
                    SendTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                    Difficulty = _world.Difficulty,
                    SolarSystemName = string.Empty,
                    UdpSessionToken = session.UdpToken,
                    ResumeToken = session.ResumeToken,
                });
                await TcpFrameCodec.WriteAsync(stream,
                    new TcpFrame(TcpFrameKind.HelloAck, SessionHandshakeCodec.Version,
                        responsePayload, responsePayload.Length * 8), serverToken).ConfigureAwait(false);
                Console.WriteLine($"[MP-CONNECT] HELLO_ACK_SENT {endpoint} player={session.Id} bytes={responsePayload.Length}");

                Console.WriteLine($"[TCP连接] {username} @ {endpoint}");
                var connectionGeneration = session.ConnectionGeneration;
                handlerGeneration = connectionGeneration;
                var writer = WriterLoopAsync(session, connectionGeneration, serverToken);
                lock (_worldLock)
                {
                    SendInitialState(session);
                    RefreshAuthorities();
                }

                while (!serverToken.IsCancellationRequested && client.Connected && session.ConnectionGeneration == connectionGeneration)
                {
                    var frame = await TcpFrameCodec.ReadAsync(stream, serverToken).ConfigureAwait(false);
                    session.LastReceiveUtc = DateTime.UtcNow;
                    session.ReceivedBytes += frame.Payload.Length + 13;
                    session.ReceivedFrames++;
                    switch (frame.Kind)
                    {
                        case TcpFrameKind.Ping:
                            EnqueueCritical(session, new TcpFrame(TcpFrameKind.Pong, frame.Sequence,
                                frame.Payload, frame.PayloadBits));
                            break;
                        case TcpFrameKind.Pong:
                            HandlePong(session, frame);
                            break;
                        case TcpFrameKind.Packet:
                            lock (_worldLock) HandleData(session, frame);
                            break;
                        case TcpFrameKind.RequestWorldSnapshot:
                            lock (_worldLock) SendWorldSnapshot(session);
                            break;
                        case TcpFrameKind.RequestRocketSnapshot:
                            if (frame.Payload.Length >= 4)
                                lock (_worldLock) SendRocketSnapshot(session, BitConverter.ToInt32(frame.Payload, 0));
                            break;
                        case TcpFrameKind.Disconnect:
                            return;
                        default:
                            throw new InvalidDataException($"Unexpected TCP frame: {frame.Kind}.");
                    }
                }
                session.Close();
                try { await writer.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
        {
        }
        catch (EndOfStreamException)
        {
        }
        catch (IOException ex)
        {
            if (_settings.Debug) Console.WriteLine($"[TCP网络] {endpoint}: {ex.Message}");
        }
        catch (SocketException ex)
        {
            if (_settings.Debug) Console.WriteLine($"[TCP网络] {endpoint}: {ex.Message}");
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or NetException)
        {
            Console.WriteLine($"[拒绝TCP数据] {endpoint}: {ex.Message}");
            if (session is not null)
                EnqueueCritical(session, DisconnectFrame("Invalid packet."));
        }
        finally
        {
            if (session is not null && session.ConnectionGeneration == handlerGeneration)
            {
                if (session.EnterRecoveryWindow()) { }
                else if (_players.TryRemove(session.Id, out _))
                {
                    session.Close();
                    lock (_worldLock)
                    {
                        if (_pendingTimeWarpVote?.RequiredPlayerIds.Contains(session.Id) == true)
                            CancelTimeWarpVote($"{session.Username} 已离线，投票取消。");
                        Broadcast(PacketType.PlayerDisconnected,
                            new PlayerDisconnectedPacket { PlayerId = session.Id }, session);
                        RefreshAuthorities();
                    }
                    Console.WriteLine($"[TCP断开] {session.Username}");
                }
            }
        }
    }

    private async Task WriterLoopAsync(TcpSession session, int connectionGeneration, CancellationToken serverToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken, session.Closed.Token);
        while (!linked.IsCancellationRequested && session.ConnectionGeneration == connectionGeneration)
        {
            if (!session.Queue.TryDequeue(out var frame) || frame is null)
            {
                await session.SendSignal.WaitAsync(TimeSpan.FromMilliseconds(100), linked.Token).ConfigureAwait(false);
                continue;
            }
            await TcpFrameCodec.WriteAsync(session.Stream, frame, linked.Token).ConfigureAwait(false);
            session.SentBytes += frame.Payload.Length + 13;
            session.SentFrames++;
        }
    }

    private async Task MaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_heartbeatClock.Elapsed >= TimeSpan.FromSeconds(2))
            {
                _heartbeatClock.Restart();
                var now = DateTime.UtcNow;
                foreach (var session in _players.Values)
                {
                    if (now - session.LastReceiveUtc > TimeSpan.FromSeconds(10))
                    {
                        if (session.EnterRecoveryWindow())
                        {
                            continue;
                        }
                        if (_players.TryRemove(session.Id, out _))
                        {
                            session.Close();
                            lock (_worldLock)
                            {
                                if (_pendingTimeWarpVote?.RequiredPlayerIds.Contains(session.Id) == true)
                                    CancelTimeWarpVote($"{session.Username} 已离线，投票取消。");
                                Broadcast(PacketType.PlayerDisconnected,
                                    new PlayerDisconnectedPacket { PlayerId = session.Id }, session);
                                RefreshAuthorities();
                            }
                            Console.WriteLine($"{session.Username} 已断开。");
                        }
                        continue;
                    }
                    var ticks = now.Ticks;
                    session.LastPingTicks = ticks;
                    var bytes = BitConverter.GetBytes(ticks);
                    EnqueueCritical(session, new TcpFrame(TcpFrameKind.Ping,
                        Interlocked.Increment(ref _sequence), bytes, bytes.Length * 8));
                }
            }
            SaveIfDue();
            lock (_worldLock)
            {
                if (_pendingTimeWarpVote is not null && DateTime.UtcNow >= _pendingTimeWarpVote.ExpiresUtc)
                    CancelTimeWarpVote("投票已超时，时间倍率保持不变。");
            }
            if (_settings.Debug && _debugClock.Elapsed >= TimeSpan.FromSeconds(5))
            {
                _debugClock.Restart();
                PrintDebugSummary();
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private void HandlePong(TcpSession session, TcpFrame frame)
    {
        if (frame.Payload.Length < 8) return;
        var sentTicks = BitConverter.ToInt64(frame.Payload, 0);
        var rtt = TimeSpan.FromTicks(Math.Max(0, DateTime.UtcNow.Ticks - sentTicks)).TotalMilliseconds;
        session.JitterMs = session.RoundTripMs <= 0 ? 0 : session.JitterMs * 0.8 + Math.Abs(rtt - session.RoundTripMs) * 0.2;
        session.RoundTripMs = rtt;
        Send(session, PacketType.UpdateWorldTime,
            new UpdateWorldTimePacket { WorldTime = WorldTime + rtt / 2000.0 * TimeScale });
    }

    private string? ValidateJoin(string username, string password, bool isResume)
    {
        if (!isResume && _players.Count >= _settings.MaxConnections) return "Server is full.";
        if (username.Length == 0 || username.Length > _settings.MaxUsernameLength || username.Any(char.IsControl))
            return "Invalid username.";
        if (!isResume && _settings.BlockDuplicatePlayerNames && _players.Values.Any(
                player => string.Equals(player.Username, username, StringComparison.OrdinalIgnoreCase)))
            return "Username is already in use.";
        return PasswordMatches(password, _settings.Password) ? null : "Invalid password.";
    }

    private void SendInitialState(TcpSession joining)
    {
        foreach (var player in _players.Values.OrderBy(player => player.Id))
        {
            Send(joining, PacketType.PlayerConnected, new PlayerConnectedPacket
            {
                PlayerId = player.Id,
                Username = player.Username,
                IconColor = player.Color,
                PrintMessage = false,
            });
            Send(joining, PacketType.UpdatePlayerControl, new UpdatePlayerControlPacket
            {
                PlayerId = player.Id,
                RocketId = player.ControlledRocket,
            });
        }
        SendWorldSnapshot(joining);
        Send(joining, PacketType.TimeWarp, new TimeWarpPacket
        {
            Operation = TimeWarpOperation.Applied,
            Multiplier = _timeScale,
            WorldTime = WorldTime,
        });

        Broadcast(PacketType.PlayerConnected, new PlayerConnectedPacket
        {
            PlayerId = joining.Id,
            Username = joining.Username,
            IconColor = joining.Color,
            PrintMessage = true,
        }, joining);
    }

    private void SendWorldSnapshot(TcpSession session)
    {
        foreach (var rocket in _world.Rockets.OrderBy(pair => pair.Key))
            SendRocketSnapshot(session, rocket.Key);
    }

    private void SendRocketSnapshot(TcpSession session, int rocketId)
    {
        if (!_world.Rockets.TryGetValue(rocketId, out var rocket)) return;
        Send(session, PacketType.CreateRocket, new CreateRocketPacket
        {
            WorldTime = WorldTime,
            GlobalId = rocketId,
            Rocket = rocket,
        });
    }

    private void HandleData(TcpSession player, TcpFrame frame)
    {
        var message = NetPayloadCodec.ToIncoming(frame.Payload, frame.PayloadBits);
        var rawType = message.ReadByte();
        if (!Enum.IsDefined(typeof(PacketType), rawType))
            throw new InvalidDataException($"Unknown packet type: {rawType}.");
        var type = (PacketType)rawType;
        player.PacketCounts.AddOrUpdate(type, 1, (_, count) => count + 1);
        switch (type)
        {
            case PacketType.UpdatePlayerControl: HandlePlayerControl(message, player); break;
            case PacketType.UpdatePlayerColor: HandlePlayerColor(message, player); break;
            case PacketType.SendChatMessage: HandleChat(message, player); break;
            case PacketType.CreateRocket: HandleCreateRocket(message, player); break;
            case PacketType.DestroyRocket: HandleDestroyRocket(message, player); break;
            case PacketType.UpdateRocketPrimary: HandleRocketPrimary(message, player); break;
            case PacketType.UpdateRocketSecondary: HandleRocketSecondary(message, player); break;
            case PacketType.DestroyPart: HandleDestroyPart(message, player); break;
            case PacketType.UpdateStaging: HandleStaging(message, player); break;
            case PacketType.UpdatePart_EngineModule: HandleEngine(message, player); break;
            case PacketType.UpdatePart_WheelModule: HandleWheel(message, player); break;
            case PacketType.UpdatePart_BoosterModule: HandleBooster(message, player); break;
            case PacketType.UpdatePart_ParachuteModule: HandleParachute(message, player); break;
            case PacketType.UpdatePart_MoveModule: HandleMove(message, player); break;
            case PacketType.UpdatePart_ResourceModule: HandleResource(message, player); break;
            case PacketType.DockTransaction: HandleDockTransaction(message, player); break;
            case PacketType.TimeWarp: HandleTimeWarp(message, player); break;
            default: throw new InvalidDataException($"Packet {type} is server-only or invalid after joining.");
        }
    }

    private void HandleTimeWarp(NetIncomingMessage message, TcpSession player)
    {
        var packet = Read<TimeWarpPacket>(message);
        if (packet.Operation != TimeWarpOperation.Request)
            throw new InvalidDataException("Invalid client time-warp operation.");

        var controllingPlayers = _players.Values.Count(session => session.ControlledRocket != -1);
        if (!TimeWarpControlRules.CanSet(controllingPlayers, packet.Multiplier))
        {
            SendTimeWarpNotice(player, "当前无法设置该时间倍率。");
            return;
        }

        SetTimeScale(packet.Multiplier, string.Empty);
    }

    private void StartTimeWarpVote(TcpSession requester, double multiplier)
    {
        if (!IsAllowedTimeScale(multiplier))
        {
            SendTimeWarpNotice(requester, "允许的时间倍率: 1 到 2500");
            return;
        }
        if (_pendingTimeWarpVote is not null)
        {
            SendTimeWarpNotice(requester, "当前已有一项时间加速投票正在进行。");
            return;
        }

        var required = _players.Keys.ToHashSet();
        var vote = new PendingTimeWarpVote(
            Interlocked.Increment(ref _nextTimeWarpVoteId), requester.Id, requester.Username,
            multiplier, required, DateTime.UtcNow.AddSeconds(30));
        vote.ApprovedPlayerIds.Add(requester.Id);
        _pendingTimeWarpVote = vote;
        Broadcast(PacketType.TimeWarp, new TimeWarpPacket
        {
            Operation = TimeWarpOperation.Vote,
            VoteId = vote.Id,
            RequesterId = requester.Id,
            RequesterName = requester.Username,
            Multiplier = multiplier,
            WorldTime = WorldTime,
            TimeoutSeconds = 30,
            Message = $"{requester.Username} 申请将时间倍率设为 {multiplier:0.##}x。",
        });
        Console.WriteLine($"[时间投票] {requester.Username} 申请 {multiplier:0.##}x，等待 {required.Count} 名玩家一致同意。");
        if (vote.RequiredPlayerIds.SetEquals(vote.ApprovedPlayerIds)) CompleteTimeWarpVote();
    }

    private void RegisterTimeWarpVote(TcpSession player, int voteId, bool approved)
    {
        var vote = _pendingTimeWarpVote;
        if (vote is null || vote.Id != voteId || !vote.RequiredPlayerIds.Contains(player.Id)) return;
        if (!approved)
        {
            CancelTimeWarpVote($"{player.Username} 拒绝了投票，时间倍率保持不变。");
            return;
        }
        vote.ApprovedPlayerIds.Add(player.Id);
        Console.WriteLine($"[时间投票] {player.Username} 已同意 ({vote.ApprovedPlayerIds.Count}/{vote.RequiredPlayerIds.Count})。");
        if (vote.RequiredPlayerIds.SetEquals(vote.ApprovedPlayerIds)) CompleteTimeWarpVote();
    }

    private void CompleteTimeWarpVote()
    {
        var vote = _pendingTimeWarpVote;
        if (vote is null) return;
        _pendingTimeWarpVote = null;
        SetTimeScale(vote.Multiplier, "全员投票通过", vote.Id);
        Console.WriteLine($"[时间投票] 全员通过，时间倍率设为 {vote.Multiplier:0.##}x。");
    }

    private void CancelTimeWarpVote(string reason)
    {
        var vote = _pendingTimeWarpVote;
        if (vote is null) return;
        _pendingTimeWarpVote = null;
        Broadcast(PacketType.TimeWarp, new TimeWarpPacket
        {
            Operation = TimeWarpOperation.Cancelled,
            VoteId = vote.Id,
            Multiplier = _timeScale,
            WorldTime = WorldTime,
            Message = reason,
        });
        Console.WriteLine("[时间投票] " + reason);
    }

    private void SendTimeWarpNotice(TcpSession player, string message)
    {
        Send(player, PacketType.TimeWarp, new TimeWarpPacket
        {
            Operation = TimeWarpOperation.Notice,
            Multiplier = _timeScale,
            WorldTime = WorldTime,
            Message = message,
        });
    }

    private void HandlePlayerControl(NetIncomingMessage message, TcpSession player)
    {
        var packet = Read<UpdatePlayerControlPacket>(message);
        if (packet.RocketId != -1)
        {
            if (!_world.Rockets.ContainsKey(packet.RocketId)) return;
            if (_players.Values.Any(other => other.Id != player.Id && other.ControlledRocket == packet.RocketId)) return;
        }
        packet.PlayerId = player.Id;
        player.ControlledRocket = packet.RocketId;
        RefreshAuthorities();
        EnforceTimeScaleControlRule();
        Broadcast(PacketType.UpdatePlayerControl, packet, player);
    }

    private void HandlePlayerColor(NetIncomingMessage message, TcpSession player)
    {
        var packet = Read<UpdatePlayerColorPacket>(message);
        packet.PlayerId = player.Id;
        packet.Color = ClampColor(packet.Color);
        player.Color = packet.Color;
        Broadcast(PacketType.UpdatePlayerColor, packet, player);
    }

    private void HandleChat(NetIncomingMessage message, TcpSession player)
    {
        var packet = Read<SendChatMessagePacket>(message);
        var text = packet.Message.Trim();
        if (text.Length == 0 || text.Length > _settings.MaxChatMessageLength || text.Any(char.IsControl)) return;
        var now = DateTime.UtcNow;
        if ((now - player.LastChatUtc).TotalSeconds < _settings.ChatMessageCooldown) return;
        player.LastChatUtc = now;
        packet.SenderId = player.Id;
        packet.Message = text;
        Broadcast(PacketType.SendChatMessage, packet, player);
    }

    private void HandleCreateRocket(NetIncomingMessage message, TcpSession player)
    {
        var packet = Read<CreateRocketPacket>(message);
        ValidateRocket(packet.Rocket);
        if (packet.GlobalId >= 0 && _world.Rockets.ContainsKey(packet.GlobalId))
        {
            if (!CanUpdate(player, packet.GlobalId)) return;
            _world.Rockets[packet.GlobalId] = packet.Rocket;
            packet.WorldTime = WorldTime;
            Broadcast(PacketType.CreateRocket, packet, player);
            return;
        }
        packet.GlobalId = NextRocketId();
        packet.WorldTime = WorldTime;
        _world.Rockets.Add(packet.GlobalId, packet.Rocket);
        player.UpdateAuthority.Add(packet.GlobalId);
        Broadcast(PacketType.CreateRocket, packet);
        if (!packet.ForLaunch) RefreshAuthorities();
    }

    private void HandleDestroyRocket(NetIncomingMessage message, TcpSession player)
    {
        var packet = Read<DestroyRocketPacket>(message);
        if (!CanUpdate(player, packet.RocketId) || !_world.Rockets.Remove(packet.RocketId)) return;
        packet.WorldTime = WorldTime;
        foreach (var connected in _players.Values)
        {
            if (connected.ControlledRocket == packet.RocketId) connected.ControlledRocket = -1;
            connected.UpdateAuthority.Remove(packet.RocketId);
        }
        Broadcast(PacketType.DestroyRocket, packet, player);
        RefreshAuthorities();
    }

    private void HandleRocketPrimary(NetIncomingMessage message, TcpSession player)
    {
        var packet = Read<UpdateRocketPrimaryPacket>(message);
        if (!TryAuthorizedRocket(player, packet.RocketId, out var rocket)) return;
        ValidateFinite(packet.Location, packet.Rotation, packet.AngularVelocity);
        RocketLatencyCompensation.Advance(packet, player.RoundTripMs);
        packet.WorldTime = WorldTime;
        rocket.Apply(packet);
        BroadcastLatest(PacketType.UpdateRocketPrimary, packet, packet.RocketId, player);
    }

    private void HandleRocketSecondary(NetIncomingMessage message, TcpSession player)
    {
        var packet = Read<UpdateRocketSecondaryPacket>(message);
        if (!TryAuthorizedRocket(player, packet.RocketId, out var rocket)) return;
        if (!AllFinite(packet.InputTurn, packet.RawX, packet.RawY, packet.HorizontalX,
                packet.HorizontalY, packet.VerticalX, packet.VerticalY, packet.ThrottlePercent))
            throw new InvalidDataException("Rocket input contains a non-finite value.");
        packet.ThrottlePercent = Math.Clamp(packet.ThrottlePercent, 0, 1);
        packet.WorldTime = WorldTime;
        rocket.Apply(packet);
        BroadcastLatest(PacketType.UpdateRocketSecondary, packet, packet.RocketId, player);
    }

    private void HandleDestroyPart(NetIncomingMessage message, TcpSession player)
    {
        var packet = Read<DestroyPartPacket>(message);
        if (!TryAuthorizedRocket(player, packet.RocketId, out var rocket) || !rocket.RemovePart(packet.PartId)) return;
        packet.WorldTime = WorldTime;
        Broadcast(PacketType.DestroyPart, packet, player);
    }

    private void HandleStaging(NetIncomingMessage message, TcpSession player)
    {
        var packet = Read<UpdateStagingPacket>(message);
        if (!TryAuthorizedRocket(player, packet.RocketId, out var rocket)) return;
        ValidateStages(packet.Stages, rocket);
        packet.WorldTime = WorldTime;
        rocket.Stages = packet.Stages;
        Broadcast(PacketType.UpdateStaging, packet, player);
    }

    private void HandleDockTransaction(NetIncomingMessage message, TcpSession player)
    {
        var packet = Read<DockTransactionPacket>(message);
        if (packet.Committed || packet.KeepRocketId == packet.RemoveRocketId) return;
        if (packet.Operation == DockTransactionOperation.Undock)
        {
            HandleUndockTransaction(packet, player);
            return;
        }
        if (packet.KeepRocketId > packet.RemoveRocketId)
        {
            (packet.KeepRocketId, packet.RemoveRocketId) = (packet.RemoveRocketId, packet.KeepRocketId);
            (packet.KeepPartId, packet.RemovePartId) = (packet.RemovePartId, packet.KeepPartId);
        }
        if (!_world.Rockets.TryGetValue(packet.KeepRocketId, out var keep) ||
            !_world.Rockets.TryGetValue(packet.RemoveRocketId, out var remove) ||
            !keep.Parts.ContainsKey(packet.KeepPartId) || !remove.Parts.ContainsKey(packet.RemovePartId)) return;

        var keepController = _players.Values.FirstOrDefault(value => value.ControlledRocket == packet.KeepRocketId);
        var removeController = _players.Values.FirstOrDefault(value => value.ControlledRocket == packet.RemoveRocketId);
        if (player != keepController && player != removeController && !CanUpdate(player, packet.KeepRocketId)) return;

        var key = (packet.KeepRocketId, packet.RemoveRocketId, packet.KeepPartId, packet.RemovePartId);
        if (!_pendingDocks.TryGetValue(key, out var pending) ||
            DateTime.UtcNow - pending.CreatedUtc > TimeSpan.FromSeconds(5))
        {
            pending = new PendingDock(packet, DateTime.UtcNow);
            _pendingDocks[key] = pending;
        }
        pending.Confirmations.Add(player.Id);

        if (keepController is not null && removeController is not null && keepController.Id != removeController.Id &&
            (!pending.Confirmations.Contains(keepController.Id) || !pending.Confirmations.Contains(removeController.Id))) return;

        var merged = MergeDockedRockets(keep, remove, packet.KeepPartId, packet.RemovePartId);
        _world.Rockets[packet.KeepRocketId] = merged;
        _world.Rockets.Remove(packet.RemoveRocketId);
        _pendingDocks.Remove(key);

        foreach (var connected in _players.Values)
        {
            if (connected.ControlledRocket != packet.KeepRocketId && connected.ControlledRocket != packet.RemoveRocketId) continue;
            connected.ControlledRocket = -1;
            Broadcast(PacketType.UpdatePlayerControl, new UpdatePlayerControlPacket
            {
                PlayerId = connected.Id,
                RocketId = -1,
            });
        }

        packet.Committed = true;
        packet.WorldTime = WorldTime;
        packet.MergedRocket = merged;
        Broadcast(PacketType.DockTransaction, packet);
        RefreshAuthorities();
    }

    private static RocketState MergeDockedRockets(RocketState keep, RocketState remove, int keepPartId, int removePartId)
    {
        var keepPort = keep.Parts[keepPartId];
        var removePort = remove.Parts[removePartId];
        var keepPivotX = keepPort.X;
        var keepPivotY = keepPort.Y;
        var removePivotX = removePort.X;
        var removePivotY = removePort.Y;
        var keepDirection = PortDirectionDegrees(keepPort);
        var removeDirection = PortDirectionDegrees(removePort);
        var alignWorld = MathF.Round((keepDirection - removeDirection + 180f) / 90f) * 90f;
        var relativeRotation = alignWorld;

        var idMap = new Dictionary<int, int>();
        var nextId = keep.Parts.Count == 0 ? 1 : keep.Parts.Keys.Max() + 1;
        foreach (var pair in remove.Parts.OrderBy(pair => pair.Key))
        {
            while (keep.Parts.ContainsKey(nextId)) nextId++;
            var id = keep.Parts.ContainsKey(pair.Key) ? nextId++ : pair.Key;
            idMap[pair.Key] = id;
            var relativeX = pair.Value.X - removePivotX;
            var relativeY = pair.Value.Y - removePivotY;
            Rotate(relativeX, relativeY, relativeRotation, out var rotatedX, out var rotatedY);
            pair.Value.X = keepPivotX + rotatedX;
            pair.Value.Y = keepPivotY + rotatedY;
            pair.Value.OrientationZ += relativeRotation;
            keep.Parts[id] = pair.Value;
        }
        foreach (var joint in remove.Joints)
            keep.Joints.Add(new JointState(idMap[joint.PartA], idMap[joint.PartB]));
        keep.Joints.Add(new JointState(keepPartId, idMap[removePartId]));
        foreach (var stage in remove.Stages)
            keep.Stages.Add(new StageState(stage.StageId, stage.PartIds.Select(id => idMap[id])));
        keep.RocketName = string.IsNullOrWhiteSpace(keep.RocketName) ? remove.RocketName : keep.RocketName;
        return keep;
    }

    private void HandleUndockTransaction(DockTransactionPacket packet, TcpSession player)
    {
        if (!_world.Rockets.TryGetValue(packet.KeepRocketId, out var source) ||
            !CanUpdate(player, packet.KeepRocketId)) return;
        var bridge = source.Joints.FirstOrDefault(joint =>
            (joint.PartA == packet.KeepPartId && joint.PartB == packet.RemovePartId) ||
            (joint.PartA == packet.RemovePartId && joint.PartB == packet.KeepPartId));
        if (bridge is null) return;

        source.Joints.Remove(bridge);
        var groups = ConnectedPartGroups(source);
        if (groups.Count != 2) return;
        var keepGroup = groups.FirstOrDefault(group => group.Contains(packet.KeepPartId)) ?? groups[0];
        var secondGroup = groups.First(group => group != keepGroup);
        var firstRocket = ExtractRocket(source, keepGroup);
        var secondRocket = ExtractRocket(source, secondGroup);
        var secondId = NextRocketId();
        _world.Rockets[packet.KeepRocketId] = firstRocket;
        _world.Rockets[secondId] = secondRocket;

        foreach (var connected in _players.Values)
        {
            if (connected.ControlledRocket != packet.KeepRocketId) continue;
            connected.ControlledRocket = -1;
            Broadcast(PacketType.UpdatePlayerControl, new UpdatePlayerControlPacket { PlayerId = connected.Id, RocketId = -1 });
        }

        packet.Committed = true;
        packet.WorldTime = WorldTime;
        packet.MergedRocket = firstRocket;
        packet.SecondRocketId = secondId;
        packet.SecondRocket = secondRocket;
        Broadcast(PacketType.DockTransaction, packet);
        RefreshAuthorities();
    }

    private static List<HashSet<int>> ConnectedPartGroups(RocketState rocket)
    {
        var neighbours = rocket.Parts.Keys.ToDictionary(id => id, _ => new List<int>());
        foreach (var joint in rocket.Joints)
        {
            neighbours[joint.PartA].Add(joint.PartB);
            neighbours[joint.PartB].Add(joint.PartA);
        }
        var remaining = new HashSet<int>(rocket.Parts.Keys);
        var groups = new List<HashSet<int>>();
        while (remaining.Count > 0)
        {
            var group = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(remaining.First());
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!remaining.Remove(id)) continue;
                group.Add(id);
                foreach (var neighbour in neighbours[id]) stack.Push(neighbour);
            }
            groups.Add(group);
        }
        return groups;
    }

    private static RocketState ExtractRocket(RocketState source, HashSet<int> ids)
    {
        var result = new RocketState
        {
            RocketName = source.RocketName,
            Location = source.Location,
            Rotation = source.Rotation,
            AngularVelocity = source.AngularVelocity,
            ThrottleOn = source.ThrottleOn,
            ThrottlePercent = source.ThrottlePercent,
            Rcs = source.Rcs,
            Joints = source.Joints.Where(joint => ids.Contains(joint.PartA) && ids.Contains(joint.PartB)).ToList(),
            Stages = source.Stages.Select(stage => new StageState(stage.StageId, stage.PartIds.Where(ids.Contains))).Where(stage => stage.PartIds.Count > 0).ToList(),
        };
        foreach (var id in ids) result.Parts[id] = source.Parts[id];
        return result;
    }

    private static float PortDirectionDegrees(PartState part) => part.OrientationZ + (part.OrientationY < 0 ? -90f : 90f);

    private static void Rotate(float x, float y, float degrees, out float rotatedX, out float rotatedY)
    {
        var radians = degrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        rotatedX = x * cos - y * sin;
        rotatedY = x * sin + y * cos;
    }

    private void HandleEngine(NetIncomingMessage message, TcpSession player)
    {
        var packet = Read<UpdatePartEnginePacket>(message);
        if (!TryAuthorizedPart(player, packet.RocketId, packet.PartId, out var part)) return;
        packet.WorldTime = WorldTime; part.ToggleVariables["engine_on"] = packet.EngineOn;
        Broadcast(PacketType.UpdatePart_EngineModule, packet, player);
    }

    private void HandleWheel(NetIncomingMessage message, TcpSession player)
    {
        var packet = Read<UpdatePartWheelPacket>(message);
        if (!TryAuthorizedPart(player, packet.RocketId, packet.PartId, out var part)) return;
        packet.WorldTime = WorldTime; part.ToggleVariables["wheel_on"] = packet.WheelOn;
        Broadcast(PacketType.UpdatePart_WheelModule, packet, player);
    }

    private void HandleBooster(NetIncomingMessage message, TcpSession player)
    {
        var packet = Read<UpdatePartBoosterPacket>(message);
        if (!TryAuthorizedPart(player, packet.RocketId, packet.PartId, out var part)) return;
        if (!AllFinite(packet.Throttle, packet.FuelPercent)) throw new InvalidDataException("Booster state is non-finite.");
        packet.Throttle = Math.Clamp(packet.Throttle, 0, 1); packet.FuelPercent = Math.Clamp(packet.FuelPercent, 0, 1);
        packet.WorldTime = WorldTime; part.NumberVariables["fuel_percent"] = packet.FuelPercent;
        Broadcast(PacketType.UpdatePart_BoosterModule, packet, player);
    }

    private void HandleParachute(NetIncomingMessage message, TcpSession player)
    {
        var packet = Read<UpdatePartParachutePacket>(message);
        if (!TryAuthorizedPart(player, packet.RocketId, packet.PartId, out var part)) return;
        if (!AllFinite(packet.State, packet.TargetState)) throw new InvalidDataException("Parachute state is non-finite.");
        packet.WorldTime = WorldTime; part.NumberVariables["animation_state"] = packet.State;
        part.NumberVariables["deploy_state"] = packet.TargetState;
        Broadcast(PacketType.UpdatePart_ParachuteModule, packet, player);
    }

    private void HandleMove(NetIncomingMessage message, TcpSession player)
    {
        var packet = Read<UpdatePartMovePacket>(message);
        if (!TryAuthorizedPart(player, packet.RocketId, packet.PartId, out var part)) return;
        if (!AllFinite(packet.Time, packet.TargetTime)) throw new InvalidDataException("Move state is non-finite.");
        packet.WorldTime = WorldTime; part.NumberVariables["state"] = packet.Time;
        part.NumberVariables["state_target"] = packet.TargetTime;
        Broadcast(PacketType.UpdatePart_MoveModule, packet, player);
    }

    private void HandleResource(NetIncomingMessage message, TcpSession player)
    {
        var packet = Read<UpdatePartResourcePacket>(message);
        if (!CanUpdate(player, packet.RocketId) || !double.IsFinite(packet.ResourcePercent)) return;
        if (!_world.Rockets.TryGetValue(packet.RocketId, out var rocket)) return;
        packet.ResourcePercent = Math.Clamp(packet.ResourcePercent, 0, 1);
        var found = false;
        foreach (var id in packet.PartIds)
            if (rocket.Parts.TryGetValue(id, out var part))
            { part.NumberVariables["fuel_percent"] = packet.ResourcePercent; found = true; }
        if (!found) return;
        packet.WorldTime = WorldTime;
        Broadcast(PacketType.UpdatePart_ResourceModule, packet, player);
    }

    private void Send(TcpSession session, PacketType type, INetData packet)
    {
        var payload = NetPayloadCodec.Serialize(type, packet);
        EnqueueCritical(session, new TcpFrame(TcpFrameKind.Packet,
            Interlocked.Increment(ref _sequence), payload.Data, payload.BitLength));
    }

    private void Broadcast(PacketType type, INetData packet, TcpSession? except = null)
    {
        var payload = NetPayloadCodec.Serialize(type, packet);
        foreach (var session in _players.Values)
        {
            if (session == except) continue;
            EnqueueCritical(session, new TcpFrame(TcpFrameKind.Packet,
                Interlocked.Increment(ref _sequence), payload.Data, payload.BitLength));
        }
    }

    private void BroadcastLatest(PacketType type, INetData packet, int rocketId, TcpSession? except = null)
    {
        var payload = NetPayloadCodec.Serialize(type, packet);
        var key = ((long)(byte)type << 32) | (uint)rocketId;
        foreach (var session in _players.Values)
        {
            if (session == except) continue;
            if (session.UdpEndpoint is not null)
            {
                _udp.Send(session.UdpEndpoint, session.UdpToken, payload.Data);
                continue;
            }
            session.Queue.EnqueueLatest(key, new TcpFrame(TcpFrameKind.Packet,
                Interlocked.Increment(ref _sequence), payload.Data, payload.BitLength));
            session.Signal();
        }
    }

    private static void EnqueueCritical(TcpSession session, TcpFrame frame)
    {
        session.Queue.EnqueueCritical(frame);
        session.Signal();
    }

    private void RefreshAuthorities()
    {
        foreach (var player in _players.Values) player.UpdateAuthority.Clear();
        var connected = _players.Values.OrderBy(player => player.Id).ToList();
        if (connected.Count == 0) return;
        var roundRobin = 0;
        foreach (var rocketId in _world.Rockets.Keys.OrderBy(id => id))
        {
            var owner = connected.FirstOrDefault(player => player.ControlledRocket == rocketId)
                ?? connected[roundRobin++ % connected.Count];
            owner.UpdateAuthority.Add(rocketId);
        }
        foreach (var player in connected)
            Send(player, PacketType.UpdatePlayerAuthority,
                new UpdatePlayerAuthorityPacket { RocketIds = new HashSet<int>(player.UpdateAuthority) });
        EnforceTimeScaleControlRule();
    }

    private bool CanUpdate(TcpSession player, int rocketId) =>
        player.ControlledRocket == rocketId || player.UpdateAuthority.Contains(rocketId);

    private bool TryAuthorizedRocket(TcpSession player, int rocketId, out RocketState rocket)
    {
        if (CanUpdate(player, rocketId) && _world.Rockets.TryGetValue(rocketId, out rocket!)) return true;
        rocket = null!; return false;
    }

    private bool TryAuthorizedPart(TcpSession player, int rocketId, int partId, out PartState part)
    {
        if (TryAuthorizedRocket(player, rocketId, out var rocket) && rocket.Parts.TryGetValue(partId, out part!))
            return true;
        part = null!; return false;
    }

    private int NextRocketId()
    {
        int id;
        do id = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        while (_world.Rockets.ContainsKey(id));
        return id;
    }

    private static T Read<T>(NetIncomingMessage message) where T : INetData, new()
    { var packet = new T(); packet.Deserialize(message); return packet; }

    private static Color3 ClampColor(Color3 color)
    {
        if (!AllFinite(color.R, color.G, color.B)) throw new InvalidDataException("Color is non-finite.");
        return new Color3(Math.Clamp(color.R, 0, 1), Math.Clamp(color.G, 0, 1), Math.Clamp(color.B, 0, 1));
    }

    private static void ValidateFinite(NetLocation location, float rotation, float angularVelocity)
    {
        if (!double.IsFinite(location.X) || !double.IsFinite(location.Y) ||
            !double.IsFinite(location.Vx) || !double.IsFinite(location.Vy) ||
            !AllFinite(rotation, angularVelocity) || string.IsNullOrWhiteSpace(location.Address) ||
            location.Address.Length > 256 || location.Address.Any(char.IsControl))
            throw new InvalidDataException("Rocket location contains invalid values.");
    }

    private static bool AllFinite(params float[] values) => values.All(float.IsFinite);

    private static void ValidateRocket(RocketState rocket)
    {
        if (rocket.RocketName.Length > 256 || rocket.RocketName.Any(char.IsControl))
            throw new InvalidDataException("Rocket name is invalid.");
        if (rocket.Parts.Count > NetMessageExtensions.MaxCollectionCount ||
            rocket.Joints.Count > NetMessageExtensions.MaxCollectionCount ||
            rocket.Stages.Count > NetMessageExtensions.MaxCollectionCount)
            throw new InvalidDataException("Rocket collections are too large.");
        ValidateFinite(rocket.Location, rocket.Rotation, rocket.AngularVelocity);
        if (!float.IsFinite(rocket.ThrottlePercent)) throw new InvalidDataException("Rocket throttle is non-finite.");
        ValidateStages(rocket.Stages, rocket);
    }

    private static void ValidateStages(IEnumerable<StageState> stages, RocketState rocket)
    {
        foreach (var stage in stages)
        {
            if (stage.PartIds.Count > NetMessageExtensions.MaxCollectionCount)
                throw new InvalidDataException("Stage contains too many part IDs.");
            if (stage.PartIds.Any(id => !rocket.Parts.ContainsKey(id)))
                throw new InvalidDataException("Stage references an unknown part.");
        }
    }

    private static bool PasswordMatches(string supplied, string expected)
    {
        if (expected.Length == 0) return true;
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(supplied ?? string.Empty));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static Color3 RandomColor()
    {
        var hue = RandomNumberGenerator.GetInt32(0, 360) / 360f;
        var section = hue * 6f;
        var x = 1f - MathF.Abs(section % 2f - 1f);
        return (int)section switch
        {
            0 => new Color3(1, x, 0), 1 => new Color3(x, 1, 0),
            2 => new Color3(0, 1, x), 3 => new Color3(0, x, 1),
            4 => new Color3(x, 0, 1), _ => new Color3(1, 0, x),
        };
    }

    private static TcpFrame DisconnectFrame(string reason)
    {
        var bytes = Encoding.UTF8.GetBytes(reason);
        return new TcpFrame(TcpFrameKind.Disconnect, 0, bytes, bytes.Length * 8);
    }

    private static ValueTask SendDisconnectDirectAsync(Stream stream, string reason, CancellationToken cancellationToken) =>
        TcpFrameCodec.WriteAsync(stream, DisconnectFrame(reason), cancellationToken);

    private void SaveIfDue()
    {
        if (_settings.AutoSaveSeconds <= 0 || string.IsNullOrWhiteSpace(_settings.StatePath)) return;
        if (_saveClock.Elapsed.TotalSeconds < _settings.AutoSaveSeconds) return;
        SaveState(); _saveClock.Restart();
    }

    private void SaveState()
    {
        if (string.IsNullOrWhiteSpace(_settings.StatePath)) return;
        lock (_worldLock)
        {
            _world.WorldTime += _worldClock.Elapsed.TotalSeconds * _timeScale;
            _worldClock.Restart();
            ServerStateStore.Save(_settings.StatePath, _world);
        }
    }

    public ServerCommandResult ExecuteCommand(string? commandLine)
    {
        var line = (commandLine ?? string.Empty).Trim();
        if (line.Length == 0) return new ServerCommandResult(false, string.Empty);
        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();
        switch (command)
        {
            case "help":
                return new ServerCommandResult(false,
                    "命令: help, status, players, say <消息>, timewarp <1|5|25|100|500|2500>, stoptimewarp, cleardebris [最大部件数], save, resync, kick <ID|名字>, stop");
            case "status":
                return new ServerCommandResult(false,
                    $"玩家={PlayerCount} 火箭={RocketCount()} 世界时间={WorldTime:F1} 倍率={TimeScale:0.##}x");
            case "players":
                return new ServerCommandResult(false, PlayerList());
            case "say":
                if (parts.Length < 2) return new ServerCommandResult(false, "用法: say <消息>");
                var text = line.Substring(line.IndexOf(' ') + 1).Trim();
                if (text.Length == 0) return new ServerCommandResult(false, "用法: say <消息>");
                Broadcast(PacketType.SendChatMessage, new SendChatMessagePacket { SenderId = -1, Message = "[服务器] " + text });
                return new ServerCommandResult(false, "广播已发送。");
            case "timewarp":
                if (parts.Length != 2 || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var scale) || !IsAllowedTimeScale(scale))
                    return new ServerCommandResult(false, "允许的时间倍率: 1 到 2500");
                CancelTimeWarpVote("服务器已强制设置时间倍率，当前投票取消。");
                SetTimeScale(scale, "服务器强制设置");
                return new ServerCommandResult(false, $"时间倍率已强制设置为 {scale:0.##}x。");
            case "stoptimewarp":
                CancelTimeWarpVote("服务器已结束时间加速，当前投票取消。");
                SetTimeScale(1, "服务器结束时间加速");
                return new ServerCommandResult(false, "时间倍率已恢复为 1x。");
            case "cleardebris":
                var maxParts = 3;
                if (parts.Length > 2 || (parts.Length == 2 && (!int.TryParse(parts[1], out maxParts) || maxParts < 0)))
                    return new ServerCommandResult(false, "用法: cleardebris [最大部件数]（默认 3）");
                var removed = ClearDebris(maxParts);
                return new ServerCommandResult(false, $"已清理 {removed} 枚无人控制且部件数不超过 {maxParts} 的太空垃圾。");
            case "save":
                SaveState();
                return new ServerCommandResult(false, "世界状态已保存。");
            case "resync":
                lock (_worldLock)
                    foreach (var session in _players.Values) SendWorldSnapshot(session);
                return new ServerCommandResult(false, "已向所有玩家发送世界快照。");
            case "kick":
                if (parts.Length != 2) return new ServerCommandResult(false, "用法: kick <玩家ID|名字>");
                var target = FindPlayer(parts[1]);
                if (target is null) return new ServerCommandResult(false, "未找到玩家。");
                target.Close();
                return new ServerCommandResult(false, $"已踢出 {target.Username} (ID {target.Id})。");
            case "stop":
            case "exit":
                return new ServerCommandResult(true, "正在安全保存并停止服务端。");
            default:
                return new ServerCommandResult(false, "未知命令。输入 help 查看命令。");
        }
    }

    private int RocketCount() { lock (_worldLock) return _world.Rockets.Count; }

    private string PlayerList()
    {
        var players = _players.Values.OrderBy(p => p.Id).Select(p =>
            $"{p.Id}: {p.Username} 控制={p.ControlledRocket} RTT={p.RoundTripMs:F0}ms").ToArray();
        return players.Length == 0 ? "当前没有在线玩家。" : string.Join(Environment.NewLine, players);
    }

    private TcpSession? FindPlayer(string value)
    {
        if (int.TryParse(value, out var id) && _players.TryGetValue(id, out var byId)) return byId;
        return _players.Values.FirstOrDefault(p => string.Equals(p.Username, value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAllowedTimeScale(double value) =>
        double.IsFinite(value) && value >= TimeWarpControlRules.MinimumMultiplier &&
        value <= TimeWarpControlRules.MaximumMultiplier;

    private void EnforceTimeScaleControlRule()
    {
        if (_timeScale == 1) return;
        var controllingPlayers = _players.Values.Count(session => session.ControlledRocket != -1);
        if (controllingPlayers != 1) SetTimeScale(1, string.Empty);
    }

    private void SetTimeScale(double scale, string reason, int voteId = 0)
    {
        lock (_worldLock)
        {
            _world.WorldTime += _worldClock.Elapsed.TotalSeconds * _timeScale;
            _worldClock.Restart();
            _timeScale = scale;
            Broadcast(PacketType.TimeWarp, new TimeWarpPacket
            {
                Operation = TimeWarpOperation.Applied,
                VoteId = voteId,
                Multiplier = scale,
                WorldTime = _world.WorldTime,
                Approved = true,
                Message = reason,
            });
        }
    }

    private int ClearDebris(int maxParts)
    {
        lock (_worldLock)
        {
            var controlled = _players.Values.Select(player => player.ControlledRocket).Where(id => id >= 0).ToHashSet();
            var ids = _world.Rockets.Where(pair => !controlled.Contains(pair.Key) && pair.Value.Parts.Count <= maxParts)
                .Select(pair => pair.Key).ToArray();
            foreach (var id in ids)
            {
                _world.Rockets.Remove(id);
                Broadcast(PacketType.DestroyRocket, new DestroyRocketPacket
                {
                    WorldTime = WorldTime,
                    RocketId = id,
                    Reason = 0,
                });
            }
            if (ids.Length > 0) RefreshAuthorities();
            return ids.Length;
        }
    }

    private void PrintDebugSummary()
    {
        var sent = _players.Values.Sum(player => player.SentBytes);
        var received = _players.Values.Sum(player => player.ReceivedBytes);
        var overwritten = _players.Values.Sum(player => player.Queue.OverwrittenStates);
        Console.WriteLine($"[TCP调试] 玩家={_players.Count} 上行={received / 1024.0:F1}KB 下行={sent / 1024.0:F1}KB 状态覆盖={overwritten}");
        foreach (var player in _players.Values.OrderBy(player => player.Id))
            Console.WriteLine($"[TCP玩家] {player.Username} RTT={player.RoundTripMs:F0}ms 抖动={player.JitterMs:F0}ms 队列={player.Queue.Count} 收={player.ReceivedFrames} 发={player.SentFrames}");
    }

    public ValueTask DisposeAsync()
    {
        if (_started)
        {
            _listener.Stop();
            _udp.Dispose();
            foreach (var session in _players.Values) session.Close();
            _started = false;
        }
        return ValueTask.CompletedTask;
    }

    private sealed class PendingDock
    {
        public DockTransactionPacket Request { get; }
        public DateTime CreatedUtc { get; }
        public HashSet<int> Confirmations { get; } = new();

        public PendingDock(DockTransactionPacket request, DateTime createdUtc)
        {
            Request = request;
            CreatedUtc = createdUtc;
        }
    }

    private sealed class PendingTimeWarpVote
    {
        public int Id { get; }
        public int RequesterId { get; }
        public string RequesterName { get; }
        public double Multiplier { get; }
        public HashSet<int> RequiredPlayerIds { get; }
        public HashSet<int> ApprovedPlayerIds { get; } = new();
        public DateTime ExpiresUtc { get; }

        public PendingTimeWarpVote(int id, int requesterId, string requesterName, double multiplier,
            HashSet<int> requiredPlayerIds, DateTime expiresUtc)
        {
            Id = id;
            RequesterId = requesterId;
            RequesterName = requesterName;
            Multiplier = multiplier;
            RequiredPlayerIds = requiredPlayerIds;
            ExpiresUtc = expiresUtc;
        }
    }

    private sealed class TcpSession
    {
        public int Id { get; }
        public string Username { get; }
        public Color3 Color { get; set; }
        public TcpClient Client { get; private set; }
        public NetworkStream Stream { get; private set; }
        public int ConnectionGeneration { get; private set; } = 1;
        public TcpSendQueue Queue { get; } = new();
        public SemaphoreSlim SendSignal { get; } = new(0, 1);
        public CancellationTokenSource Closed { get; } = new();
        public HashSet<int> UpdateAuthority { get; } = new();
        public ConcurrentDictionary<PacketType, long> PacketCounts { get; } = new();
        public string UdpToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        public string ResumeToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        public IPEndPoint? UdpEndpoint { get; set; }
        public DateTime LastUdpReceiveUtc { get; set; } = DateTime.UtcNow;
        public DateTime RecoveryExpiresUtc { get; private set; } = DateTime.MinValue;
        public int ControlledRocket { get; set; } = -1;
        public DateTime LastChatUtc { get; set; } = DateTime.MinValue;
        public DateTime LastReceiveUtc { get; set; } = DateTime.UtcNow;
        public long LastPingTicks { get; set; }
        public double RoundTripMs { get; set; }
        public double JitterMs { get; set; }
        public long SentBytes { get; set; }
        public long ReceivedBytes { get; set; }
        public long SentFrames { get; set; }
        public long ReceivedFrames { get; set; }

        public TcpSession(int id, string username, Color3 color, TcpClient client, NetworkStream stream)
        { Id = id; Username = username; Color = color; Client = client; Stream = stream; }

        public bool CanResume(string token) =>
            RecoveryExpiresUtc >= DateTime.UtcNow &&
            !string.IsNullOrEmpty(token) &&
            CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(ResumeToken), Encoding.UTF8.GetBytes(token));

        public void ReplaceConnection(TcpClient client, NetworkStream stream)
        {
            var oldClient = Client;
            Client = client;
            Stream = stream;
            ConnectionGeneration++;
            RecoveryExpiresUtc = DateTime.MinValue;
            LastReceiveUtc = DateTime.UtcNow;
            try { oldClient.Close(); } catch { }
        }

        public bool EnterRecoveryWindow()
        {
            if (DateTime.UtcNow - LastUdpReceiveUtc > TimeSpan.FromSeconds(5)) return false;
            RecoveryExpiresUtc = DateTime.UtcNow.AddSeconds(20);
            return true;
        }

        public void Signal()
        {
            try { SendSignal.Release(); } catch (SemaphoreFullException) { }
        }

        public void Close()
        {
            if (!Closed.IsCancellationRequested) Closed.Cancel();
            try { Client.Close(); } catch { }
            Signal();
        }
    }
}
