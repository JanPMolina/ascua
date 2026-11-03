using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Domain.Tests.Support;
using Ascua.Domain.Views;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests.Heroes;

/// <summary>Hilván (05-heroes.md §6): «Tienes un solo cuerpo».</summary>
public sealed class HilvanTests
{
    private static Hero Hilvan(int x1, int y1, int x2, int y2, PlayerId? player = null, int seat = 0) =>
        Hero(player ?? A, HeroClass.Hilvan, x1, y1, seat) with { SecondBody = At(x2, y2) };

    [Theory]
    [InlineData(Direction.North, 2, 3, 6, 3)]
    [InlineData(Direction.South, 2, 5, 6, 5)]
    [InlineData(Direction.West, 1, 4, 7, 4)]
    [InlineData(Direction.East, 3, 4, 5, 4)]
    public void Moves_are_equal_north_and_south_and_mirrored_east_and_west(Direction direction, int x1, int y1, int x2, int y2)
    {
        var (next, _) = Resolve(State([Hilvan(2, 4, 6, 4)]), (A, Move(direction)));

        Assert.Equal(At(x1, y1), next.H(A).Position);
        Assert.Equal(At(x2, y2), next.H(A).SecondBody);
    }

    [Fact]
    public void Each_body_bounces_on_its_own_and_a_wall_misaligns_them()
    {
        var room = EmptyRoom().WithTerrain(At(6, 3), Terrain.Wall);

        var (next, events) = Resolve(State([Hilvan(2, 4, 6, 4)], room: room), (A, Move(Direction.North)));

        Assert.Equal(At(2, 3), next.H(A).Position);
        Assert.Equal(At(6, 4), next.H(A).SecondBody);
        Assert.Contains(events, e => e is MoveBounced { Body: 1 });
    }

    [Fact]
    public void If_both_aim_at_the_same_tile_neither_enters()
    {
        var (next, _) = Resolve(State([Hilvan(3, 4, 5, 4)]), (A, Move(Direction.East)));

        Assert.Equal(At(3, 4), next.H(A).Position);
        Assert.Equal(At(5, 4), next.H(A).SecondBody);
    }

    [Fact]
    public void Adjacent_bodies_cannot_swap_places()
    {
        var (next, _) = Resolve(State([Hilvan(4, 4, 5, 4)]), (A, Move(Direction.East)));

        Assert.Equal(At(4, 4), next.H(A).Position);
        Assert.Equal(At(5, 4), next.H(A).SecondBody);
    }

    [Fact]
    public void A_body_can_enter_the_tile_the_other_one_leaves()
    {
        var (next, _) = Resolve(State([Hilvan(4, 4, 4, 3)]), (A, Move(Direction.North)));

        Assert.Equal(At(4, 3), next.H(A).Position);
        Assert.Equal(At(4, 2), next.H(A).SecondBody);
    }

    [Fact]
    public void A_body_blocks_others_and_is_found_on_the_board()
    {
        var other = Hero(B, HeroClass.Mampostero, 6, 5, seat: 1);

        var (next, _) = Resolve(State([Hilvan(2, 4, 6, 4), other]), (B, Move(Direction.North)));

        Assert.Equal(At(6, 5), next.H(B).Position);
    }

    [Fact]
    public void Attacks_use_both_bodies_mirrored()
    {
        var left = Enemy(1, EnemyKind.Totem, 1, 4);
        var right = Enemy(2, EnemyKind.Totem, 7, 4);

        var (next, _) = Resolve(State([Hilvan(2, 4, 6, 4)], [left, right]), (A, Attack(Direction.West)));

        Assert.Equal(2, next.E(1)!.Hp);
        Assert.Equal(2, next.E(2)!.Hp);
    }

    [Fact]
    public void Interactions_use_both_bodies_mirrored()
    {
        var room = EmptyRoom() with
        {
            Chests =
            [
                new Chest(1, At(1, 4), ChestLoot.Nothing, HasSilverKey: false),
                new Chest(2, At(7, 4), ChestLoot.Nothing, HasSilverKey: false),
            ],
        };

        var (next, _) = Resolve(State([Hilvan(2, 4, 6, 4)], room: room), (A, Interact(Direction.West)));

        Assert.All(next.CurrentRoom.Chests, c => Assert.True(c.Opened));
        Assert.Equal(2, next.H(A).Embers);
        Assert.Equal(2, next.H(A).Progress.ChestsOpened);
    }

    [Fact]
    public void Guarding_protects_both_bodies()
    {
        var striker = Enemy(1, EnemyKind.Scavenger, 8, 8) with { Telegraph = [At(2, 4), At(6, 4)] };

        var (next, _) = Resolve(State([Hilvan(2, 4, 6, 4)], [striker]), (A, Order.DefaultGuard));

        Assert.Equal(5, next.H(A).Hp);
    }

