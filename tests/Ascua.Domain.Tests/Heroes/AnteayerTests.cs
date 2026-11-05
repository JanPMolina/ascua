using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Domain.Tests.Support;
using Ascua.Domain.Views;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests.Heroes;

/// <summary>Anteayer (05-heroes.md §5): «Lo hecho, hecho está».</summary>
public sealed class AnteayerTests
{
    [Fact]
    public void An_attack_repeats_two_rounds_later_from_where_she_was_and_in_that_direction()
    {
        var anteayer = Hero(A, HeroClass.Anteayer, 4, 5);
        var totem = Enemy(1, EnemyKind.Totem, 4, 4);
        var state = State([anteayer], [totem]);

        state = Resolve(state, (A, Attack(Direction.North))).State;
        Assert.Equal(2, state.E(1)!.Hp);
        Assert.Equal(new PendingEcho(3, At(4, 5), Direction.North), Assert.Single(state.H(A).EchoQueue));

        state = Resolve(state, (A, Move(Direction.East))).State;
        Assert.Equal(2, state.E(1)!.Hp);

        var (next, events) = Resolve(state, (A, Order.DefaultGuard));
        Assert.Equal(1, next.E(1)!.Hp);
        Assert.Empty(next.H(A).EchoQueue);
        var fired = Assert.Single(events.OfType<EchoFired>());
        Assert.Equal(At(4, 5), fired.Origin);
        Assert.Equal(Direction.North, fired.Direction);
    }

    [Theory]
    [InlineData(OrderType.Move)]
    [InlineData(OrderType.Interact)]
    [InlineData(OrderType.Guard)]
    public void Moving_guarding_or_interacting_leave_no_echo(OrderType type)
    {
        var order = type == OrderType.Guard ? Order.DefaultGuard : new Order(type, Direction.North);

        var (next, _) = Resolve(State([Hero(A, HeroClass.Anteayer, 4, 5)]), (A, order));

        Assert.Empty(next.H(A).EchoQueue);
    }

    [Fact]
    public void The_echo_uses_her_current_damage_and_acts_right_after_her()
    {
        var anteayer = Hero(A, HeroClass.Anteayer, 4, 5) with
        {
            EchoQueue = [new PendingEcho(1, At(4, 5), Direction.North)],
            Blessings = [Blessing.StoneFist],
        };
        var totem = Enemy(1, EnemyKind.Totem, 4, 4);

        var (next, _) = Resolve(State([anteayer], [totem]), (A, Attack(Direction.North)));

        // Su ataque hace 2 y el Eco, inmediatamente después, otros 2: lo mata.
        Assert.Null(next.E(1));
    }

    [Fact]
    public void The_echos_friendly_fire_is_hers()
    {
        var anteayer = Hero(A, HeroClass.Anteayer, 8, 8, seat: 0) with { EchoQueue = [new PendingEcho(1, At(4, 5), Direction.North)] };
        var ally = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1) with { Embers = 1 };

        var (next, _) = Resolve(State([anteayer, ally]), (A, Order.DefaultGuard), (B, Interact(Direction.East)));

