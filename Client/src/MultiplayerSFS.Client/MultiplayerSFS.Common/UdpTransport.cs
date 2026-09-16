using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MultiplayerSFS.Common;

public sealed class P2PRawDatagram
{
	public IPEndPoint RemoteEndPoint { get; }
	public byte[] Data { get; }

	public P2PRawDatagram(IPEndPoint remoteEndPoint, byte[] data)
	{
		RemoteEndPoint = remoteEndPoint;
		Data = data ?? Array.Empty<byte>();
	}
}

public sealed class UdpClientTransport : IDisposable
{
	private const byte Bind = 1;
	private const byte BindAck = 2;
	private const byte Data = 3;
	public const byte P2PMagic = 0xA7;

	private readonly Action<TcpFrame> receivePacket;
	private readonly object socketLock = new object();
	private readonly object peerLock = new object();
	private readonly Queue<P2PRawDatagram> peerDatagrams = new Queue<P2PRawDatagram>();
	private UdpClient socket;
	private IPEndPoint serverEndpoint;
	private CancellationTokenSource cancellation;
	private CancellationTokenSource heartbeatCancellation;
	private string token;
	private int sequence;
	private volatile bool bound;

	public bool Bound => bound;

	public UdpClientTransport(Action<TcpFrame> receivePacket)
	{
		this.receivePacket = receivePacket;
	}

	public void Start(IPAddress address, int port, string sessionToken)
	{
		if (address == null || port < 1 || port > 65535 || string.IsNullOrEmpty(sessionToken)) return;
		token = sessionToken;
		serverEndpoint = new IPEndPoint(address, port);
		socket = new UdpClient(address.AddressFamily);
		cancellation = new CancellationTokenSource();
		heartbeatCancellation = new CancellationTokenSource();
		SendServer(Bind, Array.Empty<byte>());
		Task.Run(() => ReceiveLoop(cancellation.Token));
		Task.Run(() => BindHeartbeatLoop(heartbeatCancellation.Token));
	}

	public void SendPacket(Packet packet)
	{
		if (!bound || packet == null) return;
		NetPayload payload = NetPayloadCodec.Serialize(packet, true);
		SendServer(Data, payload.Data);
	}

	public bool TrySendPeer(IPEndPoint endpoint, byte[] data)
	{
		if (endpoint == null || data == null || data.Length == 0) return false;
		try
		{
			lock (socketLock)
			{
				if (socket == null) return false;
				socket.Send(data, data.Length, endpoint);
				return true;
			}
		}
		catch
		{
			return false;
		}
	}

	public bool TryReceivePeer(out P2PRawDatagram datagram)
	{
		lock (peerLock)
		{
			if (peerDatagrams.Count > 0)
			{
				datagram = peerDatagrams.Dequeue();
				return true;
			}
		}
		datagram = null;
		return false;
	}

	private void SendServer(byte kind, byte[] payload)
	{
		if (serverEndpoint == null || string.IsNullOrEmpty(token)) return;
		byte[] tokenBytes = Encoding.UTF8.GetBytes(token);
		if (tokenBytes.Length > byte.MaxValue) return;
		byte[] data = new byte[2 + tokenBytes.Length + payload.Length];
		data[0] = kind;
		data[1] = (byte)tokenBytes.Length;
		Buffer.BlockCopy(tokenBytes, 0, data, 2, tokenBytes.Length);
		Buffer.BlockCopy(payload, 0, data, 2 + tokenBytes.Length, payload.Length);
		TrySendPeer(serverEndpoint, data);
	}

	private async Task BindHeartbeatLoop(CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				SendServer(Bind, Array.Empty<byte>());
				await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) { }
	}

	private async Task ReceiveLoop(CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				UdpReceiveResult result = await socket.ReceiveAsync().ConfigureAwait(false);
				byte[] data = result.Buffer;
				if (data.Length == 0) continue;
				if (data[0] == P2PMagic)
				{
					lock (peerLock)
					{
						if (peerDatagrams.Count < 512)
							peerDatagrams.Enqueue(new P2PRawDatagram(result.RemoteEndPoint, data));
					}
					continue;
				}
				if (data.Length < 2) continue;
				int tokenLength = data[1];
				if (data.Length < 2 + tokenLength) continue;
				string receivedToken = Encoding.UTF8.GetString(data, 2, tokenLength);
				if (!string.Equals(receivedToken, token, StringComparison.Ordinal)) continue;
				if (data[0] == BindAck) { bound = true; continue; }
				if (data[0] != Data) continue;
				int offset = 2 + tokenLength;
				int length = data.Length - offset;
				if (length == 0) continue;
				byte[] payload = new byte[length];
				Buffer.BlockCopy(data, offset, payload, 0, length);
				receivePacket(new TcpFrame(TcpFrameKind.Packet, Interlocked.Increment(ref sequence), payload, payload.Length * 8));
			}
		}
		catch { }
	}

	public void Dispose()
	{
		bound = false;
		try { cancellation?.Cancel(); } catch { }
		try { heartbeatCancellation?.Cancel(); } catch { }
		try { socket?.Close(); } catch { }
		lock (peerLock) peerDatagrams.Clear();
	}
}
