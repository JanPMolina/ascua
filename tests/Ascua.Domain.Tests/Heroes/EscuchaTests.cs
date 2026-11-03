using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Tests.Support;
using Ascua.Domain.Views;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests.Heroes;

/// <summary>La Escucha (05-heroes.md §8): «Ves lo que tienes cerca».</summary>
public sealed class EscuchaTests
{
    private static Hero Escucha(int x, int y, int seat = 0) => Hero(A, HeroClass.Escucha, x, y, seat);

    [Fact]
    public void She_knows_all_the_terrain_but_only_perceives_what_is_one_tile_away()
    {
        var room = EmptyRoom().WithTerrain(At(8, 8), Terrain.Spikes) with
        {
            Chests = [new Chest(1, At(0, 1), ChestLoot.Nothing, HasSilverKey: false), new Chest(2, At(8, 0), ChestLoot.Nothing, HasSilverKey: false)],
            Fires = [new Fire(At(8, 7), 2, 1)],
        };
        var near = Enemy(1, EnemyKind.Totem, 1, 0);
        var far = Enemy(2, EnemyKind.Totem, 5, 5);
        var view = PlayerViewProjector.For(State([Escucha(0, 0)], [near, far], room), A);

        Assert.Equal(Room.Size * Room.Size, view.Tiles.Count);
        Assert.Contains(view.Tiles, t => t is { Position: { X: 8, Y: 8 }, Terrain: Terrain.Spikes });
        Assert.Equal(1, Assert.Single(view.Enemies).Id);
        Assert.Equal(1, Assert.Single(view.Chests).Id);
        Assert.Empty(view.Fires);
        Assert.Equal(1, view.Self.VisionRadius);
    }

    [Fact]
    public void The_lamp_gives_her_radius_two()
    {
        var escucha = Escucha(0, 0) with { Blessings = [Blessing.Lamp] };

        var view = PlayerViewProjector.For(State([escucha], [Enemy(1, EnemyKind.Totem, 2, 0)]), A);

        Assert.Equal(2, view.Self.VisionRadius);
        Assert.Single(view.Enemies);
    }

    [Fact]
    public void Whatever_stayed_still_does_not_exist_for_her_even_allies()
    {
        var ally = Hero(B, HeroClass.Mampostero, 8, 8, seat: 1);
        var state = State([Escucha(0, 0), ally], [Enemy(1, EnemyKind.Totem, 5, 5)]);

        var (next, _) = Resolve(state, (A, Order.DefaultGuard), (B, Order.DefaultGuard));

        Assert.Null(next.Seen(A, B).Position);
        Assert.Empty(PlayerViewProjector.For(next, A).Enemies);
    }

    [Fact]
    public void She_hears_what_moved_attacked_used_a_skill_opened_a_chest_or_took_a_blow_anywhere_in_the_room()
    {
        var mover = Hero(B, HeroClass.Mampostero, 8, 8, seat: 1);
        var (moved, _) = Resolve(State([Escucha(0, 0), mover]), (A, Order.DefaultGuard), (B, Move(Direction.North)));
        Assert.Equal(At(8, 7), moved.Seen(A, B).Position);
        Assert.Contains(At(8, 7), PlayerViewProjector.For(moved, A).Noise);

        var attacker = Hero(B, HeroClass.Mampostero, 8, 8, seat: 1);
        var (attacked, _) = Resolve(State([Escucha(0, 0), attacker]), (A, Order.DefaultGuard), (B, Attack(Direction.North)));
        Assert.Equal(At(8, 8), attacked.Seen(A, B).Position);

        var room = EmptyRoom() with { Chests = [new Chest(1, At(8, 7), ChestLoot.Nothing, HasSilverKey: false)] };
        var (opened, _) = Resolve(State([Escucha(0, 0), attacker], room: room), (A, Order.DefaultGuard), (B, Interact(Direction.North)));
        Assert.Equal(At(8, 8), opened.Seen(A, B).Position);

        var target = Enemy(1, EnemyKind.Totem, 8, 7);
        var (hit, _) = Resolve(State([Escucha(0, 0), attacker], [target]), (A, Order.DefaultGuard), (B, Attack(Direction.North)));
        Assert.Contains(PlayerViewProjector.For(hit, A).Enemies, e => e.Id == 1);
    }

    [Fact]
    public void Monsters_that_move_are_heard_but_quiet_ones_are_not()
    {
        var mover = Enemy(1, EnemyKind.Scavenger, 8, 8);
        var quiet = Enemy(2, EnemyKind.Totem, 8, 0);

        var (next, _) = Resolve(State([Escucha(0, 0)], [mover, quiet]), (A, Order.DefaultGuard));

        var ids = PlayerViewProjector.For(next, A).Enemies.Select(e => e.Id).ToList();
        Assert.Contains(1, ids);
        Assert.DoesNotContain(2, ids);
    }

