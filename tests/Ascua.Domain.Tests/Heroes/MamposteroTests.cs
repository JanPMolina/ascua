using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Domain.Tests.Support;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests.Heroes;

/// <summary>El Mampostero (05-heroes.md §11): «La mazmorra está quieta».</summary>
public sealed class MamposteroTests
{
    private static Hero Mason(int x, int y, int seat = 0) => Hero(A, HeroClass.Mampostero, x, y, seat);

    [Fact]
    public void Spikes_and_cracks_do_not_affect_him()
    {
        var spikes = EmptyRoom().WithTerrain(At(4, 4), Terrain.Spikes);
        var (spiked, _) = Resolve(State([Mason(4, 4)], room: spikes, round: 2), (A, Interact(Direction.East)));
        Assert.Equal(6, spiked.H(A).Hp);

        var crack = EmptyRoom().WithTerrain(At(4, 3), Terrain.Crack);
        var (walked, _) = Resolve(State([Mason(4, 4)], room: crack), (A, Move(Direction.North)));
        Assert.Equal(At(4, 3), walked.H(A).Position);
        Assert.True(walked.H(A).IsActive);
    }

    [Fact]
    public void Shift_slides_his_row_with_everything_on_it_and_spends_the_cooldown()
    {
        var room = EmptyRoom().WithTerrain(At(2, 4), Terrain.Wall).WithTerrain(At(6, 4), Terrain.Spikes) with
        {
            Chests = [new Chest(1, At(1, 4), ChestLoot.Nothing, HasSilverKey: false)],
            Fires = [new Fire(At(5, 4), 2, 1)],
            Ground = [new GroundEmbers(At(0, 4), 2)],
        };
        var ally = Hero(B, HeroClass.Campanero, 3, 4, seat: 1);
        var fallen = Hero(C, HeroClass.Campanero, 7, 4, seat: 2) with { Hp = 0, Status = HeroStatus.Down };
        var enemy = Enemy(1, EnemyKind.Totem, 8, 3);
        var onRow = Enemy(2, EnemyKind.Totem, 4, 4) with { Position = At(5, 4) };

        var state = State([Mason(4, 4), ally, fallen], [enemy, onRow], room);
        var (next, events) = Resolve(state, (A, Skill(Direction.East)), (B, Interact(Direction.North)));

        Assert.Equal(At(5, 4), next.H(A).Position);
        Assert.Equal(At(4, 4), next.H(B).Position);
        Assert.Equal(At(8, 4), next.H(C).Position);
        Assert.Equal(At(6, 4), next.E(2)!.Position);
        Assert.Equal(At(8, 3), next.E(1)!.Position);
        Assert.Equal(Terrain.Wall, next.CurrentRoom.TerrainAt(At(3, 4)));
        Assert.Equal(Terrain.Spikes, next.CurrentRoom.TerrainAt(At(7, 4)));
        Assert.Equal(Terrain.Floor, next.CurrentRoom.TerrainAt(At(2, 4)));
        Assert.Equal(At(2, 4), next.CurrentRoom.Chests[0].Position);
        Assert.Equal(At(1, 4), next.CurrentRoom.Ground[0].Position);
        Assert.Contains(events, e => e is RoomShifted { Axis: ShiftAxis.Row, Line: 4, Direction: Direction.East });
        Assert.Equal(HeroCatalog.For(HeroClass.Mampostero).SkillCooldown - 1, next.H(A).SkillCooldown);
    }

    [Fact]
    public void Shift_north_or_south_slides_his_column()
    {
        var other = Hero(B, HeroClass.Campanero, 4, 2, seat: 1);

        var (next, events) = Resolve(State([Mason(4, 4), other]), (A, Skill(Direction.South)), (B, Interact(Direction.East)));

        Assert.Equal(At(4, 5), next.H(A).Position);
        Assert.Equal(At(4, 3), next.H(B).Position);
        Assert.Contains(events, e => e is RoomShifted { Axis: ShiftAxis.Column, Line: 4 });
    }

