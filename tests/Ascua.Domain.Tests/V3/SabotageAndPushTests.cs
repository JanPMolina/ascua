using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Domain.Tests.Support;
using Ascua.Domain.Views;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests.V3;

/// <summary>06-version-3.md §2 y §7.</summary>
public class SabotageAndPushTests
{
    /// <summary>Quien no da orden defiende y anularía el golpe: la víctima está haciendo otra cosa.</summary>
    private static readonly (PlayerId, Order) BBusy = (B, Interact(Direction.South));

    [Fact]
    public void Sabotage_hits_the_first_ally_in_line_for_one_with_no_author_and_is_recorded()
    {
        var a = Hero(A, HeroClass.Errata, 4, 4, seat: 0, facing: Direction.West);
        var b = Hero(B, HeroClass.Mampostero, 4, 2, seat: 1) with { Embers = 2 };

        var (next, events) = Resolve(State([a, b]), (A, Sabotage(Direction.North)), BBusy);

        Assert.Equal(5, next.H(B).Hp);
        Assert.Equal(2, next.H(B).Embers);
        var betrayal = Assert.Single(next.Betrayals);
        Assert.Equal(BetrayalKind.Sabotage, betrayal.Kind);
        Assert.Equal(0, betrayal.Room);
        Assert.Equal(Direction.West, next.H(A).Facing);
        Assert.Null(events.OfType<EntityDamaged>().Single().Source);
    }

    [Fact]
    public void Nothing_in_what_others_receive_reveals_the_author()
    {
        var a = Hero(A, HeroClass.Errata, 4, 4, seat: 0);
        var b = Hero(B, HeroClass.Mampostero, 4, 2, seat: 1);
        var escucha = Hero(C, HeroClass.Escucha, 4, 1, seat: 2);

        var (next, events) = Resolve(State([a, b, escucha]), (A, Sabotage(Direction.North)), BBusy);

        foreach (var viewer in new[] { B, C })
        {
            var seen = events.SeenBy(next, viewer);
            Assert.DoesNotContain(seen, e => e is BetrayalRecorded);
            Assert.DoesNotContain(seen, e => e is EntityDamaged { Source: not null });
            Assert.DoesNotContain(A.Value, Json.Of(seen));
        }

        Assert.Contains(events.SeenBy(next, A), e => e is BetrayalRecorded { Kind: BetrayalKind.Sabotage });
        Assert.DoesNotContain(A, next.NoisyHeroes);
        Assert.Empty(PlayerViewProjector.For(next, B).Outcome?.Betrayals ?? []);
    }

    [Fact]
    public void Sabotage_is_once_per_room_and_is_not_spent_without_a_target()
    {
        var a = Hero(A, HeroClass.Errata, 4, 4, seat: 0);
        var b = Hero(B, HeroClass.Mampostero, 0, 8, seat: 1);

        var (unspent, _) = Resolve(State([a, b]), (A, Sabotage(Direction.North)));
        Assert.Null(OrderValidator.Validate(unspent, A, unspent.Round, Sabotage(Direction.North)));

        var near = b with { Position = At(4, 3) };
        var (spent, _) = Resolve(State([a, near]), (A, Sabotage(Direction.North)), BBusy);
        Assert.Equal(ErrorCode.InvalidOrder, OrderValidator.Validate(spent, A, spent.Round, Sabotage(Direction.North))!.Kind);
    }

    [Fact]
    public void A_wall_cuts_the_line_and_an_enemy_does_not()
    {
        var a = Hero(A, HeroClass.Errata, 4, 4, seat: 0);
        var b = Hero(B, HeroClass.Mampostero, 4, 2, seat: 1);

        var blocked = EmptyRoom().WithTerrain(At(4, 3), Terrain.Wall);
        var (afterWall, _) = Resolve(State([a, b], room: blocked), (A, Sabotage(Direction.North)), BBusy);
        Assert.Empty(afterWall.Betrayals);

        var (afterEnemy, _) = Resolve(State([a, b], [Enemy(1, EnemyKind.Totem, 4, 3)]), (A, Sabotage(Direction.North)), BBusy);
        Assert.Single(afterEnemy.Betrayals);
    }

