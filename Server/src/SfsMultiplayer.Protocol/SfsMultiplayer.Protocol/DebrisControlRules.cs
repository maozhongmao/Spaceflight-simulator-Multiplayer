namespace SfsMultiplayer.Protocol;

public static class DebrisControlRules
{
    public const int DefaultAutoRemoveMaxParts = 5;

    public static bool ShouldAutoRemove(bool forLaunch, int partCount, int maxParts = DefaultAutoRemoveMaxParts)
    {
        return !forLaunch && maxParts >= 0 && partCount >= 0 && partCount <= maxParts;
    }
}
