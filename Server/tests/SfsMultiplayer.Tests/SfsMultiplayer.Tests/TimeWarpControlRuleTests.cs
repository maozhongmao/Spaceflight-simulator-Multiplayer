using SfsMultiplayer.Protocol;

namespace SfsMultiplayer.Tests;

public sealed class TimeWarpControlRuleTests
{
    [Fact]
    public void AllowsFiniteMultiplierInsideRangeWhenExactlyOnePlayerControls()
    {
        Assert.True(TimeWarpControlRules.CanSet(1, 12.5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void RejectsAccelerationUnlessExactlyOnePlayerControls(int controllers)
    {
        Assert.False(TimeWarpControlRules.CanSet(controllers, 12.5));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(2)]
    public void AllowsOneXRegardlessOfControllerCount(int controllers)
    {
        Assert.True(TimeWarpControlRules.CanSet(controllers, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2500.01)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsMultiplierOutsideRange(double multiplier)
    {
        Assert.False(TimeWarpControlRules.CanSet(1, multiplier));
    }
}
