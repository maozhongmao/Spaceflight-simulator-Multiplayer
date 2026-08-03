namespace MultiplayerSFS.Common;

public enum PacketType
{
	JoinRequest = 0,
	JoinResponse = 1,
	PlayerConnected = 2,
	PlayerDisconnected = 3,
	UpdatePlayerControl = 4,
	UpdatePlayerAuthority = 5,
	UpdateWorldTime = 6,
	UpdatePlayerColor = 7,
	SendChatMessage = 8,
	CreateRocket = 9,
	DestroyRocket = 10,
	UpdateRocketPrimary = 11,
	UpdateRocketSecondary = 12,
	DestroyPart = 13,
	UpdateStaging = 14,
	UpdatePart_EngineModule = 15,
	UpdatePart_WheelModule = 16,
	UpdatePart_BoosterModule = 17,
	UpdatePart_ParachuteModule = 18,
	UpdatePart_MoveModule = 19,
	UpdatePart_ResourceModule = 20,

	// Client-only extension IDs. The current server uses 0-20.
	ShowToastMessage = 21,
	UpdateCheatStatus = 22,
	DockTransaction = 23,
	TimeWarp = 24
}
