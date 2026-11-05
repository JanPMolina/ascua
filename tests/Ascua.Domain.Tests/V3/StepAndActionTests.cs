using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Domain.Tests.Support;
using Ascua.Domain.Views;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests.V3;

/// <summary>06-version-3.md §1: un paso opcional y una acción opcional en la misma ronda.</summary>
public class StepAndActionTests
{
    private static Order WithStep(Order order, Direction step) => order with { Step = step };

    [Theory]
    [InlineData(OrderType.Attack)]
    [InlineData(OrderType.Skill)]
    [InlineData(OrderType.Interact)]
    [InlineData(OrderType.Sabotage)]
    [InlineData(OrderType.Push)]
    public void Actions_accept_a_step(OrderType type)
    {
        var state = State([Hero(A, HeroClass.Mampostero, 4, 4)]);
        var order = new Order(type, Direction.North) { Step = Direction.East };

        Assert.Null(OrderValidator.Validate(state, A, 1, order));
    }

    [Fact]
    public void Guard_does_not_accept_a_step_and_move_is_only_a_step()
    {
        var state = State([Hero(A, HeroClass.Mampostero, 4, 4)]);

        Assert.Equal(ErrorCode.InvalidOrder, OrderValidator.Validate(state, A, 1, Order.DefaultGuard with { Step = Direction.East })!.Kind);
        Assert.Equal(ErrorCode.InvalidOrder, OrderValidator.Validate(state, A, 1, Move(Direction.North) with { Step = Direction.East })!.Kind);
        Assert.Equal(Direction.North, Move(Direction.North).Walk);
    }

    [Fact]
    public void The_step_happens_before_the_action_and_the_attack_comes_from_the_new_tile()
    {
        var hero = Hero(A, HeroClass.Mampostero, 4, 4);
        var enemy = Enemy(1, EnemyKind.Scavenger, 5, 3);

        var (next, events) = Resolve(State([hero], [enemy]), (A, WithStep(Attack(Direction.North), Direction.East)));

        Assert.Equal(At(5, 4), next.H(A).Position);
        Assert.Equal(1, next.E(1)!.Hp);
        var moved = events.OfType<HeroMoved>().Single();
        var attacked = events.OfType<HeroAttacked>().Single();
        Assert.True(moved.Seq < attacked.Seq);
        Assert.Equal(RoundPhase.Movement, moved.Phase);
        Assert.Equal(RoundPhase.Actions, attacked.Phase);
    }

    [Fact]
    public void A_bounced_step_keeps_the_action_from_the_original_tile()
    {
        var hero = Hero(A, HeroClass.Mampostero, 4, 4);
        var room = EmptyRoom().WithTerrain(At(5, 4), Terrain.Wall);
        var enemy = Enemy(1, EnemyKind.Scavenger, 4, 3);

        var (next, events) = Resolve(State([hero], [enemy], room), (A, WithStep(Attack(Direction.North), Direction.East)));

        Assert.Equal(At(4, 4), next.H(A).Position);
        Assert.Contains(events, e => e is MoveBounced);
        Assert.Equal(1, next.E(1)!.Hp);
    }

    [Fact]
    public void Two_steps_to_the_same_tile_bounce_and_both_still_act()
    {
        var a = Hero(A, HeroClass.Mampostero, 3, 4, seat: 0);
        var b = Hero(B, HeroClass.Errata, 5, 4, seat: 1);
        var enemy = Enemy(1, EnemyKind.Scavenger, 3, 3);

        var (next, _) = Resolve(State([a, b], [enemy]), (A, WithStep(Attack(Direction.North), Direction.East)), (B, Move(Direction.West)));

        Assert.Equal(At(3, 4), next.H(A).Position);
        Assert.Equal(At(5, 4), next.H(B).Position);
        Assert.Equal(1, next.E(1)!.Hp);
    }

    [Fact]
    public void A_crack_under_the_step_kills_and_there_is_no_action()
    {
        var hero = Hero(A, HeroClass.Errata, 4, 4);
        var other = Hero(B, HeroClass.Mampostero, 0, 8, seat: 1);
        var room = EmptyRoom().WithTerrain(At(5, 4), Terrain.Crack);
        var enemy = Enemy(1, EnemyKind.Scavenger, 5, 3);

        var (next, events) = Resolve(State([hero, other], [enemy], room), (A, WithStep(Attack(Direction.North), Direction.East)));

        Assert.Equal(HeroStatus.Extinct, next.H(A).Status);
        Assert.DoesNotContain(events, e => e is HeroAttacked);
        Assert.Equal(2, next.E(1)!.Hp);
    }

    [Fact]
    public void Frozen_loses_the_whole_order_including_the_step()
    {
        var hero = Hero(A, HeroClass.Errata, 4, 4) with { Frozen = true };

        var (next, _) = Resolve(State([hero]), (A, WithStep(Attack(Direction.North), Direction.East)));

        Assert.Equal(At(4, 4), next.H(A).Position);
    }

