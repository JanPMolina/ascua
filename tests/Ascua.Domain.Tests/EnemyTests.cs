using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Tests.Support;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests;

public sealed class EnemyTests
{
    [Fact]
    public void Scavenger_approaches_and_telegraphs_the_hero_tile()
    {
        var state = State([Hero(A, HeroClass.Mampostero, 4, 5)], [Enemy(1, EnemyKind.Scavenger, 4, 3)]);

        var (next, events) = Resolve(state, FixedRandom.Never);

        Assert.Equal(new Position(4, 4), next.E(1)!.Position);
        Assert.Equal(new Position(4, 5), Assert.Single(next.E(1)!.Telegraph));
        Assert.Contains(events, e => e is AttackTelegraphed);
        Assert.Equal(6, next.H(A).Hp);
    }

    [Fact]
    public void Telegraphed_attacks_land_next_round_unless_the_hero_steps_away()
    {
        var hero = Hero(A, HeroClass.Errata, 4, 5);
        var scavenger = Enemy(1, EnemyKind.Scavenger, 4, 4) with { Telegraph = [new Position(4, 5)] };
        var state = State([hero], [scavenger]);

        var (stayed, _) = Resolve(state, (A, Interact(Direction.North)));
        var (dodged, _) = Resolve(state, (A, Move(Direction.West)));

        Assert.Equal(3, stayed.H(A).Hp);
        Assert.Equal(4, dodged.H(A).Hp);
    }

    [Fact]
    public void Guard_halves_incoming_damage()
    {
        var hero = Hero(A, HeroClass.Errata, 4, 5);
        var crossbow = Enemy(1, EnemyKind.Crossbowman, 4, 1) with { Telegraph = [new Position(4, 5)] };

        var (next, _) = Resolve(State([hero], [crossbow]), (A, Order.DefaultGuard));

        Assert.Equal(3, next.H(A).Hp);
    }

    [Fact]
    public void Missing_orders_default_to_guard()
    {
        var (_, events) = Resolve(State([Hero(A, HeroClass.Errata, 4, 5)]), FixedRandom.Never);

        Assert.Contains(events, e => e is HeroGuarded { Player.Value: "alice" });
    }

    [Fact]
    public void Moths_fly_over_cracks_but_walkers_go_around()
    {
        var room = EmptyRoom().WithTerrain(new Position(4, 3), Terrain.Crack);
        var hero = Hero(A, HeroClass.Mampostero, 4, 5);

        var (withMoth, _) = Resolve(State([hero], [Enemy(1, EnemyKind.Moth, 4, 2)], room), FixedRandom.Never);
        var (withScavenger, _) = Resolve(State([hero], [Enemy(1, EnemyKind.Scavenger, 4, 2)], room), FixedRandom.Never);

        Assert.Equal(new Position(4, 3), withMoth.E(1)!.Position);
        Assert.Equal(new Position(5, 2), withScavenger.E(1)!.Position);
    }

    [Fact]
    public void Slime_splits_into_two_small_slimes_that_do_not_split_again()
    {
        var hero = Hero(A, HeroClass.Mampostero, 4, 4);
        var slime = Enemy(1, EnemyKind.Slime, 4, 3) with { Hp = 1 };

        var (next, _) = Resolve(State([hero], [slime]), (A, Attack(Direction.North)));

        Assert.Null(next.E(1));
        Assert.Equal(2, next.Enemies.Count);
        Assert.All(next.Enemies, e => Assert.True(e is { Kind: EnemyKind.Slime, Hp: 1, IsSplit: true }));
        Assert.Equal(1, next.H(A).Embers);

        var small = next.Enemies[0];
        var finisher = next with { Enemies = [small with { Position = new Position(4, 3) }], Heroes = [next.H(A) with { Facing = Direction.North }] };
        var (after, _) = Resolve(finisher, (A, Attack(Direction.North)));
        Assert.Empty(after.Enemies);
    }

    [Fact]
    public void Hopper_moves_two_tiles_per_round()
    {
        var state = State([Hero(A, HeroClass.Mampostero, 4, 6)], [Enemy(1, EnemyKind.Hopper, 4, 0)]);

        var (next, _) = Resolve(state, FixedRandom.Never);

        Assert.Equal(new Position(4, 2), next.E(1)!.Position);
    }

    [Fact]
    public void Crossbowman_announces_a_full_line_when_aligned()
    {
        var state = State([Hero(A, HeroClass.Mampostero, 4, 6)], [Enemy(1, EnemyKind.Crossbowman, 4, 1)]);

        var (next, _) = Resolve(state, FixedRandom.Never);

        Assert.Equal(new Position(4, 1), next.E(1)!.Position);
        Assert.Equal(Enumerable.Range(2, 7).Select(y => new Position(4, y)), next.E(1)!.Telegraph);
    }

    [Fact]
    public void Crossbowman_moves_to_align_and_then_announces()
    {
        var state = State([Hero(A, HeroClass.Mampostero, 4, 2)], [Enemy(1, EnemyKind.Crossbowman, 1, 1)]);

        var (next, _) = Resolve(state, FixedRandom.Never);

        Assert.Equal(new Position(1, 2), next.E(1)!.Position);
        Assert.Equal(Enumerable.Range(2, 7).Select(x => new Position(x, 2)), next.E(1)!.Telegraph);
    }

