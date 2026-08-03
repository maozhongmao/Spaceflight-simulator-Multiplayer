using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lidgren.Network;

namespace MultiplayerSFS.Common;

public enum TcpFrameKind : byte
{
	Hello = 1,
	HelloAck = 2,
	Packet = 3,
	Ping = 4,
	Pong = 5,
	Disconnect = 6,
	RequestWorldSnapshot = 7,
	RequestRocketSnapshot = 8
}

public sealed class TcpFrame
{
	public TcpFrameKind Kind { get; }
	public int Sequence { get; }
	public byte[] Payload { get; }
	public int PayloadBits { get; }

	public TcpFrame(TcpFrameKind kind, int sequence, byte[] payload, int payloadBits)
	{
		Kind = kind;
		Sequence = sequence;
		Payload = payload ?? Array.Empty<byte>();
		PayloadBits = payloadBits;
		if (payloadBits < 0 || payloadBits > Payload.Length * 8)
		{
			throw new ArgumentOutOfRangeException(nameof(payloadBits));
		}
	}
}

public static class TcpFrameCodec
{
	public const int ProtocolVersion = 32;
	public const int MaxPayloadBytes = 8 * 1024 * 1024;
	private const int BodyHeaderBytes = 9;

	public static byte[] Encode(TcpFrame frame)
	{
		if (frame == null) throw new ArgumentNullException(nameof(frame));
		if (frame.Payload.Length > MaxPayloadBytes)
		{
			throw new InvalidDataException("TCP frame payload is too large.");
		}
		int bodyLength = BodyHeaderBytes + frame.Payload.Length;
		byte[] bytes = new byte[4 + bodyLength];
		WriteInt32(bytes, 0, bodyLength);
		bytes[4] = (byte)frame.Kind;
		WriteInt32(bytes, 5, frame.Sequence);
		WriteInt32(bytes, 9, frame.PayloadBits);
		Buffer.BlockCopy(frame.Payload, 0, bytes, 13, frame.Payload.Length);
		return bytes;
	}

	public static TcpFrame Read(Stream stream)
	{
		byte[] lengthBytes = ReadExactly(stream, 4);
		int bodyLength = ReadInt32(lengthBytes, 0);
		ValidateBodyLength(bodyLength);
		return DecodeBody(ReadExactly(stream, bodyLength));
	}

	public static async Task<TcpFrame> ReadAsync(Stream stream, CancellationToken cancellationToken)
	{
		byte[] lengthBytes = await ReadExactlyAsync(stream, 4, cancellationToken).ConfigureAwait(false);
		int bodyLength = ReadInt32(lengthBytes, 0);
		ValidateBodyLength(bodyLength);
		return DecodeBody(await ReadExactlyAsync(stream, bodyLength, cancellationToken).ConfigureAwait(false));
	}

	public static async Task WriteAsync(Stream stream, TcpFrame frame, CancellationToken cancellationToken)
	{
		byte[] bytes = Encode(frame);
		await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
	}

	private static TcpFrame DecodeBody(byte[] body)
	{
		TcpFrameKind kind = (TcpFrameKind)body[0];
		if (!Enum.IsDefined(typeof(TcpFrameKind), kind))
		{
			throw new InvalidDataException("Invalid TCP frame kind: " + body[0] + ".");
		}
		int sequence = ReadInt32(body, 1);
		int payloadBits = ReadInt32(body, 5);
		int payloadLength = body.Length - BodyHeaderBytes;
		if (payloadBits < 0 || payloadBits > payloadLength * 8)
		{
			throw new InvalidDataException("Invalid TCP frame bit length: " + payloadBits + ".");
		}
		byte[] payload = new byte[payloadLength];
		Buffer.BlockCopy(body, BodyHeaderBytes, payload, 0, payloadLength);
		return new TcpFrame(kind, sequence, payload, payloadBits);
	}

	private static void ValidateBodyLength(int bodyLength)
	{
		if (bodyLength < BodyHeaderBytes || bodyLength > BodyHeaderBytes + MaxPayloadBytes)
		{
			throw new InvalidDataException("Invalid TCP frame length: " + bodyLength + ".");
		}
	}

	private static byte[] ReadExactly(Stream stream, int count)
	{
		byte[] buffer = new byte[count];
		int offset = 0;
		while (offset < count)
		{
			int read = stream.Read(buffer, offset, count - offset);
			if (read <= 0) throw new EndOfStreamException("TCP connection closed while reading a frame.");
			offset += read;
		}
		return buffer;
	}

