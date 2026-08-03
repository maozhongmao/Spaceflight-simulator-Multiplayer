using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SfsMultiplayer.Server;

internal sealed class UdpStateTransport : IDisposable
{
    internal const byte Bind = 1;
    internal const byte BindAck = 2;
    internal const byte Data = 3;
    private readonly UdpClient socket;
    private readonly Func<string, IPEndPoint, byte[], bool> receive;
    private CancellationTokenSource? cancellation;

    public UdpStateTransport(int port, Func<string, IPEndPoint, byte[], bool> receive)
    {
        socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        this.receive = receive;
    }

    public void Start()
    {
        var token = new CancellationTokenSource();
        cancellation = token;
        Task.Run(() => ReceiveLoop(token.Token));
    }

    private async Task ReceiveLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync().ConfigureAwait(false);
                var data = result.Buffer;
                if (data.Length < 2) continue;
                var tokenLength = data[1];
                if (data.Length < 2 + tokenLength) continue;
                var token = Encoding.UTF8.GetString(data, 2, tokenLength);
                var payloadOffset = 2 + tokenLength;
                var payload = new byte[data.Length - payloadOffset];
                Buffer.BlockCopy(data, payloadOffset, payload, 0, payload.Length);
                if (!receive(token, result.RemoteEndPoint, payload)) continue;
                if (data[0] == Bind) Send(result.RemoteEndPoint, token, BindAck, Array.Empty<byte>());
            }
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    public void Send(IPEndPoint endpoint, string token, byte[] payload)
    {
        if (endpoint != null) Send(endpoint, token, Data, payload);
    }

    private void Send(IPEndPoint endpoint, string token, byte kind, byte[] payload)
    {
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        var data = new byte[2 + tokenBytes.Length + payload.Length];
        data[0] = kind; data[1] = (byte)tokenBytes.Length;
        Buffer.BlockCopy(tokenBytes, 0, data, 2, tokenBytes.Length);
        Buffer.BlockCopy(payload, 0, data, 2 + tokenBytes.Length, payload.Length);
        try { socket.Send(data, data.Length, endpoint); } catch { }
    }

    public void Dispose()
    {
        try { cancellation?.Cancel(); } catch { }
        try { socket.Close(); } catch { }
    }
}
