using System.Net;

namespace MultiplayerSFS.Mod;

public class JoinInfo
{
	public IPAddress address = IPAddress.Parse("127.0.0.1");

	public int port = 9806;

	public string username = "DEFAULT_USERNAME";

	public string password = "";
}