    [Fact]
    public void Embers_make_no_noise()
    {
        var ember = Ember(B, 8, 8, seat: 1);

        var (next, _) = Resolve(State([Escucha(0, 0), ember]), (A, Order.DefaultGuard), (B, Move(Direction.North)));

        Assert.Null(next.Seen(A, B).Position);
        Assert.Empty(next.NoisyHeroes);
    }

    [Fact]
    public void An_ember_one_tile_away_is_perceived()
    {
        var ember = Ember(B, 0, 1, seat: 1);

        Assert.Equal(At(0, 1), State([Escucha(0, 0), ember]).Seen(A, B).Position);
    }

    [Fact]
    public void An_announcement_is_seen_only_if_it_falls_on_her_one_tile_away_or_comes_from_a_monster_she_hears()
    {
        var onHer = Enemy(1, EnemyKind.Scavenger, 8, 8) with { Telegraph = [At(0, 0)] };
        var near = Enemy(2, EnemyKind.Scavenger, 8, 7) with { Telegraph = [At(1, 0)] };
        var far = Enemy(3, EnemyKind.Scavenger, 8, 6) with { Telegraph = [At(5, 5)] };
        var heard = Enemy(4, EnemyKind.Scavenger, 8, 5) with { Telegraph = [At(6, 6)] };
        var state = State([Escucha(0, 0)], [onHer, near, far, heard]) with { NoisyEnemies = [4], NoiseTiles = [At(8, 5)] };

        var view = PlayerViewProjector.For(state, A);

        var tiles = view.Telegraphs.SelectMany(t => t.Tiles).ToList();
        Assert.Contains(At(0, 0), tiles);
        Assert.Contains(At(1, 0), tiles);
        Assert.Contains(At(6, 6), tiles);
        Assert.DoesNotContain(At(5, 5), tiles);
    }

    [Fact]
    public void Groping_hits_the_first_thing_within_four_tiles_friend_or_foe_and_walls_stop_it()
    {
        var ally = Hero(B, HeroClass.Mampostero, 4, 1, seat: 1) with { Embers = 1 };
        var (hit, _) = Resolve(State([Escucha(4, 5), ally]), (A, Attack(Direction.North)), (B, Interact(Direction.East)));
        Assert.Equal(1, hit.H(A).Embers);

        var totem = Enemy(1, EnemyKind.Totem, 4, 0);
        var (tooFar, _) = Resolve(State([Escucha(4, 5)], [totem]), (A, Attack(Direction.North)));
        Assert.Equal(3, tooFar.E(1)!.Hp);

        var wall = EmptyRoom().WithTerrain(At(4, 3), Terrain.Wall);
        var (walled, _) = Resolve(State([Escucha(4, 5)], [Enemy(1, EnemyKind.Totem, 4, 2)], wall), (A, Attack(Direction.North)));
        Assert.Equal(3, walled.E(1)!.Hp);
    }

    [Fact]
    public void Groping_deals_two_if_the_target_made_no_noise_and_one_if_it_did()
    {
        var totem = Enemy(1, EnemyKind.Totem, 4, 2);

        var (quiet, _) = Resolve(State([Escucha(4, 5)], [totem]), (A, Attack(Direction.North)));
        Assert.Equal(1, quiet.E(1)!.Hp);

        var (noisy, _) = Resolve(State([Escucha(4, 5)], [totem]) with { NoisyEnemies = [1] }, (A, Attack(Direction.North)));
        Assert.Equal(2, noisy.E(1)!.Hp);
    }

    [Fact]
    public void Groping_a_quiet_ally_is_friendly_fire_over_the_bigger_blow()
    {
        var ally = Hero(B, HeroClass.Mampostero, 4, 3, seat: 1);

        var (next, _) = Resolve(State([Escucha(4, 5), ally]), (A, Attack(Direction.North)), (B, Interact(Direction.East)));

        Assert.Equal(5, next.H(B).Hp);
    }

    [Fact]
    public void Shout_reveals_the_whole_room_to_her_side_until_the_next_resolution_and_spends_the_cooldown()
    {
        var ally = Hero(B, HeroClass.Mampostero, 8, 8, seat: 1);
        var renegade = PresentRenegade(C, HeroClass.Errata, 0, 8, seat: 2);
        var totem = Enemy(1, EnemyKind.Totem, 0, 3);
        var state = State([Escucha(0, 0), ally, renegade], [totem]);

        var (shouted, events) = Resolve(state, (A, Skill(Direction.North)), (B, Order.DefaultGuard), (C, Order.DefaultGuard));

        Assert.Contains(events, e => e is Shouted);
        Assert.Equal(HeroCatalog.For(HeroClass.Escucha).SkillCooldown - 1, shouted.H(A).SkillCooldown);
        Assert.Single(PlayerViewProjector.For(shouted, B).Enemies);
        Assert.Equal(At(8, 8), shouted.Seen(A, B).Position);
        Assert.Empty(PlayerViewProjector.For(shouted, C).Enemies);

        var (after, _) = Resolve(shouted, (A, Order.DefaultGuard), (B, Order.DefaultGuard), (C, Order.DefaultGuard));
        Assert.False(after.H(A).Shouting);
        Assert.Empty(PlayerViewProjector.For(after, B).Enemies);
    }