    [Fact]
    public void A_blow_on_each_body_is_two_blows_on_the_shared_life()
    {
        var striker = Enemy(1, EnemyKind.Scavenger, 8, 8) with { Telegraph = [At(2, 4), At(6, 4)] };

        var (next, events) = Resolve(State([Hilvan(2, 4, 6, 4)], [striker]), (A, Interact(Direction.East)));

        Assert.Equal(3, next.H(A).Hp);
        Assert.Equal(2, events.OfType<EntityDamaged>().Count());
    }

    [Fact]
    public void Traps_and_fire_affect_each_body()
    {
        var room = EmptyRoom().WithTerrain(At(2, 4), Terrain.Spikes).WithTerrain(At(6, 4), Terrain.Spikes);

        var (spiked, _) = Resolve(State([Hilvan(2, 4, 6, 4)], room: room, round: 2), (A, Interact(Direction.East)));
        Assert.Equal(3, spiked.H(A).Hp);

        var burning = EmptyRoom() with { Fires = [new Fire(At(2, 4), 2, 1), new Fire(At(6, 4), 2, 1)] };
        var (burnt, _) = Resolve(State([Hilvan(2, 4, 6, 4)], room: burning), (A, Interact(Direction.East)));
        Assert.Equal(3, burnt.H(A).Hp);
    }

    [Fact]
    public void If_either_body_falls_into_a_crack_hilvan_goes_extinct()
    {
        var other = Hero(B, HeroClass.Mampostero, 0, 8, seat: 1);
        var room = EmptyRoom().WithTerrain(At(6, 3), Terrain.Crack);

        var (next, events) = Resolve(State([Hilvan(2, 4, 6, 4), other], room: room), (A, Move(Direction.North)));

        Assert.Equal(HeroStatus.Extinct, next.H(A).Status);
        Assert.Null(next.H(A).SecondBody);
        Assert.Contains(events, e => e is HeroExtinguished { Reason: ExtinctionReason.Chasm });
    }

    [Fact]
    public void Tug_damages_everything_between_the_bodies_and_spends_the_cooldown()
    {
        var totem = Enemy(1, EnemyKind.Totem, 4, 4);

        var (next, _) = Resolve(State([Hilvan(2, 4, 6, 4)], [totem]), (A, Skill(Direction.North)));

        Assert.Equal(1, next.E(1)!.Hp);
        Assert.Equal(HeroCatalog.For(HeroClass.Hilvan).SkillCooldown - 1, next.H(A).SkillCooldown);
    }

    [Fact]
    public void Tug_is_normal_friendly_fire_on_allies()
    {
        var ally = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1) with { Embers = 1 };

        var (next, _) = Resolve(State([Hilvan(2, 4, 6, 4), ally]), (A, Skill(Direction.North)), (B, Interact(Direction.East)));