    [Fact]
    public void Whoever_leaves_by_one_edge_enters_by_the_other_and_takes_two()
    {
        var totem = Enemy(1, EnemyKind.Totem, 8, 4);

        var (next, _) = Resolve(State([Mason(4, 4)], [totem]), (A, Skill(Direction.East)));

        Assert.Equal(At(0, 4), next.E(1)!.Position);
        Assert.Equal(1, next.E(1)!.Hp);
    }

    [Fact]
    public void A_monster_killed_by_the_edge_is_his_final_blow()
    {
        var moth = Enemy(1, EnemyKind.Moth, 8, 4);

        var (next, events) = Resolve(State([Mason(4, 4)], [moth]), (A, Skill(Direction.East)));

        Assert.Null(next.E(1));
        Assert.Equal(1, next.H(A).Embers);
        Assert.Equal(A, events.OfType<EnemyKilled>().Single().Killer);
    }

    [Fact]
    public void Damage_to_allies_by_the_edge_is_normal_friendly_fire()
    {
        var ally = Hero(B, HeroClass.Campanero, 8, 4, seat: 1) with { Embers = 2 };

        var (next, _) = Resolve(State([Mason(4, 4), ally]), (A, Skill(Direction.East)), (B, Interact(Direction.North)));

        Assert.Equal(At(0, 4), next.H(B).Position);
        Assert.Equal(4, next.H(B).Hp);
        Assert.Equal(1, next.H(B).Embers);
        Assert.Equal(1, next.H(A).Embers);
    }

    [Fact]
    public void Announcements_do_not_move_someone_can_be_taken_out_of_or_into_the_blow()
    {
        var striker = Enemy(1, EnemyKind.Scavenger, 0, 0) with { Telegraph = [At(5, 4)] };
        var ally = Hero(B, HeroClass.Campanero, 5, 4, seat: 1);

        var (next, _) = Resolve(State([Mason(4, 4), ally], [striker]), (A, Skill(Direction.East)), (B, Interact(Direction.North)));

        // El aliado salió de la casilla y el Mampostero se metió en ella.
        Assert.Equal(5, next.H(B).Hp);
        Assert.Equal(5, next.H(A).Hp);
    }

    [Fact]
    public void Movement_orders_resolve_afterwards_someone_can_end_up_where_they_did_not_want()
    {
        var ally = Hero(B, HeroClass.Campanero, 5, 4, seat: 1);

        var (next, _) = Resolve(State([Mason(4, 8), ally]), (A, Skill(Direction.North)), (B, Move(Direction.North)));

        Assert.Equal(At(5, 3), next.H(B).Position);
    }

    [Fact]
    public void Everybody_sees_the_shift_even_from_afar()
    {
        var far = Hero(B, HeroClass.Campanero, 0, 0, seat: 1);

        var (next, events) = Resolve(State([Mason(8, 8), far]), (A, Skill(Direction.West)), (B, Interact(Direction.East)));

        Assert.Contains(events.SeenBy(next, B), e => e is RoomShifted);
    }

    [Fact]
    public void An_ember_is_intangible_and_does_not_move()
    {
        var ember = Ember(B, 5, 4, seat: 1);

        var (next, _) = Resolve(State([Mason(4, 4), ember]), (A, Skill(Direction.East)), (B, Order.DefaultGuard));

        Assert.Equal(At(5, 4), next.H(B).Position);
    }

    [Fact]
    public void If_the_stairs_would_be_cut_off_it_does_not_happen_and_keeps_the_cooldown()
    {
        // La escalera solo se alcanza desde el sur; al correr esa fila, el muro de su izquierda la tapa.
        var south = Stairs.Step(Direction.South);
        var room = EmptyRoom()
            .WithTerrain(Stairs.Step(Direction.North), Terrain.Wall)
            .WithTerrain(Stairs.Step(Direction.West), Terrain.Wall)
            .WithTerrain(Stairs.Step(Direction.East), Terrain.Wall)
            .WithTerrain(south.Step(Direction.West), Terrain.Wall)
            .WithTerrain(south.Step(Direction.East), Terrain.Wall);

        var (next, events) = Resolve(TwoRooms([Mason(0, south.Y)], first: room), (A, Skill(Direction.East)));

        Assert.Equal(At(0, south.Y), next.H(A).Position);
        Assert.Equal(0, next.H(A).SkillCooldown);
        Assert.DoesNotContain(events, e => e is RoomShifted);
    }

