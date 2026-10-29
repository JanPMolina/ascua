using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Domain.Tests.Support;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests;

public sealed class HeroTests
{
    [Fact]
    public void Anvil_blocks_frontal_hits_when_the_roll_succeeds()
    {
        var anvil = Hero(A, HeroClass.Anvil, 4, 4, facing: Direction.North);
        var scavenger = Enemy(1, EnemyKind.Scavenger, 4, 3) with { Telegraph = [new Position(4, 4)] };
        var state = State([anvil], [scavenger]);

        var (blocked, events) = Resolve(state, FixedRandom.Always, (A, Interact(Direction.North)));
        var (hit, _) = Resolve(state, FixedRandom.Never, (A, Interact(Direction.North)));

        Assert.Equal(6, blocked.H(A).Hp);
        Assert.Contains(events, e => e is HitPrevented { Reason: PreventionReason.Block });
        Assert.Equal(5, hit.H(A).Hp);
    }

    [Fact]
    public void Anvil_cannot_block_hits_from_behind()
    {
        var anvil = Hero(A, HeroClass.Anvil, 4, 4, facing: Direction.South);
        var scavenger = Enemy(1, EnemyKind.Scavenger, 4, 3) with { Telegraph = [new Position(4, 4)] };

        var (next, _) = Resolve(State([anvil], [scavenger]), FixedRandom.Always, (A, Interact(Direction.South)));

        Assert.Equal(5, next.H(A).Hp);
    }

    [Fact]
    public void Rampart_protects_allies_in_line_behind_the_anvil()
    {
        var anvil = Hero(A, HeroClass.Anvil, 4, 4, seat: 0);
        var spark = Hero(B, HeroClass.Spark, 4, 6, seat: 1);
        var scavenger = Enemy(1, EnemyKind.Scavenger, 5, 6) with { Telegraph = [new Position(4, 6)] };
        var state = State([anvil, spark], [scavenger]);

        var (walled, events) = Resolve(state, (A, Skill(Direction.North)), (B, Interact(Direction.West)));
        var (open, _) = Resolve(state, (A, Order.DefaultGuard), (B, Interact(Direction.West)));

        Assert.Equal(4, walled.H(B).Hp);
        Assert.Contains(events, e => e is HitPrevented { Reason: PreventionReason.Rampart });
        Assert.Equal(HeroCatalog.For(HeroClass.Anvil).SkillCooldown - 1, walled.H(A).SkillCooldown);
        Assert.Equal(3, open.H(B).Hp);
    }

    [Fact]
    public void Spark_hits_the_first_target_up_to_three_tiles_away()
    {
        var spark = Hero(A, HeroClass.Spark, 4, 7);
        var near = Enemy(1, EnemyKind.Totem, 4, 4);
        var far = Enemy(2, EnemyKind.Totem, 4, 2);

        var (next, _) = Resolve(State([spark], [near, far]), (A, Attack(Direction.North)));

        Assert.Equal(2, next.E(1)!.Hp);
        Assert.Equal(3, next.E(2)!.Hp);
    }

    [Fact]
    public void Spark_gains_one_charge_per_round_without_attacking_up_to_three()
    {
        var state = State([Hero(A, HeroClass.Spark, 4, 4)]);
        for (var round = 0; round < 5; round++)
        {
            state = Resolve(state, FixedRandom.Never).State;
        }

        Assert.Equal(HeroCatalog.MaxCharges, state.H(A).Charges);

        state = Resolve(state, (A, Attack(Direction.North))).State;
        Assert.Equal(HeroCatalog.MaxCharges, state.H(A).Charges);
    }

    [Fact]
    public void Discharge_spends_all_charges_and_hits_in_a_cross()
    {
        var spark = Hero(A, HeroClass.Spark, 4, 4) with { Charges = 2 };
        var north = Enemy(1, EnemyKind.Totem, 4, 2);
        var east = Enemy(2, EnemyKind.Totem, 7, 4);
        var diagonal = Enemy(3, EnemyKind.Totem, 5, 5);

        var (next, events) = Resolve(State([spark], [north, east, diagonal]), (A, Skill(Direction.North)));

        Assert.Null(next.E(1));
        Assert.Null(next.E(2));
        Assert.Equal(3, next.E(3)!.Hp);
        Assert.Equal(0, next.H(A).Charges);
        Assert.Equal(2, events.OfType<EnemyKilled>().Count());
    }