        Assert.Equal(5, next.H(B).Hp);
        Assert.Equal(1, next.H(A).Embers);
        Assert.Equal(0, next.H(B).Embers);
    }

    [Theory]
    [InlineData(2, 4, 6, 5)] // No están en la misma fila ni columna.
    [InlineData(4, 4, 5, 4)] // A menos de 2 casillas.
    public void Tug_does_nothing_if_the_bodies_are_not_stretched(int x1, int y1, int x2, int y2)
    {
        var totem = Enemy(1, EnemyKind.Totem, 4, 2);

        var (next, _) = Resolve(State([Hilvan(x1, y1, x2, y2)], [totem]), (A, Skill(Direction.North)));

        Assert.Equal(0, next.H(A).SkillCooldown);
    }

    [Fact]
    public void Tug_does_nothing_with_a_wall_between_or_nothing_to_hit()
    {
        var totem = Enemy(1, EnemyKind.Totem, 4, 4);
        var room = EmptyRoom().WithTerrain(At(5, 4), Terrain.Wall);

        var (walled, _) = Resolve(State([Hilvan(2, 4, 6, 4)], [totem], room), (A, Skill(Direction.North)));
        Assert.Equal(3, walled.E(1)!.Hp);
        Assert.Equal(0, walled.H(A).SkillCooldown);

        var (empty, _) = Resolve(State([Hilvan(2, 4, 6, 4)]), (A, Skill(Direction.North)));
        Assert.Equal(0, empty.H(A).SkillCooldown);
    }

    [Fact]
    public void Tug_ignores_the_wardens_front_immunity()
    {
        var warden = Model.Enemy.Create(1, EnemyKind.Warden, At(4, 4), maxHp: 24) with { Facing = Direction.West };

        var (next, _) = Resolve(State([Hilvan(2, 4, 6, 4)], [warden]), (A, Skill(Direction.North)));

        Assert.Equal(22, next.E(1)!.Hp);
    }

    [Fact]
    public void Fallen_both_bodies_stay_on_the_board_and_either_can_be_lifted()
    {
        var fallen = Hilvan(2, 4, 6, 4) with { Hp = 0, Status = HeroStatus.Down };
        var rescuer = Hero(B, HeroClass.Mampostero, 6, 5, seat: 1);

        var (next, _) = Resolve(State([fallen, rescuer]), (B, Interact(Direction.North)));

        Assert.Equal(HeroStatus.Active, next.H(A).Status);
        Assert.Equal(At(6, 4), next.H(A).SecondBody);
        Assert.Equal(2, next.H(B).Embers);
    }

    [Fact]
    public void A_single_body_on_the_stairs_is_enough()
    {
        var (next, _) = Resolve(TwoRooms([Hilvan(2, 5, 4, 4)]), (A, Move(Direction.North)));

        Assert.Equal(1, next.RoomIndex);
    }

    [Fact]
    public void Entering_a_room_places_the_second_body_two_tiles_away_in_the_same_row()
    {
        var hilvan = Hilvan(4, 4, 2, 4);
        var ally = Hero(B, HeroClass.Mampostero, 0, 0, seat: 1);

        var (next, _) = Resolve(TwoRooms([hilvan, ally]), (A, Move(Direction.North)));

        Assert.Equal(MatchFactory.SpawnPoints[0], next.H(A).Position);
        Assert.Equal(MatchFactory.SpawnPoints[0].Step(Direction.West, HeroRules.BodySpacing), next.H(A).SecondBody);
        Assert.Equal(MatchFactory.SpawnPoints[1], next.H(B).Position);
    }

    [Fact]
    public void A_match_starts_with_both_bodies_in_the_same_row_two_tiles_apart_without_stepping_on_a_spawn()
    {
        for (var seat = 0; seat < 4; seat++)
        {
            var players = Enumerable.Range(0, 4)
                .Select(i => new PlayerSetup(new PlayerId($"p{i}"), i == seat ? HeroClass.Hilvan : HeroClass.Errata))
                .ToList();

            var (state, _) = MatchFactory.Create(players, new Abstractions.SeededRandom(7));

            var hilvan = state.Heroes[seat];
            Assert.Equal(hilvan.Position.Y, hilvan.SecondBody!.Value.Y);
            Assert.Equal(2, hilvan.Position.DistanceTo(hilvan.SecondBody.Value));
            Assert.DoesNotContain(state.Heroes, h => h.Player != hilvan.Player && h.Position == hilvan.SecondBody);
        }
    }

    [Fact]
    public void The_ember_is_a_single_one_on_the_main_body_and_the_second_body_comes_back_on_rebirth()
    {
        var chooser = ExtinctHero(A, HeroClass.Hilvan, saved: 4) with { Position = At(2, 4), PathChoice = PathKind.Friendship };
        var ally = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1);

        var (ember, _) = Resolve(TwoRooms([chooser, ally]), (B, Order.DefaultGuard));
        Assert.True(ember.H(A).IsEmber);
        Assert.Null(ember.H(A).SecondBody);

        var (reborn, _) = Resolve(ember, (B, Move(Direction.North)));
        Assert.True(reborn.H(A).IsActive);
        Assert.Equal(MatchFactory.SpawnPoints[0], reborn.H(A).Position);
        Assert.Equal(MatchFactory.SpawnPoints[0].Step(Direction.West, HeroRules.BodySpacing), reborn.H(A).SecondBody);
    }

    [Fact]
    public void A_renegade_hilvan_reappears_with_a_second_body_and_tug_deals_at_most_one()
    {
        var hilvan = PresentRenegade(A, HeroClass.Hilvan, 4, 4) with { AwaitingSpawn = true };
        var ally = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1);

        var (next, _) = Resolve(TwoRooms([hilvan, ally]), (B, Move(Direction.North)));
        Assert.True(next.H(A).IsPresentRenegade);
        Assert.NotNull(next.H(A).SecondBody);

        var renegade = PresentRenegade(A, HeroClass.Hilvan, 2, 4) with { SecondBody = At(6, 4), WarnedHeroes = [B] };
        var hero = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1);
        var (tugged, _) = Resolve(State([renegade, hero]), (A, Skill(Direction.North)), (B, Interact(Direction.East)));
        Assert.Equal(5, tugged.H(B).Hp);
        Assert.Equal((HeroCatalog.For(HeroClass.Hilvan).SkillCooldown * 2) - 1, tugged.H(A).SkillCooldown);
    }

    [Fact]
    public void Monsters_go_for_the_nearest_body()
    {
        var scavenger = Enemy(1, EnemyKind.Scavenger, 6, 2);

        var (next, _) = Resolve(State([Hilvan(2, 4, 6, 4)], [scavenger]), (A, Order.DefaultGuard));

        Assert.Equal(At(6, 3), next.E(1)!.Position);
        Assert.Equal([At(6, 4)], next.E(1)!.Telegraph);
    }

    [Fact]
    public void Hilvan_sees_with_both_bodies_and_others_see_both()
    {
        var other = Hero(B, HeroClass.Mampostero, 0, 8, seat: 1);
        var state = State([Hilvan(0, 0, 8, 0), other], [Enemy(1, EnemyKind.Moth, 8, 2)]);

        Assert.Single(PlayerViewProjector.For(state, A).Enemies);
        var seen = state.Seen(B, A);
        Assert.Equal(At(0, 0), seen.Position);
        Assert.Equal(At(8, 0), seen.SecondBody);
    }
}
