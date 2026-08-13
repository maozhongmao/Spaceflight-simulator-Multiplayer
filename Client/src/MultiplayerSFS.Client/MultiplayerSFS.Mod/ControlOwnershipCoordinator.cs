using System;

namespace MultiplayerSFS.Mod;

public enum ControlRequestOrigin
{
    NativeSelection,
    TopologyCommit,
    UserAction,
}

public sealed class ControlOwnershipCoordinator
{
    public int ConfirmedRocketId { get; private set; } = -1;

    public int PendingRocketId { get; private set; } = -1;

    public ControlRequestOrigin? PendingOrigin { get; private set; }

    public bool Request(int rocketId, ControlRequestOrigin origin)
    {
        if (rocketId < 0 || rocketId == ConfirmedRocketId)
            return false;

        PendingRocketId = rocketId;
        PendingOrigin = origin;
        return true;
    }

    public bool ApplyServerConfirmation(int rocketId)
    {
        ConfirmedRocketId = rocketId;
        PendingRocketId = -1;
        PendingOrigin = null;
        return true;
    }

    public void Clear()
    {
        ConfirmedRocketId = -1;
        PendingRocketId = -1;
        PendingOrigin = null;
    }

    public bool CanApplyNativeSelection(int selectedRocketId)
    {
        return selectedRocketId >= 0 && selectedRocketId == ConfirmedRocketId;
    }
}