        Assert.Equal(5, next.H(B).Hp);
        Assert.Equal(0, next.H(B).Embers);
        Assert.Equal(1, next.H(A).Embers);
    }

    [Fact]
    public void The_echo_is_intangible_and_does_not_hurt_or_block_its_owner()
    {
        var anteayer = Hero(A, HeroClass.Anteayer, 4, 4) with { EchoQueue = [new PendingEcho(1, At(4, 5), Direction.North)] };
        var totem = Enemy(1, EnemyKind.Totem, 4, 3);

        var (next, _) = Resolve(State([anteayer], [totem]), (A, Order.DefaultGuard));

        // Su casilla era el alcance del Eco: a ella no le hace nada, y al Tótem de atrás no llega.
        Assert.Equal(4, next.H(A).Hp);
        Assert.Equal(3, next.E(1)!.Hp);
    }

    [Fact]
    public void The_next_echo_is_seen_by_whoever_has_that_tile_in_view()
    {
        var anteayer = Hero(A, HeroClass.Anteayer, 8, 8, seat: 0) with
        {
            EchoQueue = [new PendingEcho(3, At(2, 2), Direction.East), new PendingEcho(4, At(3, 2), Direction.East)],
        };
        var near = Hero(B, HeroClass.Mampostero, 2, 3, seat: 1);
        var far = Hero(C, HeroClass.Mampostero, 7, 7, seat: 2);
        var state = State([anteayer, near, far]);

        var seen = Assert.Single(PlayerViewProjector.For(state, B).Echoes);
        Assert.Equal(new EchoView(A, At(2, 2), Direction.East, 3), seen);
        Assert.Empty(PlayerViewProjector.For(state, C).Echoes);
        Assert.Equal(2, PlayerViewProjector.For(state, A).Echoes.Count);
    }

    [Fact]
    public void Return_jumps_to_where_she_was_two_rounds_ago_and_recovers_the_life_she_had()
    {
        var anteayer = Hero(A, HeroClass.Anteayer, 4, 4) with
        {
            Hp = 1,
            Shield = 1,
            History = [new HeroTrace(1, At(4, 6), 3), new HeroTrace(2, At(4, 5), 2)],
        };
        var state = State([anteayer], round: 3);

        var (next, events) = Resolve(state, (A, Skill(Direction.North)));

        Assert.Equal(At(4, 6), next.H(A).Position);
        Assert.Equal(3, next.H(A).Hp);
        Assert.Equal(HeroCatalog.For(HeroClass.Anteayer).SkillCooldown - 1, next.H(A).SkillCooldown);
        Assert.Contains(events, e => e is HeroMoved { To: { X: 4, Y: 6 } });
        Assert.Equal(1, next.H(A).Shield);
    }

    [Fact]
    public void Return_does_not_lower_her_life()
    {
        var anteayer = Hero(A, HeroClass.Anteayer, 4, 4) with { Hp = 4, History = [new HeroTrace(1, At(4, 6), 1)] };

        var (next, _) = Resolve(State([anteayer], round: 3), (A, Skill(Direction.North)));

        Assert.Equal(At(4, 6), next.H(A).Position);
        Assert.Equal(4, next.H(A).Hp);
    }

    [Fact]
    public void If_the_tile_is_occupied_she_lands_on_the_nearest_free_one()
    {
        var anteayer = Hero(A, HeroClass.Anteayer, 4, 4, seat: 0) with { History = [new HeroTrace(1, At(4, 6), 4)] };
        var blocker = Hero(B, HeroClass.Mampostero, 4, 6, seat: 1);

        var (next, _) = Resolve(State([anteayer, blocker], round: 3), (A, Skill(Direction.North)));

        Assert.Equal(1, next.H(A).Position.DistanceTo(At(4, 6)));
    }

    [Fact]
    public void Without_two_rounds_of_history_in_this_room_return_does_nothing_and_keeps_the_cooldown()
    {
        var anteayer = Hero(A, HeroClass.Anteayer, 4, 4) with { History = [new HeroTrace(2, At(4, 5), 4)] };

        var (next, events) = Resolve(State([anteayer], round: 3), (A, Skill(Direction.North)));

        Assert.Equal(At(4, 4), next.H(A).Position);
        Assert.Equal(0, next.H(A).SkillCooldown);
        Assert.DoesNotContain(events, e => e is SkillUsed);
    }

    [Fact]
    public void Return_happens_in_phase_one_before_moves_and_blows()
    {
        var anteayer = Hero(A, HeroClass.Anteayer, 4, 4) with { History = [new HeroTrace(1, At(0, 8), 4)] };
        var striker = Enemy(1, EnemyKind.Scavenger, 4, 3) with { Telegraph = [At(4, 4)] };

        var (next, _) = Resolve(State([anteayer], [striker], round: 3), (A, Skill(Direction.North)));

        Assert.Equal(4, next.H(A).Hp);
        Assert.Equal(At(0, 8), next.H(A).Position);
    }

    [Fact]
    public void History_is_recorded_while_playing_and_the_view_shows_where_return_would_land()
    {
        var state = State([Hero(A, HeroClass.Anteayer, 4, 6)]);

        state = Resolve(state, (A, Move(Direction.North))).State;
        Assert.Null(PlayerViewProjector.For(state, A).Self.RewindTile);
        state = Resolve(state, (A, Move(Direction.North))).State;

        Assert.Equal(At(4, 6), PlayerViewProjector.For(state, A).Self.RewindTile);
        var (next, _) = Resolve(state, (A, Skill(Direction.North)));
        Assert.Equal(At(4, 6), next.H(A).Position);
    }

    [Fact]
    public void Scheduled_echoes_still_fire_while_she_is_down_and_give_her_the_embers()
    {
        var fallen = Hero(A, HeroClass.Anteayer, 4, 5, seat: 0) with
        {
            Hp = 0,
            Status = HeroStatus.Down,
            EchoQueue = [new PendingEcho(1, At(4, 5), Direction.North)],
        };
        var ally = Hero(B, HeroClass.Mampostero, 0, 8, seat: 1);
        var moth = Enemy(1, EnemyKind.Moth, 4, 4);

        var (next, _) = Resolve(State([fallen, ally], [moth]), (B, Order.DefaultGuard));

        Assert.Null(next.E(1));
        Assert.Equal(1, next.H(A).Embers);
    }

    [Fact]
    public void A_final_blow_from_an_extinct_owner_goes_to_her_saved_embers()
    {
        var extinct = ExtinctHero(A, HeroClass.Anteayer, saved: 3) with { EchoQueue = [new PendingEcho(1, At(4, 5), Direction.North)] };
        var ally = Hero(B, HeroClass.Mampostero, 0, 8, seat: 1);
        var moth = Enemy(1, EnemyKind.Moth, 4, 4);
        var choosing = extinct with { PathOfferPending = false };

        var (next, _) = Resolve(State([choosing, ally], [moth]), (B, Order.DefaultGuard));

        Assert.Null(next.E(1));
        Assert.Equal(4, next.H(A).SavedEmbers);
        Assert.Equal(0, next.H(A).Embers);
    }

    [Fact]
    public void She_can_slay_the_warden_while_down_and_it_counts_for_the_pact()
    {
        var fallen = Hero(A, HeroClass.Anteayer, 4, 5, seat: 0) with
        {
            Hp = 0,
            Status = HeroStatus.Down,
            Pact = PactKind.MostWardenDamage,
            EchoQueue = [new PendingEcho(1, At(4, 5), Direction.North)],
        };
        var ally = Hero(B, HeroClass.Mampostero, 0, 8, seat: 1) with { Pact = PactKind.OpenThreeChests };
        var warden = Model.Enemy.Create(1, EnemyKind.Warden, At(4, 4), maxHp: 24) with { Hp = 1, Phase = 2 };

        var (next, _) = Resolve(State([fallen, ally], [warden]), (B, Order.DefaultGuard));

        Assert.Equal(MatchPhase.Finished, next.Phase);
        Assert.True(next.H(A).Progress.KilledWarden);
        Assert.Equal(7, next.H(A).Embers);
        Assert.True(next.Outcome!.Scores.Single(s => s.Player == A).PactFulfilled);
    }

    [Fact]
    public void Changing_room_erases_history_and_pending_echoes()
    {
        var anteayer = Hero(A, HeroClass.Anteayer, 4, 4) with
        {
            EchoQueue = [new PendingEcho(3, At(4, 4), Direction.West)],
            History = [new HeroTrace(1, At(4, 4), 4)],
        };

        var (next, _) = Resolve(TwoRooms([anteayer]), (A, Move(Direction.North)));

        Assert.Equal(1, next.RoomIndex);
        Assert.Empty(next.H(A).EchoQueue);
        Assert.Empty(next.H(A).History);
    }

    [Fact]
    public void As_an_ember_she_leaves_no_echoes_and_revealing_friendship_erases_the_ones_pending()
    {
        var chooser = ExtinctHero(A, HeroClass.Anteayer, saved: 2) with
        {
            PathChoice = PathKind.Friendship,
            EchoQueue = [new PendingEcho(5, At(4, 5), Direction.North)],
        };
        var ally = Hero(B, HeroClass.Mampostero, 0, 8, seat: 1);

        var (asEmber, _) = Resolve(TwoRooms([chooser, ally]), (B, Order.DefaultGuard));
        Assert.True(asEmber.H(A).IsEmber);
        Assert.Empty(asEmber.H(A).EchoQueue);

        var enemy = Enemy(1, EnemyKind.Scavenger, asEmber.H(A).Position.X, asEmber.H(A).Position.Y - 1);
        var (attacked, _) = Resolve(asEmber with { Enemies = [enemy] }, (A, Attack(Direction.North)), (B, Order.DefaultGuard));
        Assert.Empty(attacked.H(A).EchoQueue);
    }

    [Fact]
    public void As_a_renegade_the_echo_also_deals_at_most_one()
    {
        var renegade = PresentRenegade(A, HeroClass.Anteayer, 8, 8) with
        {
            Blessings = [Blessing.StoneFist],
            WarnedHeroes = [B],
            EchoQueue = [new PendingEcho(1, At(4, 5), Direction.North)],
        };
        var hero = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1);

        var (next, _) = Resolve(State([renegade, hero]), (B, Order.DefaultGuard));

        Assert.Equal(6, next.H(B).Hp);
        var (unguarded, _) = Resolve(State([renegade, hero]), (B, Interact(Direction.East)));
        Assert.Equal(5, unguarded.H(B).Hp);
    }
}
