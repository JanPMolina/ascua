using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Tests.Support;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests;

public sealed class MovementTests
{
    private static GameState TwoHeroes(int ax, int ay, int bx, int by, Room? room = null) =>
        State([Hero(A, HeroClass.Mampostero, ax, ay, seat: 0), Hero(B, HeroClass.Usurera, bx, by, seat: 1)], room: room);

    [Fact]
    public void Two_heroes_moving_to_the_same_tile_both_bounce()
    {
        var (next, events) = Resolve(TwoHeroes(3, 4, 5, 4), (A, Move(Direction.East)), (B, Move(Direction.West)));

        Assert.Equal(new Position(3, 4), next.H(A).Position);
        Assert.Equal(new Position(5, 4), next.H(B).Position);
        Assert.Equal(2, events.OfType<MoveBounced>().Count());
    }

    [Fact]
    public void Cannot_enter_a_tile_occupied_by_someone_who_stays()
    {
        var (next, _) = Resolve(TwoHeroes(3, 4, 4, 4), (A, Move(Direction.East)));

        Assert.Equal(new Position(3, 4), next.H(A).Position);
        Assert.Equal(Direction.East, next.H(A).Facing);
    }

    [Fact]
    public void Can_follow_into_a_tile_that_is_being_vacated()
    {
        var (next, _) = Resolve(TwoHeroes(3, 4, 4, 4), (A, Move(Direction.East)), (B, Move(Direction.East)));

        Assert.Equal(new Position(4, 4), next.H(A).Position);
        Assert.Equal(new Position(5, 4), next.H(B).Position);
    }

    [Fact]
    public void Heroes_cannot_swap_places()
    {
        var (next, _) = Resolve(TwoHeroes(3, 4, 4, 4), (A, Move(Direction.East)), (B, Move(Direction.West)));

        Assert.Equal(new Position(3, 4), next.H(A).Position);
        Assert.Equal(new Position(4, 4), next.H(B).Position);
    }

    [Fact]
    public void A_bounce_propagates_to_whoever_was_following()
    {
        var mampostero = Hero(A, HeroClass.Mampostero, 2, 4, seat: 0);
        var usurera = Hero(B, HeroClass.Usurera, 3, 4, seat: 1);
        var campanero = Hero(C, HeroClass.Campanero, 5, 4, seat: 2);

        // B y C chocan en (4,4); A iba a ocupar el sitio de B, que no se vacía.
        var (next, _) = Resolve(
            State([mampostero, usurera, campanero]),
            (A, Move(Direction.East)),
            (B, Move(Direction.East)),
            (C, Move(Direction.West)));

        Assert.Equal(new Position(2, 4), next.H(A).Position);
        Assert.Equal(new Position(3, 4), next.H(B).Position);
        Assert.Equal(new Position(5, 4), next.H(C).Position);
    }

    [Fact]
    public void Walls_and_room_edges_stop_movement()
    {
        var room = EmptyRoom().WithTerrain(new Position(4, 3), Terrain.Wall);

        var (next, _) = Resolve(TwoHeroes(4, 4, 0, 0, room), (A, Move(Direction.North)), (B, Move(Direction.West)));

        Assert.Equal(new Position(4, 4), next.H(A).Position);
        Assert.Equal(new Position(0, 0), next.H(B).Position);
    }

    [Fact]
    public void Enemies_block_movement()
    {
        var state = State([Hero(A, HeroClass.Mampostero, 4, 4)], [Enemy(1, EnemyKind.Totem, 4, 3)]);

        var (next, _) = Resolve(state, (A, Move(Direction.North)));

        Assert.Equal(new Position(4, 4), next.H(A).Position);
    }

    [Fact]
    public void Falling_into_a_crack_extinguishes_the_hero_and_saves_its_embers()
    {
        var room = EmptyRoom().WithTerrain(new Position(4, 3), Terrain.Crack);
        var state = State([Hero(A, HeroClass.Errata, 4, 4, seat: 0) with { Embers = 5 }, Hero(B, HeroClass.Usurera, 0, 0, seat: 1)], room: room);

        var (next, events) = Resolve(state, (A, Move(Direction.North)));

        Assert.Equal(HeroStatus.Extinct, next.H(A).Status);
        Assert.Equal(0, next.H(A).Embers);
        Assert.Equal(5, next.H(A).SavedEmbers);
        Assert.True(next.H(A).IsChoosingPath);
        Assert.Contains(events, e => e is HeroExtinguished { Reason: ExtinctionReason.Chasm, EmbersLost: 0 });
        Assert.Contains(events, e => e is PathOffered { Extinctions: 1, SavedEmbers: 5 });
    }
}
