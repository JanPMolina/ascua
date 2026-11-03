using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Tests.Support;
using Ascua.Domain.Views;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests.Heroes;

/// <summary>Polizón (05-heroes.md §7): «Eres tu cuerpo».</summary>
public sealed class PolizonTests
{
    private static Hero Polizon(int x, int y, int seat = 0, PlayerId? player = null) => Hero(player ?? A, HeroClass.Polizon, x, y, seat);

    private static Hero Inside(int enemyId, int x, int y, int boarded = 1, int seat = 0) =>
        Polizon(x, y, seat) with { HostEnemyId = enemyId, BoardedRound = boarded };

    [Fact]
    public void Boarding_takes_him_inside_the_adjacent_enemy_and_cancels_its_announcement()
    {
        var scavenger = Enemy(1, EnemyKind.Scavenger, 4, 4) with { Telegraph = [At(4, 5)] };
        var ally = Hero(B, HeroClass.Mampostero, 0, 8, seat: 1);

        var (next, events) = Resolve(State([Polizon(4, 5), ally], [scavenger]), (A, Skill(Direction.North)));

        Assert.Equal(1, next.H(A).HostEnemyId);
        Assert.Equal(At(4, 4), next.H(A).Position);
        Assert.Equal(3, next.H(A).Hp);
        Assert.Empty(next.E(1)!.Telegraph);
        Assert.Equal(0, next.H(A).SkillCooldown);
        Assert.DoesNotContain(events.SeenBy(next, B), e => e is HostBoarded or SkillUsed);
    }

    [Fact]
    public void Inside_a_host_he_disappears_from_everyone_even_allies_and_the_host_looks_like_any_monster()
    {
        var host = Enemy(1, EnemyKind.Scavenger, 4, 4);
        var ally = Hero(B, HeroClass.Mampostero, 4, 5, seat: 1);
        var state = State([Inside(1, 4, 4), ally], [host]);

        var seen = state.Seen(B, A);
        Assert.Null(seen.Position);
        Assert.Null(seen.Facing);
        Assert.Equal(HeroStatus.Active, seen.Status);
        Assert.Equal(1, PlayerViewProjector.For(state, A).Self.HostEnemyId);
        Assert.DoesNotContain("HostEnemyId\":1", Json.Of(PlayerViewProjector.For(state, B)), StringComparison.Ordinal);
        Assert.Equal(host.Hp, Assert.Single(PlayerViewProjector.For(state, B).Enemies).Hp);
    }

    [Fact]
    public void Moving_carries_the_host_at_its_speed()
    {
        var hopper = Enemy(1, EnemyKind.Hopper, 4, 4);

        var (next, _) = Resolve(State([Inside(1, 4, 4)], [hopper]), (A, Move(Direction.North)));

        Assert.Equal(At(4, 2), next.E(1)!.Position);
        Assert.Equal(At(4, 2), next.H(A).Position);
    }

    [Fact]
    public void The_host_bounces_like_anything_else_and_cannot_be_walked_into_a_crack()
    {
        var host = Enemy(1, EnemyKind.Scavenger, 4, 4);
        var room = EmptyRoom().WithTerrain(At(4, 3), Terrain.Crack);

        var (next, _) = Resolve(State([Inside(1, 4, 4)], [host], room), (A, Move(Direction.North)));

        Assert.Equal(At(4, 4), next.E(1)!.Position);
    }

    [Fact]
    public void Attacking_makes_the_host_strike_at_once_without_announcing_and_a_kill_is_his()
    {
        var host = Enemy(1, EnemyKind.Scavenger, 4, 4);
        var moth = Enemy(2, EnemyKind.Moth, 4, 3);
        var witness = Hero(B, HeroClass.Mampostero, 4, 6, seat: 1);

        var (next, events) = Resolve(State([Inside(1, 4, 4), witness], [host, moth]), (A, Attack(Direction.North)));

        Assert.Null(next.E(2));
        Assert.Equal(1, next.H(A).Embers);
        Assert.Empty(next.E(1)!.Telegraph);
        Assert.Null(events.SeenBy(next, B).OfType<EnemyKilled>().Single().Killer);
        Assert.Equal(1, events.OfType<EntityDamaged>().Single(d => d.Target.EnemyId == 2).Source?.EnemyId);
    }

