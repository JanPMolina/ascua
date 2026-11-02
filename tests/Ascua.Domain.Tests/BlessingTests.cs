using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Tests.Support;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests;

public sealed class BlessingTests
{
    [Fact]
    public void Synergies_are_recipes_of_two_blessings()
    {
        Assert.Equal(Synergy.Inferno, Assert.Single(BlessingRulesFor(Blessing.LivingEmber, Blessing.FireStep)));
        Assert.Equal(Synergy.LivingArmor, Assert.Single(BlessingRulesFor(Blessing.BrokenMirror, Blessing.DoubleHeart)));
        Assert.Equal(Synergy.Plunderer, Assert.Single(BlessingRulesFor(Blessing.Greed, Blessing.Lockpick)));
        Assert.Empty(BlessingRulesFor(Blessing.Greed, Blessing.FireStep));
    }

    [Fact]
    public void Stone_fist_adds_damage_and_costs_max_life()
    {
        var hero = Hero(A, HeroClass.Mampostero, 4, 4) with { Blessings = [Blessing.StoneFist] };

        var (next, _) = Resolve(State([hero], [Enemy(1, EnemyKind.Scavenger, 4, 3)]), (A, Attack(Direction.North)));

        Assert.Null(next.E(1));
        Assert.Equal(5, hero.MaxHp);
    }

    [Fact]
    public void Greed_pays_an_extra_ember_per_kill_but_takes_extra_damage()
    {
        var hero = Hero(A, HeroClass.Errata, 4, 4) with { Blessings = [Blessing.Greed] };
        var moth = Enemy(1, EnemyKind.Moth, 4, 3);
        var scavenger = Enemy(2, EnemyKind.Scavenger, 5, 4, Direction.West) with { Telegraph = [new Position(4, 4)] };

        var (next, _) = Resolve(State([hero], [moth, scavenger]), (A, Attack(Direction.North)));

        Assert.Equal(2, next.H(A).Embers);
        Assert.Equal(2, next.H(A).Hp);
    }

    [Fact]
    public void Hungry_blade_heals_on_kill()
    {
        var hero = Hero(A, HeroClass.Errata, 4, 4) with { Hp = 2, Blessings = [Blessing.HungryBlade] };

        var (next, _) = Resolve(State([hero], [Enemy(1, EnemyKind.Moth, 4, 3)]), (A, Attack(Direction.North)));

        Assert.Equal(3, next.H(A).Hp);
    }

    [Fact]
    public void Lockpick_makes_chests_drop_an_extra_ember()
    {
        var hero = Hero(A, HeroClass.Errata, 4, 4) with { Blessings = [Blessing.Lockpick] };
        var room = EmptyRoom() with { Chests = [new Chest(1, new Position(4, 3), ChestLoot.Shield, HasSilverKey: true)] };

        var (next, _) = Resolve(State([hero], room: room), (A, Interact(Direction.North)));

        Assert.Equal(2, next.H(A).Embers);
        Assert.Equal(1, next.H(A).Shield);
        Assert.Equal(1, next.H(A).SilverKeys);
    }

    [Fact]
    public void Living_ember_burns_the_target()
    {
        var hero = Hero(A, HeroClass.Errata, 4, 4) with { Blessings = [Blessing.LivingEmber] };

        var (next, events) = Resolve(State([hero], [Enemy(1, EnemyKind.Totem, 4, 3)]), FixedRandom.Always, (A, Attack(Direction.North)));

        Assert.Contains(events, e => e is StatusApplied { Effect: StatusEffect.Burning });
        Assert.Equal(1, next.E(1)!.Hp);
        Assert.Equal(1, next.E(1)!.BurnRounds);
    }

    [Theory]
    [InlineData(false, 1, 1)]
    [InlineData(true, 2, 2)]
    public void Fire_step_leaves_fire_and_inferno_makes_it_last_longer_and_hurt_more(bool inferno, int rounds, int damage)
    {
        Blessing[] blessings = inferno ? [Blessing.FireStep, Blessing.LivingEmber] : [Blessing.FireStep];
        var hero = Hero(A, HeroClass.Errata, 4, 4) with { Blessings = [.. blessings] };

        var (next, _) = Resolve(State([hero]), FixedRandom.Always, (A, Move(Direction.North)));

        var fire = Assert.Single(next.CurrentRoom.Fires);
        Assert.Equal(new Position(4, 4), fire.Position);
        Assert.Equal(rounds, fire.Rounds);
        Assert.Equal(damage, fire.Damage);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public void Living_armor_raises_the_reflection_chance_to_half(bool livingArmor, int scavengerHp)
    {
        Blessing[] blessings = livingArmor ? [Blessing.BrokenMirror, Blessing.DoubleHeart] : [Blessing.BrokenMirror];
        var hero = Hero(A, HeroClass.Errata, 4, 4) with { Blessings = [.. blessings] };
        var scavenger = Enemy(1, EnemyKind.Scavenger, 4, 3) with { Telegraph = [new Position(4, 4)] };

        var (next, _) = Resolve(State([hero], [scavenger]), new SequenceRandom(30), (A, Interact(Direction.East)));

        Assert.Equal(scavengerHp, next.E(1)!.Hp);
    }

    [Fact]
    public void Toppling_a_brazier_starts_fire_in_a_cross()
    {
        var room = EmptyRoom().WithTerrain(new Position(4, 2), Terrain.Brazier);
        var hero = Hero(A, HeroClass.Escucha, 4, 5);

        var (next, events) = Resolve(State([hero], room: room), (A, Attack(Direction.North)));

        Assert.Contains(events, e => e is BrazierToppled);
        Assert.Equal(Terrain.Floor, next.CurrentRoom.TerrainAt(new Position(4, 2)));
        Assert.Equal(5, next.CurrentRoom.Fires.Count);
    }

    [Fact]
    public void Altars_consume_a_silver_key_and_offer_a_private_draft()
    {
        var room = EmptyRoom().WithTerrain(new Position(4, 3), Terrain.Altar);
        var hero = Hero(A, HeroClass.Errata, 4, 4) with { SilverKeys = 1, Blessings = [Blessing.Lamp] };

        var (next, events) = Resolve(State([hero], room: room), (A, Interact(Direction.North)));

        Assert.Equal(0, next.H(A).SilverKeys);
        Assert.Equal(2, next.H(A).DraftOptions.Count);
        Assert.DoesNotContain(Blessing.Lamp, next.H(A).DraftOptions);
        var offer = Assert.Single(events.OfType<DraftOffered>());
        Assert.Equal(AudienceKind.Player, offer.Audience.Kind);
    }

    private static IReadOnlyList<Synergy> BlessingRulesFor(params Blessing[] blessings) =>
        BlessingRules.SynergiesOf(blessings);
}