    [Theory]
    [InlineData(Direction.North, 1)]
    [InlineData(Direction.South, 2)]
    public void Raven_deals_double_damage_from_behind(Direction totemFacing, int expectedHp)
    {
        var raven = Hero(A, HeroClass.Raven, 4, 5);
        var totem = Enemy(1, EnemyKind.Totem, 4, 4, totemFacing);

        var (next, _) = Resolve(State([raven], [totem]), (A, Attack(Direction.North)));

        Assert.Equal(expectedHp, next.E(1)!.Hp);
    }

    [Fact]
    public void Shadow_step_moves_two_tiles_and_hides_the_raven_from_enemies()
    {
        var raven = Hero(A, HeroClass.Raven, 4, 6);
        var scavenger = Enemy(1, EnemyKind.Scavenger, 4, 1);

        var (next, events) = Resolve(State([raven], [scavenger]), (A, Skill(Direction.North)));

        Assert.Equal(new Position(4, 4), next.H(A).Position);
        Assert.True(next.H(A).IsStealthed);
        Assert.Equal(new Position(4, 1), next.E(1)!.Position);
        Assert.Empty(next.E(1)!.Telegraph);
        var moved = Assert.Single(events.OfType<HeroMoved>());
        Assert.Equal(AudienceKind.Player, moved.Audience.Kind);
    }

    [Fact]
    public void Stealth_lasts_two_rounds()
    {
        var state = State([Hero(A, HeroClass.Raven, 4, 6)]);

        state = Resolve(state, (A, Skill(Direction.North))).State;
        Assert.Equal(2, state.H(A).StealthRounds);
        state = Resolve(state, FixedRandom.Never).State;
        Assert.Equal(1, state.H(A).StealthRounds);
        state = Resolve(state, FixedRandom.Never).State;
        Assert.False(state.H(A).IsStealthed);
    }

    [Fact]
    public void Sprout_heals_an_ally_within_two_tiles_in_that_direction()
    {
        var root = Hero(A, HeroClass.Root, 4, 4, seat: 0);
        var anvil = Hero(B, HeroClass.Anvil, 6, 4, seat: 1) with { Hp = 3 };

        var (next, _) = Resolve(State([root, anvil]), (A, Skill(Direction.East)));

        Assert.Equal(5, next.H(B).Hp);
    }

    [Fact]
    public void Sprout_heals_the_root_itself_when_no_ally_is_there()
    {
        var root = Hero(A, HeroClass.Root, 4, 4) with { Hp = 2 };

        var (next, _) = Resolve(State([root]), (A, Skill(Direction.West)));

        Assert.Equal(4, next.H(A).Hp);
    }

    [Fact]
    public void Skills_respect_their_cooldown()
    {
        var state = State([Hero(A, HeroClass.Root, 4, 4) with { Hp = 2 }]);
        var cooldown = HeroCatalog.For(HeroClass.Root).SkillCooldown;

        state = Resolve(state, (A, Skill(Direction.West))).State;
        Assert.Equal(ErrorCode.SkillOnCooldown, OrderValidator.Validate(state, A, state.Round, Skill(Direction.West))?.Kind);

        for (var i = 1; i < cooldown; i++)
        {
            state = Resolve(state, FixedRandom.Never).State;
        }

        Assert.Null(OrderValidator.Validate(state, A, state.Round, Skill(Direction.West)));
    }

    [Fact]
    public void Healing_potions_heal_one_more_for_the_root()
    {
        var root = Hero(A, HeroClass.Root, 4, 4) with { Hp = 1 };
        var room = EmptyRoom() with { Chests = [new Chest(1, new Position(4, 3), ChestLoot.HealingPotion, HasSilverKey: false)] };

        var (next, _) = Resolve(State([root], room: room), (A, Interact(Direction.North)));

        Assert.Equal(4, next.H(A).Hp);
        Assert.Equal(1, next.H(A).Embers);
        Assert.True(next.CurrentRoom.Chests[0].Opened);
    }
}
