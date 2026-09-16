using Lidgren.Network;

namespace MultiplayerSFS.Common;

public sealed class Packet_ExperimentalAccess : Packet
{
    public bool Request { get; set; }
    public string Passphrase { get; set; } = string.Empty;
    public bool Granted { get; set; }
    public string Message { get; set; } = string.Empty;

    public override PacketType Type => PacketType.ExperimentalAccess;

    public override void Serialize(NetOutgoingMessage msg)
    {
        msg.Write(Request);
        msg.Write(Passphrase ?? string.Empty);
        msg.Write(Granted);
        msg.Write(Message ?? string.Empty);
    }

    public override void Deserialize(NetIncomingMessage msg)
    {
        Request = msg.ReadBoolean();
        Passphrase = msg.ReadString() ?? string.Empty;
        Granted = msg.ReadBoolean();
        Message = msg.ReadString() ?? string.Empty;
    }
}
