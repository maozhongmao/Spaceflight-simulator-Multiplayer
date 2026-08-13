using SfsMultiplayer.Protocol;

namespace SfsMultiplayer.Tests;

public sealed class ControlOwnershipPolicyTests
{
    [Fact]
    public void AuthorityAllocationPrefersControllerThenUsesStableRoundRobin()
    {
        var assignments = AuthorityAllocationPolicy.Allocate(
            new[] { 10, 20, 30 },
            new[]
            {
                new AuthorityParticipant(2, 20),
                new AuthorityParticipant(1, -1),
            });

        Assert.Equal(1, assignments[10]);
        Assert.Equal(2, assignments[20]);
        Assert.Equal(2, assignments[30]);
    }

    [Fact]
    public void UndockPreservesCurrentController()
    {
        Assert.Equal(7, ControlOwnershipPolicy.ResolveUndockControl(7, 7));
    }

    [Fact]
    public void UndockDoesNotMoveUnrelatedController()
    {
        Assert.Equal(9, ControlOwnershipPolicy.ResolveUndockControl(9, 7));
    }
}