	private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[count];
		int offset = 0;
		while (offset < count)
		{
			int read = await stream.ReadAsync(buffer, offset, count - offset, cancellationToken).ConfigureAwait(false);
			if (read <= 0) throw new EndOfStreamException("TCP connection closed while reading a frame.");
			offset += read;
		}
		return buffer;
	}

	private static void WriteInt32(byte[] buffer, int offset, int value)
	{
		buffer[offset] = (byte)value;
		buffer[offset + 1] = (byte)(value >> 8);
		buffer[offset + 2] = (byte)(value >> 16);
		buffer[offset + 3] = (byte)(value >> 24);
	}

	private static int ReadInt32(byte[] buffer, int offset)
	{
		return buffer[offset] |
			(buffer[offset + 1] << 8) |
			(buffer[offset + 2] << 16) |
			(buffer[offset + 3] << 24);
	}
}

public sealed class NetPayload
{
	public byte[] Data { get; }
	public int BitLength { get; }

	public NetPayload(byte[] data, int bitLength)
	{
		Data = data;
		BitLength = bitLength;
	}
}

public static class NetPayloadCodec
{
	public static NetPayload Serialize(INetData value)
	{
		NetOutgoingMessage outgoing = NewOutgoing();
		value.Serialize(outgoing);
		return Copy(outgoing);
	}

	public static NetPayload Serialize(Packet packet, bool includePacketType)
	{
		NetOutgoingMessage outgoing = NewOutgoing();
		if (includePacketType) outgoing.Write((byte)packet.Type);
		packet.Serialize(outgoing);
		return Copy(outgoing);
	}

	public static T Deserialize<T>(byte[] data, int bitLength) where T : INetData, new()
	{
		T value = new T();
		value.Deserialize(ToIncoming(data, bitLength));
		return value;
	}

	public static NetIncomingMessage ToIncoming(byte[] data, int bitLength)
	{
		NetIncomingMessage incoming = (NetIncomingMessage)Activator.CreateInstance(typeof(NetIncomingMessage), true);
		incoming.Data = data.ToArray();
		incoming.LengthBits = bitLength;
		incoming.Position = 0;
		return incoming;
	}

	private static NetOutgoingMessage NewOutgoing()
	{
		return (NetOutgoingMessage)Activator.CreateInstance(typeof(NetOutgoingMessage), true);
	}

	private static NetPayload Copy(NetOutgoingMessage outgoing)
	{
		byte[] data = new byte[outgoing.LengthBytes];
		Buffer.BlockCopy(outgoing.Data, 0, data, 0, data.Length);
		return new NetPayload(data, outgoing.LengthBits);
	}
}

public sealed class TcpSendQueue
{
	private readonly object sync = new object();
	private readonly Queue<TcpFrame> critical = new Queue<TcpFrame>();
	private readonly Dictionary<long, TcpFrame> latest = new Dictionary<long, TcpFrame>();
	private readonly Queue<long> latestOrder = new Queue<long>();
	private long overwrittenStates;

	public long OverwrittenStates { get { lock (sync) return overwrittenStates; } }
	public int Count { get { lock (sync) return critical.Count + latest.Count; } }

	public void EnqueueCritical(TcpFrame frame)
	{
		lock (sync)
		{
			critical.Enqueue(frame);
			Monitor.PulseAll(sync);
		}
	}

	public void EnqueueLatest(long key, TcpFrame frame)
	{
		lock (sync)
		{
			if (latest.ContainsKey(key)) overwrittenStates++;
			else latestOrder.Enqueue(key);
			latest[key] = frame;
			Monitor.PulseAll(sync);
		}
	}

	public bool TryDequeue(out TcpFrame frame)
	{
		lock (sync) return TryDequeueUnsafe(out frame);
	}

	public bool WaitDequeue(int millisecondsTimeout, out TcpFrame frame)
	{
		lock (sync)
		{
			if (TryDequeueUnsafe(out frame)) return true;
			Monitor.Wait(sync, millisecondsTimeout);
			return TryDequeueUnsafe(out frame);
		}
	}

	private bool TryDequeueUnsafe(out TcpFrame frame)
	{
		if (critical.Count > 0)
		{
			frame = critical.Dequeue();
			return true;
		}
		while (latestOrder.Count > 0)
		{
			long key = latestOrder.Dequeue();
			if (latest.TryGetValue(key, out frame))
			{
				latest.Remove(key);
				return true;
			}
		}
		frame = null;
		return false;
	}
}