    [Fact]
    public void A_crossbowman_host_shoots_the_first_target_in_line_with_its_damage_and_others_only_the_next_tile()
    {
        var host = Enemy(1, EnemyKind.Crossbowman, 4, 6);
        var near = Enemy(2, EnemyKind.Totem, 4, 2);
        var far = Enemy(3, EnemyKind.Totem, 4, 1);

        var (shot, _) = Resolve(State([Inside(1, 4, 6)], [host, near, far]), (A, Attack(Direction.North)));
        Assert.Equal(1, shot.E(2)!.Hp);
        Assert.Equal(3, shot.E(3)!.Hp);

        var melee = Enemy(1, EnemyKind.Scavenger, 4, 6);
        var (missed, _) = Resolve(State([Inside(1, 4, 6)], [melee, near]), (A, Attack(Direction.North)));
        Assert.Equal(3, missed.E(2)!.Hp);
    }

    [Fact]
    public void Leaving_steps_out_to_that_tile_and_leaves_the_host_without_acting_that_round()
    {
        var host = Enemy(1, EnemyKind.Scavenger, 4, 4);
        var witness = Hero(B, HeroClass.Mampostero, 7, 4, seat: 1);

        var (next, events) = Resolve(State([Inside(1, 4, 4), witness], [host]), (A, Skill(Direction.West)));

        Assert.False(next.H(A).IsInHost);
        Assert.Equal(At(3, 4), next.H(A).Position);
        Assert.Equal(At(4, 4), next.E(1)!.Position);
        Assert.Empty(next.E(1)!.Telegraph);
        Assert.Equal(HeroCatalog.For(HeroClass.Polizon).SkillCooldown - 1, next.H(A).SkillCooldown);
        Assert.Contains(events, e => e is HostExited);
    }

    [Fact]
    public void Leaving_to_an_occupied_tile_lands_on_the_nearest_free_one()
    {
        var host = Enemy(1, EnemyKind.Scavenger, 4, 4);
        var blocker = Enemy(2, EnemyKind.Totem, 3, 4);

        var (next, _) = Resolve(State([Inside(1, 4, 4)], [host, blocker]), (A, Skill(Direction.West)));

        Assert.False(next.H(A).IsInHost);
        Assert.True(next.H(A).HasBody);
        Assert.NotEqual(At(3, 4), next.H(A).Position);
        Assert.NotEqual(At(4, 4), next.H(A).Position);
    }

    [Fact]
    public void Guarding_and_interacting_do_nothing_inside()
    {
        var host = Enemy(1, EnemyKind.Scavenger, 4, 4);
        var room = EmptyRoom() with { Chests = [new Chest(1, At(4, 3), ChestLoot.Nothing, HasSilverKey: false)] };
        var state = State([Inside(1, 4, 4)], [host], room);

        var (guarded, guardEvents) = Resolve(state, (A, Order.DefaultGuard));
        Assert.DoesNotContain(guardEvents, e => e is HeroGuarded);
        Assert.True(guarded.H(A).IsInHost);

        var (interacted, _) = Resolve(state, (A, Interact(Direction.North)));
        Assert.False(interacted.CurrentRoom.Chests[0].Opened);
    }

    [Fact]
    public void The_host_neither_announces_nor_acts_on_its_own_and_monsters_ignore_him()
    {
        var host = Enemy(1, EnemyKind.Scavenger, 4, 4);
        var other = Enemy(2, EnemyKind.Scavenger, 4, 2);
        var ally = Hero(B, HeroClass.Mampostero, 8, 8, seat: 1);

        var (next, _) = Resolve(State([Inside(1, 4, 4), ally], [host, other]), (A, Order.DefaultGuard), (B, Order.DefaultGuard));

        Assert.Equal(At(4, 4), next.E(1)!.Position);
        Assert.Empty(next.E(1)!.Telegraph);
        Assert.True(next.E(2)!.Position.DistanceTo(At(8, 8)) < At(4, 2).DistanceTo(At(8, 8)));
    }

    [Fact]
    public void Every_two_rounds_inside_the_host_loses_one_and_he_heals_one()
    {
        var host = Enemy(1, EnemyKind.Scavenger, 4, 4);
        var polizon = Inside(1, 4, 4, boarded: 1) with { Hp = 1 };
        var state = State([polizon], [host], round: 2);

        var (odd, _) = Resolve(state, (A, Order.DefaultGuard));
        Assert.Equal(2, odd.E(1)!.Hp);
        Assert.Equal(1, odd.H(A).Hp);

        var (even, events) = Resolve(odd, (A, Order.DefaultGuard));
        Assert.Equal(1, even.E(1)!.Hp);
        Assert.Equal(2, even.H(A).Hp);
        Assert.Contains(events, e => e is HostDrained);
    }

