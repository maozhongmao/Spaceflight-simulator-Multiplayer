using System;

namespace MultiplayerSFS.Common;

public static class P2PStateOrderPolicy
{
    public static bool ShouldAccept(long currentGeneration, long currentSequence,
        long incomingGeneration, long incomingSequence)
    {
        if (incomingGeneration != currentGeneration)
            return incomingGeneration > currentGeneration;
        return incomingSequence > currentSequence;
    }
}

public static class P2PProximityPolicy
{
    public static bool IsEligible(NetLocation first, NetLocation second, double thresholdMeters)
    {
        if (first == null || second == null || !IsFinite(thresholdMeters) || thresholdMeters < 0)
            return false;
        if (!string.Equals(first.address, second.address, StringComparison.Ordinal))
            return false;
        var dx = second.position.x - first.position.x;
        var dy = second.position.y - first.position.y;
        if (!IsFinite(dx) || !IsFinite(dy)) return false;
        return dx * dx + dy * dy <= thresholdMeters * thresholdMeters;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}

public static class P2PTransitionPolicy
{
    public static bool ShouldFallback(DateTime nowUtc, DateTime lastPacketUtc, double timeoutSeconds)
    {
        if (!IsFinite(timeoutSeconds) || timeoutSeconds < 0) return true;
        return nowUtc - lastPacketUtc >= TimeSpan.FromSeconds(timeoutSeconds);
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