public sealed class TcpClientTransport : IDisposable
{
	private readonly object stateLock = new object();
	private readonly object statsLock = new object();
	private readonly Queue<TcpFrame> incoming = new Queue<TcpFrame>();
	private readonly TcpSendQueue outgoing = new TcpSendQueue();
	private TcpClient tcpClient;
	private NetworkStream stream;
	private UdpClientTransport udp;
	private CancellationTokenSource cancellation;
	private Task readerTask;
	private Task writerTask;
	private Task heartbeatTask;
	private int sequence;
	private long lastReceiveTicks;
	private long sentBytes;
	private long receivedBytes;
	private long sentFrames;
	private long receivedFrames;
	private double roundTripMs;
	private double jitterMs;
	private string lastDisconnectReason = "Not connected";
	private string lastPacketType = "None";
	private string remoteAddress = string.Empty;
	private volatile bool connected;

	public bool Connected => connected;
	public int QueueCount => outgoing.Count;
	public long OverwrittenStates => outgoing.OverwrittenStates;
	public long SentBytes => Interlocked.Read(ref sentBytes);
	public long ReceivedBytes => Interlocked.Read(ref receivedBytes);
	public long SentFrames => Interlocked.Read(ref sentFrames);
	public long ReceivedFrames => Interlocked.Read(ref receivedFrames);
	public string LastDisconnectReason { get { lock (statsLock) return lastDisconnectReason; } }
	public string LastPacketType { get { lock (statsLock) return lastPacketType; } set { lock (statsLock) lastPacketType = value; } }
	public string RemoteAddress { get { lock (statsLock) return remoteAddress; } }
	public double RoundTripMs { get { lock (statsLock) return roundTripMs; } }
	public double JitterMs { get { lock (statsLock) return jitterMs; } }
	public NetworkAdaptiveProfile AdaptiveProfile => NetworkAdaptationPolicy.Evaluate(RoundTripMs, JitterMs, QueueCount);
	public double SecondsSinceReceive
	{
		get
		{
			long ticks = Interlocked.Read(ref lastReceiveTicks);
			return ticks == 0 ? double.PositiveInfinity : Math.Max(0, (DateTime.UtcNow.Ticks - ticks) / (double)TimeSpan.TicksPerSecond);
		}
	}

	public async Task<Packet_JoinResponse> ResumeAsync(IPAddress address, int port, Packet_JoinRequest request)
	{
		if (udp == null || string.IsNullOrEmpty(request.ResumeToken) || request.ResumePlayerId < 0)
			throw new InvalidOperationException("No active UDP session is available for TCP resume.");
		return await ConnectCoreAsync(address, port, request, true).ConfigureAwait(false);
	}

	public async Task<Packet_JoinResponse> ConnectAsync(IPAddress address, int port, Packet_JoinRequest request)
	{
		return await ConnectCoreAsync(address, port, request, false).ConfigureAwait(false);
	}