    [Fact]
    public void If_the_drain_kills_the_host_the_final_blow_is_his_and_he_steps_out_unharmed()
    {
        var host = Enemy(1, EnemyKind.Scavenger, 4, 4) with { Hp = 1 };
        var polizon = Inside(1, 4, 4, boarded: 1) with { Hp = 2 };

        var (next, _) = Resolve(State([polizon], [host], round: 3), (A, Order.DefaultGuard));

        Assert.Null(next.E(1));
        Assert.False(next.H(A).IsInHost);
        Assert.Equal(At(4, 4), next.H(A).Position);
        Assert.Equal(3, next.H(A).Hp);
        Assert.Equal(1, next.H(A).Embers);
    }

    [Fact]
    public void If_another_kills_the_host_he_steps_out_on_that_tile_and_takes_one_and_the_blow_is_theirs()
    {
        var host = Enemy(1, EnemyKind.Moth, 4, 4);
        var hunter = Hero(B, HeroClass.Mampostero, 4, 5, seat: 1);

        var (next, events) = Resolve(State([Inside(1, 4, 4), hunter], [host]), (B, Attack(Direction.North)));

        Assert.Null(next.E(1));
        Assert.False(next.H(A).IsInHost);
        Assert.Equal(At(4, 4), next.H(A).Position);
        Assert.Equal(2, next.H(A).Hp);
        Assert.Equal(1, next.H(B).Embers);
        Assert.Equal(0, next.H(A).Embers);
        Assert.Equal(B, events.OfType<EnemyKilled>().Single().Killer);
    }

    [Fact]
    public void A_hero_who_hits_the_host_does_not_learn_who_is_inside()
    {
        var host = Enemy(1, EnemyKind.Scavenger, 4, 4);
        var hunter = Hero(B, HeroClass.Mampostero, 4, 5, seat: 1);

        var (next, events) = Resolve(State([Inside(1, 4, 4), hunter], [host]), (B, Attack(Direction.North)));

        Assert.Equal(1, next.E(1)!.Hp);
        Assert.DoesNotContain(events.SeenBy(next, B), e => e is HostExited or HostBoarded);
        Assert.Null(next.Seen(B, A).Position);
    }

    [Fact]
    public void A_hit_on_an_ally_from_inside_blames_the_monster_steals_nothing_and_is_a_betrayal()
    {
        var host = Enemy(1, EnemyKind.Crossbowman, 4, 4);
        var ally = Hero(B, HeroClass.Mampostero, 4, 2, seat: 1) with { Embers = 2 };

        var (next, events) = Resolve(State([Inside(1, 4, 4), ally], [host]), (A, Attack(Direction.North)), (B, Interact(Direction.East)));

        Assert.Equal(4, next.H(B).Hp);
        Assert.Equal(2, next.H(B).Embers);
        Assert.Equal(0, next.H(A).Embers);
        Assert.Equal(1, events.OfType<EntityDamaged>().Single(d => d.Target.Hero == B).Source?.EnemyId);
        Assert.Equal(new Betrayal(A, B, 2), Assert.Single(next.Betrayals));
    }

    [Fact]
    public void The_warden_cannot_be_boarded_it_turns_around_and_allies_can_hit_its_back_that_same_round()
    {
        var warden = Model.Enemy.Create(1, EnemyKind.Warden, At(4, 4), maxHp: 24) with { Facing = Direction.South, Telegraph = [At(0, 0)] };
        var polizon = Polizon(3, 4);
        var ally = Hero(B, HeroClass.Mampostero, 4, 5, seat: 1);

        var (next, events) = Resolve(State([polizon, ally], [warden]), (A, Skill(Direction.East)), (B, Attack(Direction.North)));

        Assert.False(next.H(A).IsInHost);
        Assert.Equal(At(3, 4), next.H(A).Position);
        Assert.Equal(23, next.E(1)!.Hp);
        Assert.Equal(HeroRules.WardenTurnCooldown - 1, next.H(A).SkillCooldown);
        Assert.Contains(events, e => e is WardenTurned { Facing: Direction.North });

        // Sin el giro, de frente no le hace nada; el anuncio tampoco cambia.
        var (unturned, _) = Resolve(State([polizon, ally], [warden]), (A, Order.DefaultGuard), (B, Attack(Direction.North)));
        Assert.Equal(24, unturned.E(1)!.Hp);
        Assert.Contains(events, e => e is not AttackCorrected);
    }

