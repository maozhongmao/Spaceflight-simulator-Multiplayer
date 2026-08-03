using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MultiplayerSFS.Common;

public sealed class UdpClientTransport : IDisposable
{
	private const byte Bind = 1;
	private const byte BindAck = 2;
	private const byte Data = 3;
	private readonly Action<TcpFrame> receivePacket;
	private UdpClient socket;
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
		if (string.IsNullOrEmpty(sessionToken)) return;
		token = sessionToken;
		socket = new UdpClient(address.AddressFamily);
		socket.Connect(new IPEndPoint(address, port));
		cancellation = new CancellationTokenSource();
		heartbeatCancellation = new CancellationTokenSource();
		Send(Bind, Array.Empty<byte>());
		Task.Run(() => ReceiveLoop(cancellation.Token));
		Task.Run(() => BindHeartbeatLoop(heartbeatCancellation.Token));
	}

	public void SendPacket(Packet packet)
	{
		if (!bound || packet == null) return;
		NetPayload payload = NetPayloadCodec.Serialize(packet, true);
		Send(Data, payload.Data);
	}

	private void Send(byte kind, byte[] payload)
	{
		if (socket == null || string.IsNullOrEmpty(token)) return;
		byte[] tokenBytes = Encoding.UTF8.GetBytes(token);
		byte[] data = new byte[2 + tokenBytes.Length + payload.Length];
		data[0] = kind;
		data[1] = (byte)tokenBytes.Length;
		Buffer.BlockCopy(tokenBytes, 0, data, 2, tokenBytes.Length);
		Buffer.BlockCopy(payload, 0, data, 2 + tokenBytes.Length, payload.Length);
		try { socket.Send(data, data.Length); } catch { }
	}

	private async Task BindHeartbeatLoop(CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				Send(Bind, Array.Empty<byte>());
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
	}
}