    [Fact]
    public void A_guard_cancels_it_spends_it_and_it_is_not_a_betrayal()
    {
        var a = Hero(A, HeroClass.Errata, 4, 4, seat: 0);
        var b = Hero(B, HeroClass.Mampostero, 4, 3, seat: 1);

        var (next, _) = Resolve(State([a, b]), (A, Sabotage(Direction.North)), (B, Order.DefaultGuard));

        Assert.Equal(6, next.H(B).Hp);
        Assert.Empty(next.Betrayals);
        Assert.False(next.H(A).CanSabotage(next.RoomIndex));
    }

    [Fact]
    public void A_shield_absorbing_it_is_still_a_betrayal()
    {
        var a = Hero(A, HeroClass.Errata, 4, 4, seat: 0);
        var b = Hero(B, HeroClass.Mampostero, 4, 3, seat: 1) with { Shield = 1 };

        var (next, _) = Resolve(State([a, b]), (A, Sabotage(Direction.North)), BBusy);

        Assert.Equal(6, next.H(B).Hp);
        Assert.Single(next.Betrayals);
    }

    [Fact]
    public void It_drops_a_coin_from_the_miser_without_stealing()
    {
        var a = Hero(A, HeroClass.Errata, 4, 4, seat: 0);
        var b = Hero(B, HeroClass.Avaro, 4, 3, seat: 1);

        var (next, _) = Resolve(State([a, b]), (A, Sabotage(Direction.North)), BBusy);

        Assert.Equal(2, next.H(B).Embers);
        Assert.Equal(0, next.H(A).Embers);
        Assert.Equal(1, Assert.Single(next.CurrentRoom.Ground).Amount);
    }

    [Fact]
    public void Polizon_inside_a_host_cannot_sabotage_or_push()
    {
        var host = Enemy(1, EnemyKind.Scavenger, 4, 4);
        var polizon = Hero(A, HeroClass.Polizon, 4, 4) with { HostEnemyId = 1, BoardedRound = 1 };
        var state = State([polizon], [host]);

        Assert.Equal(ErrorCode.InvalidOrder, OrderValidator.Validate(state, A, 1, Sabotage(Direction.North))!.Kind);
        Assert.Equal(ErrorCode.InvalidOrder, OrderValidator.Validate(state, A, 1, Push(Direction.North))!.Kind);
    }

    [Fact]
    public void Hilvan_can_sabotage_from_the_second_body_in_mirror()
    {
        var hilvan = Hero(A, HeroClass.Hilvan, 2, 4, seat: 0) with { SecondBody = At(6, 4) };
        var b = Hero(B, HeroClass.Mampostero, 8, 4, seat: 1);

        // Al oeste desde el principal no hay nadie; en espejo, al este desde el segundo, está B.
        var (next, _) = Resolve(State([hilvan, b]), (A, Sabotage(Direction.West)), BBusy);

        Assert.Single(next.Betrayals);
        Assert.Equal(5, next.H(B).Hp);
    }

    // ───── Empujón

    [Fact]
    public void Push_moves_an_adjacent_enemy_with_a_visible_author_and_a_cooldown()
    {
        var hero = Hero(A, HeroClass.Mampostero, 4, 4);
        var enemy = Enemy(1, EnemyKind.Scavenger, 4, 3);

        var (next, events) = Resolve(State([hero], [enemy]), (A, Push(Direction.North)));

        var pushed = events.OfType<Pushed>().Single();
        Assert.Equal(A, pushed.Pusher);
        Assert.Equal(At(4, 2), pushed.To);
        Assert.Contains(events.SeenBy(next, A), e => e is Pushed);
        Assert.Equal(2, next.H(A).PushCooldown);
        Assert.Equal(ErrorCode.SkillOnCooldown, OrderValidator.Validate(next, A, next.Round, Push(Direction.North))!.Kind);
    }