    [Fact]
    public void The_totem_cannot_be_boarded_and_trying_keeps_the_cooldown()
    {
        var totem = Enemy(1, EnemyKind.Totem, 4, 4);

        var (next, _) = Resolve(State([Polizon(4, 5)], [totem]), (A, Skill(Direction.North)));

        Assert.False(next.H(A).IsInHost);
        Assert.Equal(0, next.H(A).SkillCooldown);
    }

    [Fact]
    public void Trying_to_board_nothing_keeps_the_cooldown()
    {
        var (next, _) = Resolve(State([Polizon(4, 5)]), (A, Skill(Direction.North)));

        Assert.Equal(0, next.H(A).SkillCooldown);
    }

    [Fact]
    public void The_room_is_not_cleared_while_a_host_is_alive()
    {
        var host = Enemy(1, EnemyKind.Scavenger, 2, 2);
        var ally = Hero(B, HeroClass.Mampostero, 4, 1, seat: 1);

        var (next, _) = Resolve(TwoRooms([Inside(1, 2, 2), ally], [host]), (B, Move(Direction.North)));

        Assert.Equal(0, next.RoomIndex);
    }

    [Fact]
    public void Life_zero_inside_does_not_happen_he_steps_out_first_and_falls_outside()
    {
        var host = Enemy(1, EnemyKind.Scavenger, 4, 4);
        var polizon = Inside(1, 4, 4) with { Hp = 1, BurnRounds = 2, BurnDamage = 1 };
        var ally = Hero(B, HeroClass.Mampostero, 8, 8, seat: 1);

        var (next, _) = Resolve(State([polizon, ally], [host]), (A, Order.DefaultGuard), (B, Order.DefaultGuard));

        Assert.Equal(HeroStatus.Down, next.H(A).Status);
        Assert.False(next.H(A).IsInHost);
        Assert.NotEqual(At(4, 4), next.H(A).Position);
    }

    [Fact]
    public void As_a_renegade_he_can_board_without_drain_his_host_spares_monsters_and_hits_heroes_for_at_most_one()
    {
        var host = Enemy(1, EnemyKind.Crossbowman, 4, 4);
        var totem = Enemy(2, EnemyKind.Totem, 4, 3);
        var renegade = PresentRenegade(A, HeroClass.Polizon, 4, 4) with { HostEnemyId = 1, BoardedRound = 1 };
        var hero = Hero(B, HeroClass.Mampostero, 5, 4, seat: 1) with { Embers = 1 };

        var (spared, _) = Resolve(State([renegade, hero], [host, totem], round: 3), (A, Attack(Direction.North)), (B, Interact(Direction.North)));
        Assert.Equal(3, spared.E(2)!.Hp);
        Assert.Equal(2, spared.E(1)!.Hp);

        var (hit, _) = Resolve(State([renegade, hero], [host]), (A, Attack(Direction.East)), (B, Interact(Direction.North)));
        Assert.Equal(5, hit.H(B).Hp);
        Assert.Equal(1, hit.H(B).Embers);
        Assert.Empty(hit.Betrayals);

        var outsider = PresentRenegade(A, HeroClass.Polizon, 4, 5);
        var (boarded, _) = Resolve(State([outsider, hero], [host]), (A, Skill(Direction.North)), (B, Interact(Direction.North)));
        Assert.True(boarded.H(A).IsInHost);

        var (left, _) = Resolve(boarded, (A, Skill(Direction.South)), (B, Interact(Direction.North)));
        Assert.Equal((HeroCatalog.For(HeroClass.Polizon).SkillCooldown * 2) - 1, left.H(A).SkillCooldown);
    }

    [Fact]
    public void As_an_ember_he_has_no_host()
    {
        var chooser = ExtinctHero(A, HeroClass.Polizon, saved: 2) with { PathChoice = PathKind.Friendship };
        var ally = Hero(B, HeroClass.Mampostero, 0, 8, seat: 1);

        var (next, _) = Resolve(TwoRooms([chooser, ally]), (B, Order.DefaultGuard));

        Assert.True(next.H(A).IsEmber);
        Assert.False(next.H(A).IsInHost);
    }
}