    [Fact]
    public void Totem_summons_a_scavenger_every_few_rounds_and_goes_out_after_three()
    {
        var state = State([Hero(A, HeroClass.Mampostero, 0, 8)], [Enemy(1, EnemyKind.Totem, 8, 0)]);
        var events = new List<DomainEvent>();
        for (var round = 1; round <= 3 * EnemyCatalog.TotemSummonEvery; round++)
        {
            var (next, roundEvents) = Resolve(state, FixedRandom.Never);
            state = next;
            events.AddRange(roundEvents);
            if (round == EnemyCatalog.TotemSummonEvery - 1)
            {
                Assert.DoesNotContain(events, e => e is EnemySpawned);
            }
        }

        Assert.Equal(3, events.OfType<EnemySpawned>().Count());
        Assert.Contains(events, e => e is TotemExtinguished { EnemyId: 1 });
        Assert.Null(state.E(1));
    }

    [Fact]
    public void Warden_in_phase_one_is_immune_from_the_front_but_not_from_the_flank()
    {
        var warden = Model.Enemy.Create(1, EnemyKind.Warden, new Position(4, 2), maxHp: 24) with { Facing = Direction.South };
        var mampostero = Hero(A, HeroClass.Mampostero, 4, 3, seat: 0);
        var errata = Hero(B, HeroClass.Errata, 3, 2, seat: 1);
        var state = State([mampostero, errata], [warden]);

        var (front, frontEvents) = Resolve(state, (A, Attack(Direction.North)));
        var (flank, _) = Resolve(state, (B, Attack(Direction.East)));

        Assert.Equal(24, front.E(1)!.Hp);
        Assert.Contains(frontEvents, e => e is HitPrevented { Reason: PreventionReason.FrontImmunity });
        Assert.Equal(23, flank.E(1)!.Hp);
    }

    [Fact]
    public void Warden_rests_every_other_round_without_moving_or_turning()
    {
        var warden = Model.Enemy.Create(1, EnemyKind.Warden, new Position(4, 2), maxHp: 24) with { Facing = Direction.South };
        var state = State([Hero(A, HeroClass.Mampostero, 7, 2)], [warden]);

        var (struck, _) = Resolve(state, (A, Order.DefaultGuard));
        Assert.NotEmpty(struck.E(1)!.Telegraph);

        var (resting, _) = Resolve(struck, (A, Move(Direction.South)));
        Assert.Empty(resting.E(1)!.Telegraph);
        Assert.Equal(struck.E(1)!.Position, resting.E(1)!.Position);
        Assert.Equal(struck.E(1)!.Facing, resting.E(1)!.Facing);

        var (again, _) = Resolve(resting, (A, Order.DefaultGuard));
        Assert.NotEmpty(again.E(1)!.Telegraph);
    }

    [Fact]
    public void Warden_telegraphs_a_three_by_three_in_front_of_him()
    {
        var warden = Model.Enemy.Create(1, EnemyKind.Warden, new Position(4, 2), maxHp: 24);
        var state = State([Hero(A, HeroClass.Mampostero, 4, 4)], [warden]);

        var (next, _) = Resolve(state, FixedRandom.Never);

        var expected = from y in Enumerable.Range(3, 3) from x in Enumerable.Range(3, 3) select new Position(x, y);
        Assert.Equal(expected.OrderBy(p => p.Y).ThenBy(p => p.X), next.E(1)!.Telegraph.OrderBy(p => p.Y).ThenBy(p => p.X));
    }

    [Fact]
    public void Warden_enters_phase_two_at_half_life_summons_totems_and_alternates_with_a_cross()
    {
        var warden = Model.Enemy.Create(1, EnemyKind.Warden, new Position(4, 2), maxHp: 24) with { Hp = 13, Facing = Direction.South };
        var errata = Hero(A, HeroClass.Errata, 3, 2);
        var state = State([errata], [warden]);

        var (next, events) = Resolve(state, (A, Attack(Direction.East)));

        var boss = next.E(1)!;
        Assert.Equal(2, boss.Phase);
        Assert.Contains(events, e => e is WardenEnraged);
        Assert.Equal(2, next.Enemies.Count(e => e.Kind == EnemyKind.Totem));
        Assert.Contains(new Position(0, 2), boss.Telegraph);
        Assert.Contains(new Position(4, 8), boss.Telegraph);

        // Entre golpe y golpe toma aire: el siguiente anuncio llega una ronda después.
        var (resting, _) = Resolve(next, (A, Order.DefaultGuard));
        Assert.Empty(resting.E(1)!.Telegraph);

        var (after, _) = Resolve(resting, (A, Move(Direction.West)));
        Assert.DoesNotContain(new Position(4, 8), after.E(1)!.Telegraph);
        Assert.Equal(9, after.E(1)!.Telegraph.Count);
    }

    [Fact]
    public void Frozen_enemies_lose_their_next_action()
    {
        var hero = Hero(A, HeroClass.Mampostero, 4, 4) with { Blessings = [Blessing.Frost] };
        var scavenger = Enemy(1, EnemyKind.Scavenger, 4, 3) with { Telegraph = [new Position(4, 4)] };

        var (next, events) = Resolve(State([hero], [scavenger]), FixedRandom.Always, (A, Attack(Direction.North)));

        Assert.Equal(6, next.H(A).Hp);
        Assert.Contains(events, e => e is ActionLost);
        Assert.Empty(next.E(1)!.Telegraph);
        Assert.False(next.E(1)!.Frozen);
    }
}