    [Fact]
    public void After_a_shout_every_monster_targets_her_this_round()
    {
        var ally = Hero(B, HeroClass.Mampostero, 2, 2, seat: 1);
        var scavenger = Enemy(1, EnemyKind.Scavenger, 2, 3);

        var (quiet, _) = Resolve(State([Escucha(8, 8), ally], [scavenger]), (A, Order.DefaultGuard), (B, Order.DefaultGuard));
        Assert.Equal([At(2, 2)], quiet.E(1)!.Telegraph);

        var (shouted, _) = Resolve(State([Escucha(8, 8), ally], [scavenger]), (A, Skill(Direction.North)), (B, Order.DefaultGuard));
        Assert.Empty(shouted.E(1)!.Telegraph);
        Assert.True(shouted.E(1)!.Position.DistanceTo(At(8, 8)) < At(2, 3).DistanceTo(At(8, 8)));
    }

    [Fact]
    public void As_a_renegade_shout_reveals_the_room_to_renegades_and_attracts_nobody()
    {
        var renegade = PresentRenegade(A, HeroClass.Escucha, 0, 0);
        var other = PresentRenegade(C, HeroClass.Errata, 8, 0, seat: 2);
        var hero = Hero(B, HeroClass.Mampostero, 2, 2, seat: 1);
        var scavenger = Enemy(1, EnemyKind.Scavenger, 2, 3);

        var (next, _) = Resolve(State([renegade, hero, other], [scavenger]), (A, Skill(Direction.North)), (B, Order.DefaultGuard), (C, Order.DefaultGuard));

        Assert.Equal([At(2, 2)], next.E(1)!.Telegraph);
        Assert.NotEmpty(PlayerViewProjector.For(next, C).Enemies);
        Assert.Equal((HeroCatalog.For(HeroClass.Escucha).SkillCooldown * 2) - 1, next.H(A).SkillCooldown);
    }

    [Fact]
    public void As_a_renegade_groping_deals_at_most_one()
    {
        var renegade = PresentRenegade(A, HeroClass.Escucha, 4, 5) with { WarnedHeroes = [B], Blessings = [Blessing.StoneFist] };
        var hero = Hero(B, HeroClass.Mampostero, 4, 3, seat: 1);

        var (next, _) = Resolve(State([renegade, hero]), (A, Attack(Direction.North)), (B, Interact(Direction.East)));

        Assert.Equal(5, next.H(B).Hp);
    }

    [Fact]
    public void The_stowaways_host_makes_noise_like_any_monster_but_he_does_not()
    {
        var polizon = Hero(B, HeroClass.Polizon, 8, 8, seat: 1) with { HostEnemyId = 1, BoardedRound = 1 };
        var host = Enemy(1, EnemyKind.Scavenger, 8, 8);

        var (next, _) = Resolve(State([Escucha(0, 0), polizon], [host]), (A, Order.DefaultGuard), (B, Move(Direction.North)));

        Assert.Contains(PlayerViewProjector.For(next, A).Enemies, e => e.Id == 1);
        Assert.Null(next.Seen(A, B).Position);
        Assert.DoesNotContain(B, next.NoisyHeroes);
    }

    [Fact]
    public void As_an_ember_she_sees_like_any_ember()
    {
        var ember = Ember(A, 0, 0, heroClass: HeroClass.Escucha);
        var view = PlayerViewProjector.For(State([ember], [Enemy(1, EnemyKind.Totem, 2, 2)]), A);

        Assert.Equal(PathRules.EmberVisionRadius, view.Self.VisionRadius);
        Assert.Single(view.Enemies);
        Assert.NotEqual(Room.Size * Room.Size, view.Tiles.Count);
    }

    [Fact]
    public void Noise_does_not_carry_over_to_the_next_room()
    {
        var ally = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1);

        var (next, _) = Resolve(TwoRooms([Escucha(0, 8), ally]), (A, Order.DefaultGuard), (B, Move(Direction.North)));

        Assert.Equal(1, next.RoomIndex);
        Assert.Empty(next.NoiseTiles);
        Assert.Empty(next.NoisyHeroes);
    }
}
