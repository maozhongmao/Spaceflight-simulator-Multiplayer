using Lidgren.Network;

namespace MultiplayerSFS.Common;

public class Packet_UpdateCheatStatus : Packet
{
	public bool InfiniteFuel { get; set; }

	public bool NoAtmosphericDrag { get; set; }

	public bool UnbreakableParts { get; set; }

	public bool NoGravity { get; set; }

	public bool NoHeatDamage { get; set; }

	public bool NoBurnMarks { get; set; }

	public bool InfiniteBuildArea { get; set; }

	public bool PartClipping { get; set; }

	public override PacketType Type => PacketType.UpdateCheatStatus;

	public override void Serialize(NetOutgoingMessage msg)
	{
		((NetBuffer)msg).Write(InfiniteFuel);
		((NetBuffer)msg).Write(NoAtmosphericDrag);
		((NetBuffer)msg).Write(UnbreakableParts);
		((NetBuffer)msg).Write(NoGravity);
		((NetBuffer)msg).Write(NoHeatDamage);
		((NetBuffer)msg).Write(NoBurnMarks);
		((NetBuffer)msg).Write(InfiniteBuildArea);
		((NetBuffer)msg).Write(PartClipping);
	}

	public override void Deserialize(NetIncomingMessage msg)
	{
		InfiniteFuel = ((NetBuffer)msg).ReadBoolean();
		NoAtmosphericDrag = ((NetBuffer)msg).ReadBoolean();
		UnbreakableParts = ((NetBuffer)msg).ReadBoolean();
		NoGravity = ((NetBuffer)msg).ReadBoolean();
		NoHeatDamage = ((NetBuffer)msg).ReadBoolean();
		NoBurnMarks = ((NetBuffer)msg).ReadBoolean();
		InfiniteBuildArea = ((NetBuffer)msg).ReadBoolean();
		PartClipping = ((NetBuffer)msg).ReadBoolean();
	}
}
