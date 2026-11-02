using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Domain.Tests.Support;
using Ascua.Domain.Views;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests.Heroes;

/// <summary>El Campanero (05-heroes.md §12): «El reloj es igual para todos». El reloj en sí es del servidor.</summary>
public sealed class CampaneroTests
{
    private static Hero Campanero(int x = 4, int y = 4, int seat = 0) => Hero(A, HeroClass.Campanero, x, y, seat);

    private static Hero Other(PlayerId player, int seat) => Hero(player, HeroClass.Mampostero, seat, 8, seat);

    private static readonly IReadOnlySet<PlayerId> Everyone = new HashSet<PlayerId> { A, B, C };

    [Fact]
    public void Bell_rings_for_the_next_round_and_everybody_hears_it()
    {
        var state = State([Campanero(), Other(B, 1)]);

        var (next, events) = Resolve(state, (A, Skill(Direction.North)), (B, Interact(Direction.East)));

        Assert.Equal(2, next.BellRound);
        Assert.Equal(A, next.BellRinger);
        Assert.True(next.IsBellRound);
        Assert.Contains(events.SeenBy(next, B), e => e is BellRung { ForRound: 2 });
        Assert.Equal(HeroCatalog.For(HeroClass.Campanero).SkillCooldown - 1, next.H(A).SkillCooldown);
        Assert.Equal(A, PlayerViewProjector.For(next, B).BellRinger);
    }

    [Fact]
    public void Alone_the_bell_does_nothing_and_keeps_the_cooldown()
    {
        var (next, _) = Resolve(State([Campanero()]), (A, Skill(Direction.North)));

        Assert.Null(next.BellRinger);
        Assert.Equal(0, next.H(A).SkillCooldown);
    }

    [Fact]
    public void The_bell_lasts_one_round()
    {
        var (rung, _) = Resolve(State([Campanero(), Other(B, 1)]), (A, Skill(Direction.North)), (B, Interact(Direction.East)));

        var (after, _) = Resolve(rung, (A, Order.DefaultGuard), (B, Order.DefaultGuard));

        Assert.False(after.IsBellRound);
        Assert.Null(after.BellRinger);
        Assert.Equal(0, after.BellRound);
    }

    [Fact]
    public void Closing_the_bell_only_works_in_a_bell_round_and_is_public()
    {
        var quiet = State([Campanero(), Other(B, 1)]);
        Assert.Empty(MatchRules.CloseBell(quiet).Events);

        var (rung, _) = Resolve(quiet, (A, Skill(Direction.North)), (B, Interact(Direction.East)));
        var closed = MatchRules.CloseBell(rung);

        Assert.True(closed.State.BellClosed);
        Assert.Contains(closed.Events.SeenBy(closed.State, B), e => e is BellExpired);
        Assert.True(PlayerViewProjector.For(closed.State, B).BellClosed);
        Assert.Empty(MatchRules.CloseBell(closed.State).Events);
    }

    [Fact]
    public void If_the_bell_ringer_falls_before_the_next_round_the_bell_does_not_ring()
    {
        var hurt = Campanero() with { Hp = 1 };
        var striker = Enemy(1, EnemyKind.Scavenger, 4, 3) with { Telegraph = [At(4, 4)] };

        var (next, _) = Resolve(State([hurt, Other(B, 1)], [striker]), (A, Skill(Direction.North)), (B, Interact(Direction.East)));

        Assert.Equal(HeroStatus.Down, next.H(A).Status);
        Assert.Null(next.BellRinger);
    }

    [Fact]
    public void As_a_renegade_the_bell_still_rings_and_costs_double()
    {
        var renegade = PresentRenegade(A, HeroClass.Campanero, 4, 4);

        var (next, _) = Resolve(State([renegade, Other(B, 1)]), (A, Skill(Direction.North)), (B, Interact(Direction.East)));

        Assert.True(next.IsBellRound);
        Assert.Equal((HeroCatalog.For(HeroClass.Campanero).SkillCooldown * 2) - 1, next.H(A).SkillCooldown);
    }

    [Fact]
    public void As_an_ember_there_is_no_bell_and_no_last_word()
    {
        var ember = Ember(A, 4, 4, heroClass: HeroClass.Campanero);
        var state = State([ember, Other(B, 1)]);

        var (next, _) = Resolve(state, (A, Skill(Direction.North)), (B, Interact(Direction.East)));

        Assert.Null(next.BellRinger);
        Assert.Null(LastWord.Listener(state));
    }

    [Fact]
    public void If_all_the_others_decided_he_hears_their_orders()
    {
        var state = State([Campanero(), Other(B, 1), Other(C, 2)]);
        var attack = Attack(Direction.East);
        var move = Move(Direction.North);

        Assert.Null(LastWord.Heard(state, new Dictionary<PlayerId, Order> { [B] = attack }, Everyone));

        var heard = LastWord.Heard(state, new Dictionary<PlayerId, Order> { [B] = attack, [C] = move }, Everyone);

        Assert.Equal([(B, attack), (C, move)], heard);
    }

    [Fact]
    public void If_he_already_decided_he_hears_nothing()
    {
        var state = State([Campanero(), Other(B, 1)]);

        Assert.Null(LastWord.Heard(state, new Dictionary<PlayerId, Order> { [A] = Order.DefaultGuard, [B] = Move(Direction.North) }, Everyone));
    }

    [Fact]
    public void Someone_in_their_grace_period_does_not_count_for_everybody_decided()
    {
        var state = State([Campanero(), Other(B, 1), Other(C, 2)]);
        var connected = new HashSet<PlayerId> { A, B };

        var heard = LastWord.Heard(state, new Dictionary<PlayerId, Order> { [B] = Move(Direction.North) }, connected);

        Assert.NotNull(heard);
        Assert.Equal(Order.DefaultGuard, heard!.Single(h => h.Player == C).Order);
    }

    [Fact]
    public void With_nothing_decided_there_is_nothing_to_hear()
    {
        var state = State([Campanero(), Other(B, 1)]);

        Assert.Null(LastWord.Heard(state, new Dictionary<PlayerId, Order>(), new HashSet<PlayerId> { A }));
    }

    [Fact]
    public void Once_the_bell_deadline_passes_those_who_did_not_decide_count_as_guarding()
    {
        var state = State([Campanero(), Other(B, 1), Other(C, 2)]) with { BellRound = 1, BellRinger = A, BellClosed = true };

        var heard = LastWord.Heard(state, new Dictionary<PlayerId, Order> { [B] = Move(Direction.North) }, Everyone);

        Assert.Equal(Order.DefaultGuard, heard!.Single(h => h.Player == C).Order);

        // Aunque nadie llegara: sabe que todos guardan.
        var nobody = LastWord.Heard(state, new Dictionary<PlayerId, Order>(), Everyone);
        Assert.All(nobody!, h => Assert.Equal(Order.DefaultGuard, h.Order));
    }

    [Fact]
    public void His_order_still_resolves_with_the_others()
    {
        var totem = Enemy(1, EnemyKind.Totem, 4, 2);

        var (next, _) = Resolve(State([Campanero(), Other(B, 1)], [totem]), (A, Attack(Direction.North)), (B, Interact(Direction.East)));

        Assert.Equal(2, next.E(1)!.Hp);
    }
}
