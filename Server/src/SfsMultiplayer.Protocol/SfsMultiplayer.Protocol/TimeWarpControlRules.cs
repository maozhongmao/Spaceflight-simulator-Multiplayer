namespace SfsMultiplayer.Protocol;

public static class TimeWarpControlRules
{
    public const double MinimumMultiplier = 1.0;
    public const double MaximumMultiplier = 2500.0;

    public static bool CanSet(int controllingPlayers, double multiplier)
    {
        if (!double.IsFinite(multiplier) || multiplier < MinimumMultiplier || multiplier > MaximumMultiplier)
            return false;
        return multiplier == 1.0 || controllingPlayers == 1;
    }
}
