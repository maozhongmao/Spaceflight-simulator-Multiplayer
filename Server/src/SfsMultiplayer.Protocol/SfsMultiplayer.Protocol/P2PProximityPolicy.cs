namespace SfsMultiplayer.Protocol;

public static class P2PProximityPolicy
{
    public static bool IsEligible(NetLocation first, NetLocation second, double thresholdMeters)
    {
        if (!double.IsFinite(thresholdMeters) || thresholdMeters < 0)
            return false;
        if (!string.Equals(first.Address, second.Address, StringComparison.Ordinal))
            return false;
        var dx = second.X - first.X;
        var dy = second.Y - first.Y;
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) return false;
        return dx * dx + dy * dy <= thresholdMeters * thresholdMeters;
    }
}
