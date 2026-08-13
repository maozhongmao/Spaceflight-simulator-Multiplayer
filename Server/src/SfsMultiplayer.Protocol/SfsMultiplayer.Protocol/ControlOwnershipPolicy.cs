namespace SfsMultiplayer.Protocol;

public static class ControlOwnershipPolicy
{
    public static int ResolveUndockControl(int currentRocketId, int retainedRocketId)
    {
        return currentRocketId == retainedRocketId ? retainedRocketId : currentRocketId;
    }
}