    [Fact]
    public void Hilvan_steps_in_mirror_and_acts_from_where_each_body_ended()
    {
        var hilvan = Hero(A, HeroClass.Hilvan, 3, 4) with { SecondBody = At(5, 4) };
        var north = Enemy(1, EnemyKind.Scavenger, 2, 3);
        var south = Enemy(2, EnemyKind.Scavenger, 6, 5);

        // Paso al oeste: el principal a (2,4) y el segundo, en espejo, a (6,4). Ataque al norte: los dos van al norte.
        var (next, _) = Resolve(State([hilvan], [north, south]), (A, WithStep(Attack(Direction.North), Direction.West)));

        Assert.Equal(At(2, 4), next.H(A).Position);
        Assert.Equal(At(6, 4), next.H(A).SecondBody);
        Assert.Equal(1, next.E(1)!.Hp);
        Assert.Equal(2, next.E(2)!.Hp);
    }

    [Fact]
    public void Polizon_inside_moves_the_host_and_strikes_from_the_new_tile()
    {
        var host = Enemy(1, EnemyKind.Scavenger, 4, 4);
        var prey = Enemy(2, EnemyKind.Scavenger, 5, 3);
        var polizon = Hero(A, HeroClass.Polizon, 4, 4) with { HostEnemyId = 1, BoardedRound = 1 };

        var (next, _) = Resolve(State([polizon], [host, prey]), (A, WithStep(Attack(Direction.North), Direction.East)));

        Assert.Equal(At(5, 4), next.E(1)!.Position);
        Assert.Equal(At(5, 4), next.H(A).Position);
        Assert.Equal(1, next.E(2)!.Hp);
    }

    [Fact]
    public void An_ember_drifts_and_uses_another_order_in_the_same_round()
    {
        var ember = Ember(A, 4, 4);
        var ally = Hero(B, HeroClass.Mampostero, 7, 4, seat: 1) with { Status = HeroStatus.Down, Hp = 0 };

        // Flotar 2 al este queda junto al caído; Revivir desde allí.
        var (next, _) = Resolve(State([ember, ally]), (A, WithStep(Interact(Direction.East), Direction.East)));

        Assert.Equal(At(6, 4), next.H(A).Position);
        Assert.Equal(HeroStatus.Active, next.H(B).Status);
    }

    [Fact]
    public void An_ember_cannot_sabotage_or_push_and_a_renegade_cannot_sabotage()
    {
        var ember = Ember(A, 4, 4);
        var renegade = PresentRenegade(B, HeroClass.Mampostero, 1, 1, seat: 1);
        var state = State([ember, renegade]);

        Assert.Equal(ErrorCode.InvalidOrder, OrderValidator.Validate(state, A, 1, Sabotage(Direction.North))!.Kind);
        Assert.Equal(ErrorCode.InvalidOrder, OrderValidator.Validate(state, A, 1, Push(Direction.North))!.Kind);
        Assert.Equal(ErrorCode.InvalidOrder, OrderValidator.Validate(state, B, 1, Sabotage(Direction.North))!.Kind);
        Assert.Null(OrderValidator.Validate(state, B, 1, Push(Direction.North)));
    }

    [Fact]
    public void The_echo_repeats_from_where_she_was_after_the_step()
    {
        var anteayer = Hero(A, HeroClass.Anteayer, 4, 4);

        var (next, _) = Resolve(State([anteayer]), (A, WithStep(Attack(Direction.North), Direction.East)));

        var echo = Assert.Single(next.H(A).EchoQueue);
        Assert.Equal(At(5, 4), echo.Origin);
        Assert.Equal(Direction.North, echo.Direction);
    }

    [Fact]
    public void Rewind_happens_first_and_the_step_goes_from_the_landing_tile()
    {
        var anteayer = Hero(A, HeroClass.Anteayer, 4, 4) with { History = [new HeroTrace(1, At(1, 1), 4)] };

        var (next, _) = Resolve(State([anteayer], round: 3), (A, WithStep(Skill(Direction.North), Direction.East)));

        Assert.Equal(At(2, 1), next.H(A).Position);
    }

    [Fact]
    public void The_shift_happens_first_and_the_step_goes_from_where_the_room_left_him()
    {
        var mason = Hero(A, HeroClass.Mampostero, 4, 4);

        var (next, _) = Resolve(State([mason]), (A, WithStep(Skill(Direction.East), Direction.South)));

        Assert.Equal(At(5, 5), next.H(A).Position);
    }

    [Fact]
    public void The_last_word_hears_the_step()
    {
        var bell = Hero(A, HeroClass.Campanero, 1, 1);
        var other = Hero(B, HeroClass.Errata, 5, 5, seat: 1);
        var state = State([bell, other]);
        var order = WithStep(Attack(Direction.North), Direction.East);

        var heard = LastWord.Heard(state, new Dictionary<PlayerId, Order> { [B] = order }, new HashSet<PlayerId> { A, B });

        Assert.Equal(Direction.East, Assert.Single(heard!).Order.Step);
    }

    [Fact]
    public void Every_event_of_a_resolution_says_its_phase()
    {
        var hero = Hero(A, HeroClass.Mampostero, 4, 4);
        var enemy = Enemy(1, EnemyKind.Scavenger, 4, 2) with { Telegraph = [At(4, 4)] };

        var (_, events) = Resolve(State([hero], [enemy]), (A, Order.DefaultGuard));

        Assert.All(events, e => Assert.NotNull(e.Phase));
        var phases = events.Select(e => (int)e.Phase!.Value).ToList();
        Assert.Equal(phases.Order(), phases);
    }
}