    [Theory]
    [InlineData(EnemyKind.Warden)]
    [InlineData(EnemyKind.Totem)]
    public void The_warden_and_the_totem_cannot_be_pushed(EnemyKind kind)
    {
        var hero = Hero(A, HeroClass.Mampostero, 4, 4);
        var enemy = Model.Enemy.Create(1, kind, At(4, 3), 5);

        var (next, _) = Resolve(State([hero], [enemy]), (A, Push(Direction.North)));

        Assert.Equal(At(4, 3), next.E(1)!.Position);
        Assert.Equal(0, next.H(A).PushCooldown);
    }

    [Fact]
    public void Without_an_effect_it_is_not_spent()
    {
        var hero = Hero(A, HeroClass.Mampostero, 4, 4);
        var enemy = Enemy(1, EnemyKind.Scavenger, 4, 3);
        var room = EmptyRoom().WithTerrain(At(4, 2), Terrain.Wall);

        var (next, events) = Resolve(State([hero], [enemy], room), (A, Push(Direction.North)));

        Assert.DoesNotContain(events, e => e is Pushed);
        Assert.Equal(0, next.H(A).PushCooldown);
    }

    [Fact]
    public void A_crack_kills_a_walking_enemy_and_it_is_the_final_blow_of_the_pusher()
    {
        var hero = Hero(A, HeroClass.Mampostero, 4, 4);
        var enemy = Enemy(1, EnemyKind.Scavenger, 4, 3);
        var room = EmptyRoom().WithTerrain(At(4, 2), Terrain.Crack);

        var (next, events) = Resolve(State([hero], [enemy], room), (A, Push(Direction.North)));

        Assert.Null(next.E(1));
        Assert.Equal(A, events.OfType<EnemyKilled>().Single().Killer);
        Assert.Equal(1, next.H(A).Embers);
        Assert.Equal(1, next.H(A).Progress.HazardPushes);
    }

    [Fact]
    public void A_flying_enemy_survives_the_crack_and_does_not_count_for_the_mission()
    {
        var hero = Hero(A, HeroClass.Mampostero, 4, 4);
        var moth = Enemy(1, EnemyKind.Moth, 4, 3);
        var room = EmptyRoom().WithTerrain(At(4, 2), Terrain.Crack);

        var (next, _) = Resolve(State([hero], [moth], room), (A, Push(Direction.North)));

        Assert.NotNull(next.E(1));
        Assert.Equal(0, next.H(A).Progress.HazardPushes);
    }

    [Fact]
    public void A_hero_pushed_into_a_crack_is_downed_not_dead()
    {
        var a = Hero(A, HeroClass.Mampostero, 4, 4, seat: 0);
        var b = Hero(B, HeroClass.Errata, 4, 3, seat: 1) with { Embers = 3 };
        var room = EmptyRoom().WithTerrain(At(4, 2), Terrain.Crack);

        var (next, events) = Resolve(State([a, b], room: room), (A, Push(Direction.North)));

        Assert.Equal(HeroStatus.Down, next.H(B).Status);
        Assert.Equal(0, next.H(B).Hp);
        Assert.Equal(3, next.H(B).Embers);
        Assert.Equal(0, next.H(A).Embers);
        Assert.DoesNotContain(events, e => e is HeroExtinguished);
        Assert.Empty(next.Betrayals);
    }

    [Fact]
    public void Last_breath_saves_him_from_the_crack_and_the_mason_does_not_fall()
    {
        var a = Hero(A, HeroClass.Errata, 4, 4, seat: 0);
        var room = EmptyRoom().WithTerrain(At(4, 2), Terrain.Crack);

        var breath = Hero(B, HeroClass.Errata, 4, 3, seat: 1) with { Blessings = [Blessing.LastBreath] };
        var (afterBreath, _) = Resolve(State([a, breath], room: room), (A, Push(Direction.North)));
        Assert.Equal(HeroStatus.Active, afterBreath.H(B).Status);
        Assert.Equal(1, afterBreath.H(B).Hp);

        var mason = Hero(B, HeroClass.Mampostero, 4, 3, seat: 1);
        var (afterMason, _) = Resolve(State([a, mason], room: room), (A, Push(Direction.North)));
        Assert.Equal(HeroStatus.Active, afterMason.H(B).Status);
        Assert.Equal(6, afterMason.H(B).Hp);
    }