    [Fact]
    public void If_an_active_hero_would_be_cut_off_it_does_not_happen()
    {
        // La columna 5 separa la sala y solo tiene un hueco en (5,4), al que se llega por (4,4). Al correr la fila 4,
        // el muro de (3,4) pasa a (4,4) y el aliado de la derecha queda encerrado.
        var room = EmptyRoom().WithTerrain(At(3, 4), Terrain.Wall);
        for (var y = 0; y < Room.Size; y++)
        {
            room = y == 4 ? room : room.WithTerrain(At(5, y), Terrain.Wall);
        }

        var ally = Hero(B, HeroClass.Campanero, 8, 2, seat: 1);

        var (next, _) = Resolve(State([Mason(0, 4), ally], room: room), (A, Skill(Direction.East)), (B, Interact(Direction.North)));

        Assert.Equal(At(0, 4), next.H(A).Position);
        Assert.Equal(0, next.H(A).SkillCooldown);
    }

    [Fact]
    public void The_stairs_slide_too_and_still_work()
    {
        var hero = Mason(Stairs.X, Stairs.Y);

        var (next, _) = Resolve(TwoRooms([hero, Hero(B, HeroClass.Campanero, 0, 8, seat: 1)]), (A, Skill(Direction.East)), (B, Interact(Direction.East)));

        // Escalera y Mampostero se movieron juntos: sigue encima y la sala está despejada.
        Assert.Equal(1, next.RoomIndex);
        Assert.Equal(Terrain.Stairs, next.Rooms[0].TerrainAt(Stairs.Step(Direction.East)));
    }

    [Fact]
    public void As_a_renegade_edge_damage_is_at_most_one_monsters_are_spared_and_it_costs_double()
    {
        var mason = PresentRenegade(A, HeroClass.Mampostero, 4, 4) with { WarnedHeroes = [B] };
        var hero = Hero(B, HeroClass.Campanero, 8, 4, seat: 1);
        var totem = Enemy(1, EnemyKind.Totem, 0, 4) with { };

        var (next, _) = Resolve(State([mason, hero], [totem]), (A, Skill(Direction.East)), (B, Interact(Direction.North)));
        Assert.Equal(4, next.H(B).Hp);
        Assert.Equal((HeroCatalog.For(HeroClass.Mampostero).SkillCooldown * 2) - 1, next.H(A).SkillCooldown);

        var moth = Enemy(1, EnemyKind.Moth, 8, 4);
        var (spared, _) = Resolve(State([mason, hero with { Position = At(0, 0) }], [moth]), (A, Skill(Direction.East)), (B, Interact(Direction.North)));
        Assert.NotNull(spared.E(1));
    }

    [Fact]
    public void As_an_ember_he_has_no_shift()
    {
        var ember = Ember(A, 4, 4, heroClass: HeroClass.Mampostero);
        var other = Hero(B, HeroClass.Campanero, 5, 4, seat: 1) with { Hp = 2 };

        var (next, events) = Resolve(State([ember, other]), (A, Skill(Direction.East)), (B, Interact(Direction.North)));

        Assert.DoesNotContain(events, e => e is RoomShifted);
        Assert.Equal(At(5, 4), next.H(B).Position);
    }

    [Fact]
    public void Shifting_a_stowaways_host_carries_him_along()
    {
        var host = Enemy(1, EnemyKind.Scavenger, 6, 4);
        var polizon = Hero(B, HeroClass.Polizon, 6, 4, seat: 1) with { HostEnemyId = 1, BoardedRound = 1 };

        var (next, _) = Resolve(State([Mason(4, 4), polizon], [host]), (A, Skill(Direction.East)), (B, Order.DefaultGuard));

        Assert.Equal(At(7, 4), next.E(1)!.Position);
        Assert.Equal(At(7, 4), next.H(B).Position);
        Assert.True(next.H(B).IsInHost);
    }
}