	private async Task<Packet_JoinResponse> ConnectCoreAsync(IPAddress address, int port, Packet_JoinRequest request, bool preserveUdp)
	{
		if (!preserveUdp) Disconnect("Reconnecting");
		else DisconnectTcpOnly("Recovering TCP");
		if (address == null || IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address))
		{
			throw new ArgumentException("请输入服务器的实际 IP 地址，不能使用 0.0.0.0 或 ::。", nameof(address));
		}
		TcpClient candidate = new TcpClient(address.AddressFamily)
		{
			NoDelay = true,
			ReceiveBufferSize = 256 * 1024,
			SendBufferSize = 256 * 1024
		};
		Task connectTask = candidate.ConnectAsync(address, port);
		if (await Task.WhenAny(connectTask, Task.Delay(10000)).ConfigureAwait(false) != connectTask)
		{
			candidate.Close();
			throw new TimeoutException("TCP connection timed out.");
		}
		await connectTask.ConfigureAwait(false);
		UnityEngine.Debug.Log("[MP-CONNECT] TCP_CONNECTED " + address + ":" + port);
		NetworkStream candidateStream = candidate.GetStream();
		using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
		{
			byte[] helloPayload = SessionHandshakeCodec.EncodeHello(request);
			await TcpFrameCodec.WriteAsync(candidateStream,
				new TcpFrame(TcpFrameKind.Hello, SessionHandshakeCodec.Version,
					helloPayload, helloPayload.Length * 8), timeout.Token).ConfigureAwait(false);
			UnityEngine.Debug.Log("[MP-CONNECT] HELLO_SENT bytes=" + helloPayload.Length);
			TcpFrame ack = await TcpFrameCodec.ReadAsync(candidateStream, timeout.Token).ConfigureAwait(false);
			UnityEngine.Debug.Log("[MP-CONNECT] HANDSHAKE_FRAME_RECEIVED kind=" + ack.Kind + " sequence=" + ack.Sequence + " bytes=" + ack.Payload.Length);
			if (ack.Kind == TcpFrameKind.Disconnect)
			{
				candidate.Close();
				throw new InvalidOperationException(Encoding.UTF8.GetString(ack.Payload));
			}
			if (ack.Kind != TcpFrameKind.HelloAck || ack.Sequence != SessionHandshakeCodec.Version)
			{
				candidate.Close();
				throw new InvalidDataException("Server returned an invalid session handshake.");
			}
			Packet_JoinResponse response = SessionHandshakeCodec.DecodeAck(ack.Payload);
			UnityEngine.Debug.Log("[MP-CONNECT] HELLO_ACK_DECODED player=" + response.PlayerId + " udpTokenBytes=" + Encoding.UTF8.GetByteCount(response.UdpSessionToken));
			lock (stateLock)
			{
				tcpClient = candidate;
				stream = candidateStream;
				cancellation = new CancellationTokenSource();
				connected = true;
				Interlocked.Exchange(ref lastReceiveTicks, DateTime.UtcNow.Ticks);
				lock (statsLock)
				{
					remoteAddress = address + ":" + port;
					lastDisconnectReason = string.Empty;
				}
				readerTask = Task.Run(() => ReaderLoop(cancellation.Token));
				writerTask = Task.Run(() => WriterLoop(cancellation.Token));
				heartbeatTask = Task.Run(() => HeartbeatLoop(cancellation.Token));
				if (!preserveUdp)
				{
					udp = new UdpClientTransport(EnqueueUdpPacket);
					udp.Start(address, port, response.UdpSessionToken);
				}
			}
			return response;
		}
	}

	public void Send(Packet packet)
	{
		if (!Connected || packet == null) return;
		if ((packet is Packet_UpdateRocketPrimary || packet is Packet_UpdateRocketSecondary) && udp != null && udp.Bound)
		{
			udp.SendPacket(packet);
			return;
		}
		NetPayload payload = NetPayloadCodec.Serialize(packet, true);
		TcpFrame frame = new TcpFrame(TcpFrameKind.Packet, Interlocked.Increment(ref sequence), payload.Data, payload.BitLength);
		long key;
		if (TryGetLatestStateKey(packet, out key)) outgoing.EnqueueLatest(key, frame);
		else outgoing.EnqueueCritical(frame);
	}

	public void RequestWorldSnapshot()
	{
		if (Connected) outgoing.EnqueueCritical(new TcpFrame(TcpFrameKind.RequestWorldSnapshot,
			Interlocked.Increment(ref sequence), Array.Empty<byte>(), 0));
	}

	public void RequestRocketSnapshot(int rocketId)
	{
		if (!Connected) return;
		byte[] payload = BitConverter.GetBytes(rocketId);
		outgoing.EnqueueCritical(new TcpFrame(TcpFrameKind.RequestRocketSnapshot,
			Interlocked.Increment(ref sequence), payload, payload.Length * 8));
	}

	private void EnqueueUdpPacket(TcpFrame frame)
	{
		lock (incoming) incoming.Enqueue(frame);
	}

	public bool TryReceive(out TcpFrame frame)
	{
		lock (incoming)
		{
			if (incoming.Count > 0)
			{
				frame = incoming.Dequeue();
				return true;
			}
		}
		frame = null;
		return false;
	}

	public void ClearStatistics()
	{
		Interlocked.Exchange(ref sentBytes, 0);
		Interlocked.Exchange(ref receivedBytes, 0);
		Interlocked.Exchange(ref sentFrames, 0);
		Interlocked.Exchange(ref receivedFrames, 0);
		lock (statsLock)
		{
			roundTripMs = 0;
			jitterMs = 0;
			lastPacketType = "None";
		}
	}

	private void DisconnectTcpOnly(string reason)
	{
		CancellationTokenSource oldCancellation;
		TcpClient oldClient;
		lock (stateLock)
		{
			oldCancellation = cancellation;
			oldClient = tcpClient;
			cancellation = null;
			tcpClient = null;
			stream = null;
			connected = false;
		}
		if (!string.IsNullOrEmpty(reason))
		{
			lock (statsLock) lastDisconnectReason = reason;
		}
		try { oldCancellation?.Cancel(); } catch { }
		try { oldClient?.Close(); } catch { }
	}

	public void Disconnect(string reason)
	{
		CancellationTokenSource oldCancellation;
		TcpClient oldClient;
		UdpClientTransport oldUdp;
		lock (stateLock)
		{
			oldCancellation = cancellation;
			oldClient = tcpClient;
			oldUdp = udp;
			udp = null;
			cancellation = null;
			tcpClient = null;
			stream = null;
			connected = false;
		}
		if (!string.IsNullOrEmpty(reason))
		{
			lock (statsLock) lastDisconnectReason = reason;
		}
		try { oldCancellation?.Cancel(); } catch { }
		try { oldUdp?.Dispose(); } catch { }
		try { oldClient?.Close(); } catch { }
		lock (incoming) incoming.Clear();
	}

	private void ReaderLoop(CancellationToken token)
	{
		try
		{
			while (!token.IsCancellationRequested)
			{
				TcpFrame frame = TcpFrameCodec.Read(stream);
				Interlocked.Exchange(ref lastReceiveTicks, DateTime.UtcNow.Ticks);
				Interlocked.Add(ref receivedBytes, frame.Payload.Length + 13);
				Interlocked.Increment(ref receivedFrames);
				switch (frame.Kind)
				{
				case TcpFrameKind.Ping:
					outgoing.EnqueueCritical(new TcpFrame(TcpFrameKind.Pong, frame.Sequence, frame.Payload, frame.PayloadBits));
					break;
				case TcpFrameKind.Pong:
					UpdateRoundTrip(frame);
					break;
				case TcpFrameKind.Packet:
					lock (incoming) incoming.Enqueue(frame);
					break;
				case TcpFrameKind.Disconnect:
					lock (statsLock) lastDisconnectReason = Encoding.UTF8.GetString(frame.Payload);
					lock (incoming) incoming.Enqueue(frame);
					connected = false;
					return;
				}
			}
		}
		catch (Exception ex)
		{
			if (!token.IsCancellationRequested)
			{
				lock (statsLock) lastDisconnectReason = ex is EndOfStreamException ? "Server closed the TCP connection." : ex.Message;
				connected = false;
				lock (incoming) incoming.Enqueue(new TcpFrame(TcpFrameKind.Disconnect, 0,
					Encoding.UTF8.GetBytes(LastDisconnectReason), Encoding.UTF8.GetByteCount(LastDisconnectReason) * 8));
			}
		}
	}

	private void WriterLoop(CancellationToken token)
	{
		try
		{
			while (!token.IsCancellationRequested)
			{
				TcpFrame frame;
				if (!outgoing.WaitDequeue(100, out frame)) continue;
				byte[] bytes = TcpFrameCodec.Encode(frame);
				stream.Write(bytes, 0, bytes.Length);
				Interlocked.Add(ref sentBytes, bytes.Length);
				Interlocked.Increment(ref sentFrames);
			}
		}
		catch (Exception ex)
		{
			if (!token.IsCancellationRequested)
			{
				lock (statsLock) lastDisconnectReason = ex.Message;
				connected = false;
			}
		}
	}

	private void HeartbeatLoop(CancellationToken token)
	{
		while (!token.WaitHandle.WaitOne(2000))
		{
			if (SecondsSinceReceive > 10)
			{
				lock (statsLock) lastDisconnectReason = "TCP heartbeat timed out (10 seconds).";
				connected = false;
				try { tcpClient?.Close(); } catch { }
				return;
			}
			long ticks = DateTime.UtcNow.Ticks;
			byte[] payload = BitConverter.GetBytes(ticks);
			outgoing.EnqueueCritical(new TcpFrame(TcpFrameKind.Ping,
				Interlocked.Increment(ref sequence), payload, payload.Length * 8));
		}
	}

	private void UpdateRoundTrip(TcpFrame frame)
	{
		if (frame.Payload.Length < 8) return;
		long sentTicks = BitConverter.ToInt64(frame.Payload, 0);
		double current = Math.Max(0, (DateTime.UtcNow.Ticks - sentTicks) / (double)TimeSpan.TicksPerMillisecond);
		lock (statsLock)
		{
			jitterMs = roundTripMs <= 0 ? 0 : jitterMs * 0.8 + Math.Abs(current - roundTripMs) * 0.2;
			roundTripMs = current;
		}
	}

	private static bool TryGetLatestStateKey(Packet packet, out long key)
	{
		int rocketId;
		if (packet is Packet_UpdateRocketPrimary primary) rocketId = primary.RocketId;
		else if (packet is Packet_UpdateRocketSecondary secondary) rocketId = secondary.RocketId;
		else
		{
			key = 0;
			return false;
		}
		key = ((long)(int)packet.Type << 32) | (uint)rocketId;
		return true;
	}

	public void Dispose()
	{
		Disconnect("Transport disposed");
	}
}
