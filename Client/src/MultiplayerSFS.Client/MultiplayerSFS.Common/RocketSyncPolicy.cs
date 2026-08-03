namespace MultiplayerSFS.Common;

public static class RocketSyncPolicy
{
	public const int ControlledIntervalMilliseconds = 50;
	public const int UncontrolledMovingIntervalMilliseconds = 200;
	public const int IdleIntervalMilliseconds = 3000;

	public static int GetIntervalMilliseconds(bool controlled, bool moving)
	{
		return GetIntervalMilliseconds(controlled, moving, null);
	}

	public static int GetIntervalMilliseconds(bool controlled, bool moving, NetworkAdaptiveProfile profile)
	{
		if (profile == null)
		{
			if (controlled) return ControlledIntervalMilliseconds;
			return moving ? UncontrolledMovingIntervalMilliseconds : IdleIntervalMilliseconds;
		}
		if (controlled) return profile.ControlledIntervalMilliseconds;
		return moving ? profile.MovingIntervalMilliseconds : profile.IdleIntervalMilliseconds;
	}
}
