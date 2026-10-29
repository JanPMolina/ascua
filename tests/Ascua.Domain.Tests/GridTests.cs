using Ascua.Domain.Model;

namespace Ascua.Domain.Tests;

public sealed class GridTests
{
    [Theory]
    [InlineData(Direction.North, 4, 3)]
    [InlineData(Direction.South, 4, 5)]
    [InlineData(Direction.East, 5, 4)]
    [InlineData(Direction.West, 3, 4)]
    public void Step_moves_one_tile(Direction direction, int expectedX, int expectedY)
    {
        var next = new Position(4, 4).Step(direction);

        Assert.Equal(new Position(expectedX, expectedY), next);
    }

    [Fact]
    public void Guard_without_direction_is_well_formed() =>
        Assert.True(Order.DefaultGuard.IsWellFormed());

    [Fact]
    public void Move_without_direction_is_rejected() =>
        Assert.False(new Order(OrderType.Move, null).IsWellFormed());
}
