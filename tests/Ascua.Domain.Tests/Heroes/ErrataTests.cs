using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Domain.Tests.Support;
using Ascua.Domain.Views;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests.Heroes;

/// <summary>Errata (05-heroes.md §4): «Los golpes caen donde se anunciaron».</summary>
public sealed class ErrataTests
{
    private static Enemy Striker(int id, params Position[] tiles) => Enemy(id, EnemyKind.Scavenger, 8, 0) with { Telegraph = [.. tiles] };

    [Fact]
    public void She_sees_every_announced_attack_in_the_room_but_not_who_announced_it()
    {
        var errata = Hero(A, HeroClass.Errata, 1, 7, seat: 0);
        var other = Hero(B, HeroClass.Mampostero, 1, 6, seat: 1);
        var state = State([errata, other], [Striker(1, At(8, 1), At(7, 1))]);

        var seen = Assert.Single(PlayerViewProjector.For(state, A).Telegraphs);

        Assert.Equal([At(8, 1), At(7, 1)], seen.Tiles);
        Assert.Null(seen.EnemyId);
        Assert.Empty(PlayerViewProjector.For(state, A).Enemies);
        Assert.Empty(PlayerViewProjector.For(state, B).Telegraphs);
    }

    [Fact]
    public void Correct_moves_the_whole_announcement_of_every_enemy_with_a_tile_in_reach()
    {
        var errata = Hero(A, HeroClass.Errata, 4, 5, seat: 0);
        var first = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1);
        var second = Hero(C, HeroClass.Mampostero, 4, 3, seat: 2);
        var state = State([errata, first, second], [Striker(1, At(4, 3), At(0, 3))]);

        var (next, _) = Resolve(state, (A, Skill(Direction.South)), (B, Interact(Direction.East)), (C, Interact(Direction.East)));

