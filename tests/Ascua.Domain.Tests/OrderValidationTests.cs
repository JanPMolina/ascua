using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Domain.Tests.Support;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests;

public sealed class OrderValidationTests
{
    private static GameState Match() => State([Hero(A, HeroClass.Errata, 4, 4)], round: 5);

    [Fact]
    public void A_well_formed_order_for_the_open_round_is_accepted() =>
        Assert.Null(OrderValidator.Validate(Match(), A, 5, Move(Direction.North)));

    public static TheoryData<Order?> MalformedOrders => new()
    {
        null,
        new Order(OrderType.Move, null),
        new Order(OrderType.Guard, Direction.North),
        new Order((OrderType)99, Direction.North),
        new Order(OrderType.Attack, (Direction)42),
    };

    [Theory]
    [MemberData(nameof(MalformedOrders))]
    public void Malformed_orders_are_rejected_with_a_stable_code(Order? order)
    {
        var error = OrderValidator.Validate(Match(), A, 5, order);

        Assert.Equal("INVALID_ORDER", error?.Code);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void Orders_for_another_round_are_stale(int round) =>
        Assert.Equal("STALE_ROUND", OrderValidator.Validate(Match(), A, round, Order.DefaultGuard)?.Code);

    [Fact]
    public void Strangers_cannot_send_orders() =>
        Assert.Equal("NOT_A_PARTICIPANT", OrderValidator.Validate(Match(), new PlayerId("mallory"), 5, Order.DefaultGuard)?.Code);

    [Fact]
    public void Orders_are_rejected_outside_of_a_running_match() =>
        Assert.Equal(
            "MATCH_NOT_IN_PROGRESS",
            OrderValidator.Validate(Match() with { Phase = MatchPhase.Drafting }, A, 5, Order.DefaultGuard)?.Code);

    [Fact]
    public void The_resolver_replaces_invalid_orders_with_guard()
    {
        var (_, events) = Resolve(Match(), (A, new Order(OrderType.Move, null)));

        Assert.Contains(events, e => e is HeroGuarded);
        Assert.DoesNotContain(events, e => e is HeroMoved or MoveBounced);
    }

    [Fact]
    public void Events_carry_increasing_sequence_numbers_that_continue_the_log()
    {
        var state = Match() with { LastSeq = 41 };

        var (next, events) = Resolve(state, (A, Move(Direction.North)));

        Assert.Equal(Enumerable.Range(42, events.Count).Select(i => (long)i), events.Select(e => e.Seq));
        Assert.All(events, e => Assert.Equal(5, e.Round));
        Assert.Equal(events[^1].Seq, next.LastSeq);
        Assert.IsType<RoundResolved>(events[^1]);
        Assert.Equal(6, next.Round);
    }
}