    [Fact]
    public void A_renegade_pushed_into_a_crack_is_defeated_and_a_renegade_only_pushes_heroes()
    {
        var a = Hero(A, HeroClass.Errata, 4, 4, seat: 0);
        var renegade = PresentRenegade(B, HeroClass.Mampostero, 4, 3, seat: 1);
        var room = EmptyRoom().WithTerrain(At(4, 2), Terrain.Crack);

        var (next, _) = Resolve(State([a, renegade with { Class = HeroClass.Errata }], room: room), (A, Push(Direction.North)));
        Assert.Equal(HeroStatus.Extinct, next.H(B).Status);
        Assert.Equal(PathRules.RenegadeDefeatReward, next.H(A).Embers);

        var traitor = PresentRenegade(A, HeroClass.Errata, 4, 4);
        var (_, events) = Resolve(State([traitor, Hero(B, HeroClass.Errata, 0, 8, seat: 1)], [Enemy(1, EnemyKind.Scavenger, 4, 3)]), (A, Push(Direction.North)));
        Assert.DoesNotContain(events, e => e is Pushed);
    }

    [Fact]
    public void Spikes_and_fire_apply_their_normal_damage_at_the_end_of_the_round()
    {
        var a = Hero(A, HeroClass.Mampostero, 4, 4, seat: 0);
        var b = Hero(B, HeroClass.Errata, 4, 3, seat: 1);
        var spikes = EmptyRoom().WithTerrain(At(4, 2), Terrain.Spikes);

        var (next, _) = Resolve(State([a, b], room: spikes, round: 2), (A, Push(Direction.North)), BBusy);

        Assert.Equal(At(4, 2), next.H(B).Position);
        Assert.Equal(3, next.H(B).Hp);
    }

    [Fact]
    public void Pushing_one_body_of_hilvan_moves_only_that_body()
    {
        var a = Hero(A, HeroClass.Mampostero, 6, 5, seat: 0);
        var hilvan = Hero(B, HeroClass.Hilvan, 2, 4, seat: 1) with { SecondBody = At(6, 4) };

        var (next, _) = Resolve(State([a, hilvan]), (A, Push(Direction.North)));

        Assert.Equal(At(2, 4), next.H(B).Position);
        Assert.Equal(At(6, 3), next.H(B).SecondBody);
    }

    [Fact]
    public void A_pushed_host_takes_polizon_with_it()
    {
        var a = Hero(A, HeroClass.Mampostero, 4, 4, seat: 0);
        var host = Enemy(1, EnemyKind.Scavenger, 4, 3);
        var polizon = Hero(B, HeroClass.Polizon, 4, 3, seat: 1) with { HostEnemyId = 1, BoardedRound = 1 };

        var (next, _) = Resolve(State([a, polizon], [host]), (A, Push(Direction.North)));

        Assert.Equal(At(4, 2), next.E(1)!.Position);
        Assert.Equal(At(4, 2), next.H(B).Position);
    }

    [Fact]
    public void A_pushed_hero_picks_up_coins_on_the_floor()
    {
        var a = Hero(A, HeroClass.Mampostero, 4, 4, seat: 0);
        var b = Hero(B, HeroClass.Errata, 4, 3, seat: 1);
        var room = EmptyRoom() with { Ground = [new GroundEmbers(At(4, 2), 2)] };

        var (next, _) = Resolve(State([a, b], room: room), (A, Push(Direction.North)), BBusy);

        Assert.Equal(2, next.H(B).Embers);
        Assert.Empty(next.CurrentRoom.Ground);
    }
}