        Assert.Equal(5, next.H(B).Hp);
        Assert.Equal(6, next.H(C).Hp);
        Assert.Equal(HeroCatalog.For(HeroClass.Errata).SkillCooldown - 1, next.H(A).SkillCooldown);
    }

    [Fact]
    public void A_tile_pushed_into_a_wall_or_off_the_room_is_lost()
    {
        var errata = Hero(A, HeroClass.Errata, 4, 1);
        var room = EmptyRoom().WithTerrain(At(3, 0), Terrain.Wall);
        var striker = Enemy(1, EnemyKind.Scavenger, 8, 8) with { Telegraph = [At(4, 0), At(3, 1), At(5, 1)], Delayed = true };

        var (next, _) = Resolve(State([errata], [striker], room), (A, Skill(Direction.North)));

        Assert.Equal([At(5, 0)], next.E(1)!.Telegraph);
    }

    [Fact]
    public void Without_announcements_in_reach_it_does_nothing_and_keeps_the_cooldown()
    {
        var errata = Hero(A, HeroClass.Errata, 4, 7);
        var state = State([errata], [Striker(1, At(4, 1))]);

        var (next, events) = Resolve(state, (A, Skill(Direction.North)));

        Assert.Equal(0, next.H(A).SkillCooldown);
        Assert.Equal([At(4, 1)], state.E(1)!.Telegraph);
        Assert.DoesNotContain(events, e => e is SkillUsed or AttackCorrected);
    }

    [Fact]
    public void A_diverted_blow_that_kills_a_monster_is_her_final_blow_but_nobody_else_learns_it()
    {
        var errata = Hero(A, HeroClass.Errata, 4, 5, seat: 0);
        var witness = Hero(B, HeroClass.Mampostero, 4, 7, seat: 1);
        var moth = Enemy(2, EnemyKind.Moth, 4, 4);
        var state = State([errata, witness], [Striker(1, At(4, 3)), moth]);

        var (next, events) = Resolve(state, (A, Skill(Direction.South)));

        Assert.Null(next.E(2));
        Assert.Equal(1, next.H(A).Embers);
        Assert.Equal(A, events.OfType<EnemyKilled>().Single().Killer);
        Assert.Null(events.SeenBy(next, B).OfType<EnemyKilled>().Single().Killer);
        Assert.Equal(A, events.SeenBy(next, A).OfType<EnemyKilled>().Single().Killer);
        Assert.Equal(1, events.OfType<EntityDamaged>().Single(d => d.Target.EnemyId == 2).Source?.EnemyId);
    }

    [Fact]
    public void Her_use_of_the_skill_is_anonymous_to_the_others()
    {
        var errata = Hero(A, HeroClass.Errata, 4, 5, seat: 0);
        var witness = Hero(B, HeroClass.Mampostero, 4, 6, seat: 1);
        var state = State([errata, witness], [Striker(1, At(4, 3))]);

        var (next, events) = Resolve(state, (A, Skill(Direction.South)));

        Assert.DoesNotContain(events.SeenBy(next, B), e => e is SkillUsed);
        Assert.Contains(events.SeenBy(next, B), e => e is AttackCorrected);
        Assert.Contains(events.SeenBy(next, A), e => e is SkillUsed);
    }

    [Fact]
    public void A_diverted_blow_on_an_ally_blames_the_monster_steals_nothing_and_is_a_secret_betrayal()
    {
        var errata = Hero(A, HeroClass.Errata, 4, 5, seat: 0);
        var ally = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1) with { Embers = 2 };
        var state = State([errata, ally], [Striker(1, At(4, 3))]);

        var (next, events) = Resolve(state, (A, Skill(Direction.South)), (B, Interact(Direction.East)));

        // Daño completo: no es fuego amigo.
        Assert.Equal(5, next.H(B).Hp);
        Assert.Equal(2, next.H(B).Embers);
        Assert.Equal(0, next.H(A).Embers);
        Assert.Equal(1, events.OfType<EntityDamaged>().Single().Source?.EnemyId);
        Assert.Equal(new Betrayal(A, B, 1), Assert.Single(next.Betrayals));
        Assert.Equal(1, next.H(A).Progress.Betrayals);

        Assert.DoesNotContain(events.SeenBy(next, B), e => e is BetrayalRecorded);
        Assert.Contains(events.SeenBy(next, A), e => e is BetrayalRecorded);
        Assert.DoesNotContain("Betrayal", Json.Of(PlayerViewProjector.For(next, B)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_diverted_blow_on_herself_is_not_a_betrayal()
    {
        var errata = Hero(A, HeroClass.Errata, 4, 4);

        var (next, _) = Resolve(State([errata], [Striker(1, At(4, 3))]), (A, Skill(Direction.South)));

        Assert.Equal(3, next.H(A).Hp);
        Assert.Empty(next.Betrayals);
    }

    [Fact]
    public void Betrayals_are_revealed_on_the_final_screen()
    {
        var warden = Model.Enemy.Create(1, EnemyKind.Warden, At(4, 1), maxHp: 24) with { Hp = 1, Phase = 2 };
        var errata = Hero(A, HeroClass.Errata, 4, 2, seat: 0, facing: Direction.North);
        var ally = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1);
        var state = State([errata, ally], [warden]) with { Betrayals = [new Betrayal(A, B, 2), new Betrayal(A, B, 1)] };

        Assert.Null(PlayerViewProjector.For(state, B).Outcome);

        var (finished, events) = Resolve(state, (A, Attack(Direction.North)));

        Assert.Equal(MatchPhase.Finished, finished.Phase);
        Assert.Equal(new Betrayal(A, B, 3), Assert.Single(finished.Outcome!.Betrayals));
        Assert.Contains(events.SeenBy(finished, B), e => e is BetrayalRevealed { Damage: 3 });
        Assert.Equal(new Betrayal(A, B, 3), Assert.Single(PlayerViewProjector.For(finished, B).Outcome!.Betrayals));
    }

    [Fact]
    public void Betrayals_are_summed_by_traitor_and_victim()
    {
        var summed = Outcomes.Summarize([new Betrayal(A, B, 1), new Betrayal(A, B, 2), new Betrayal(C, B, 1)]);

        Assert.Equal([new Betrayal(A, B, 3), new Betrayal(C, B, 1)], summed);
    }

    [Fact]
    public void A_diverted_blow_hits_renegades_and_defeating_one_gives_her_the_reward()
    {
        var errata = Hero(A, HeroClass.Errata, 4, 5, seat: 0);
        var renegade = PresentRenegade(B, HeroClass.Mampostero, 4, 4, seat: 1) with { Hp = 1 };
        var state = State([errata, renegade], [Striker(1, At(4, 3))]);

        var (next, events) = Resolve(state, (A, Skill(Direction.South)), (B, Interact(Direction.East)));

        Assert.Equal(HeroStatus.Extinct, next.H(B).Status);
        Assert.Equal(PathRules.RenegadeDefeatReward, next.H(A).Embers);
        Assert.Empty(next.Betrayals);
        Assert.Null(events.SeenBy(next, B).OfType<RenegadeDefeated>().Single().By);
    }

    [Fact]
    public void The_warden_takes_half_of_its_own_diverted_blow_rounded_up_and_its_front_does_not_save_it()
    {
        var warden = Model.Enemy.Create(1, EnemyKind.Warden, At(4, 2), maxHp: 24) with { Facing = Direction.South, Telegraph = [At(4, 3)] };
        var errata = Hero(A, HeroClass.Errata, 4, 5);

        var (next, _) = Resolve(State([errata], [warden]), (A, Skill(Direction.North)));

        Assert.Equal(23, next.E(1)!.Hp);
    }

    [Fact]
    public void Another_monster_takes_the_full_damage_of_the_diverted_blow()
    {
        var crossbow = Enemy(1, EnemyKind.Crossbowman, 8, 8) with { Telegraph = [At(4, 3)] };
        var totem = Enemy(2, EnemyKind.Totem, 4, 4);
        var errata = Hero(A, HeroClass.Errata, 4, 5);

        var (next, _) = Resolve(State([errata], [crossbow, totem]), (A, Skill(Direction.South)));

        Assert.Equal(1, next.E(2)!.Hp);
    }

    [Fact]
    public void A_hindered_announcement_can_be_corrected_and_fires_when_its_turn_comes()
    {
        var errata = Hero(A, HeroClass.Errata, 4, 5, seat: 0);
        var ally = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1);
        var striker = Striker(1, At(4, 3)) with { Delayed = true };
        var state = State([errata, ally], [striker]);

        var (first, _) = Resolve(state, (A, Skill(Direction.South)), (B, Interact(Direction.East)));
        Assert.Equal(6, first.H(B).Hp);
        Assert.Equal([At(4, 4)], first.E(1)!.Telegraph);
        Assert.Equal(A, first.E(1)!.DivertedBy);

        var (second, _) = Resolve(first, (A, Order.DefaultGuard), (B, Interact(Direction.East)));
        Assert.Equal(5, second.H(B).Hp);
        Assert.Null(second.E(1)!.DivertedBy);
    }

    [Fact]
    public void A_new_announcement_is_no_longer_diverted()
    {
        var errata = Hero(A, HeroClass.Errata, 4, 5);
        var scavenger = Enemy(1, EnemyKind.Scavenger, 4, 3) with { Telegraph = [At(4, 4)] };

        var (next, _) = Resolve(State([errata], [scavenger]), (A, Skill(Direction.East)));

        Assert.Null(next.E(1)!.DivertedBy);
        Assert.Equal(4, next.H(A).Hp);
    }

    [Fact]
    public void As_a_renegade_her_diversions_do_not_hurt_monsters_hit_heroes_for_at_most_one_and_cost_double()
    {
        var errata = PresentRenegade(A, HeroClass.Errata, 4, 5);
        var hero = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1) with { Embers = 2 };
        var crossbow = Enemy(1, EnemyKind.Crossbowman, 8, 8) with { Telegraph = [At(4, 3), At(5, 3)] };
        var totem = Enemy(2, EnemyKind.Totem, 5, 4);

        var (next, _) = Resolve(State([errata, hero], [crossbow, totem]), (A, Skill(Direction.South)), (B, Interact(Direction.East)));

        Assert.Equal(3, next.E(2)!.Hp);
        Assert.Equal(5, next.H(B).Hp);
        Assert.Equal(2, next.H(B).Embers);
        Assert.Empty(next.Betrayals);
        Assert.Equal((HeroCatalog.For(HeroClass.Errata).SkillCooldown * 2) - 1, next.H(A).SkillCooldown);
    }

    [Fact]
    public void As_an_ember_she_loses_her_passive_and_uses_the_ember_orders()
    {
        var errata = Ember(A, 1, 7, heroClass: HeroClass.Errata);
        var other = Hero(B, HeroClass.Mampostero, 1, 8, seat: 1);
        var state = State([errata, other], [Striker(1, At(8, 1))]);

        Assert.Empty(PlayerViewProjector.For(state, A).Telegraphs);
    }

    [Fact]
    public void Two_anonymous_betrayals_are_recorded_but_the_old_betrayal_pact_is_never_fulfilled()
    {
        var errata = Hero(A, HeroClass.Errata, 4, 5, seat: 0) with { Pact = PactKind.BetrayUnseen };
        var ally = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1);
        var state = State([errata, ally], [Striker(1, At(4, 3)), Striker(2, At(4, 3)) with { Position = At(7, 0) }]);

        var (next, _) = Resolve(state, (A, Skill(Direction.South)), (B, Interact(Direction.East)));

        Assert.Equal(2, next.H(A).Progress.Betrayals);
        Assert.False(PactRules.IsFulfilled(next.H(A) with { Pact = PactKind.BetrayTwiceUnconvicted }, next.Heroes));
    }
}
