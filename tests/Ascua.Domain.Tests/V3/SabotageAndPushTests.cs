using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Domain.Tests.Support;
using Ascua.Domain.Views;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests.V3;

/// <summary>06-version-3.md §7 (el sabotaje de §2 se quitó en la versión 4).</summary>
public class SabotageAndPushTests
{
    /// <summary>Quien no da orden defiende: la otra parte está haciendo otra cosa.</summary>
    private static readonly (PlayerId, Order) BBusy = (B, Interact(Direction.South));

    [Fact]
    public void Sabotage_no_longer_exists_and_is_always_rejected()
    {
        var a = Hero(A, HeroClass.Mampostero, 4, 5);
        var b = Hero(B, HeroClass.Errata, 4, 4, seat: 1);

        Assert.Equal(ErrorCode.InvalidOrder, OrderValidator.Validate(State([a, b]), A, 1, Sabotage(Direction.North))!.Kind);
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
