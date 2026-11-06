using Ascua.Domain.Abstractions;
using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Domain.Tests.Support;
using Ascua.Domain.Views;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests.V4;

/// <summary>planteamiento/07-version-4.md.</summary>
public class VersionFourTests
{
    private static readonly HeroClass[] Five = [HeroClass.Mampostero, HeroClass.Errata, HeroClass.Hilvan, HeroClass.Avaro, HeroClass.Campanero];

    private static List<PlayerSetup> Setups(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new PlayerSetup(new PlayerId($"p{i}"), Five[i]))];

    // ───── §1 y §2: hasta 5 jugadores y sala de 11

    [Theory]
    [InlineData(2, 9)]
    [InlineData(3, 9)]
    [InlineData(4, 9)]
    [InlineData(5, 11)]
    public void The_room_is_9_with_up_to_4_players_and_11_with_5(int players, int size)
    {
        var (state, _) = MatchFactory.Create(Setups(players), new SeededRandom(7));

        Assert.All(state.Rooms, room => Assert.Equal(size, room.Size));
        Assert.All(state.Rooms, room => Assert.True(RoomValidator.IsValid(room), $"Sala {room.Index} inválida"));
        var stairs = MatchFactory.StairsFor(size);
        Assert.All(state.Rooms.Take(4), room => Assert.Equal(Terrain.Stairs, room.TerrainAt(stairs)));
        Assert.Equal(MatchFactory.WardenFor(size), Assert.Single(state.Rooms[4].InitialEnemies).Position);
        Assert.All(state.Heroes, h => Assert.Equal(size - 2, h.Position.Y));
        Assert.Equal(players, state.Heroes.Select(h => h.Position).Distinct().Count());
        Assert.All(state.Rooms.SelectMany(r => r.InitialEnemies), e => Assert.True(state.Rooms[0].IsInside(e.Position)));
    }

    [Fact]
    public void Six_players_are_rejected()
    {
        var six = Setups(5).Append(new PlayerSetup(new PlayerId("p5"), HeroClass.Usurera)).ToList();

        Assert.Throws<ArgumentException>(() => MatchFactory.Create(six, new SeededRandom(1)));
    }

    [Fact]
    public void The_size_travels_in_the_view_and_the_whole_room_is_known_to_the_escucha()
    {
        var setups = Setups(5);
        setups[0] = new PlayerSetup(setups[0].Player, HeroClass.Escucha);
        var (state, _) = MatchFactory.Create(setups, new SeededRandom(3));

        var view = PlayerViewProjector.For(state, setups[0].Player);

        Assert.Equal(11, view.Room.Size);
        Assert.Equal(121, view.Tiles.Count);
    }

    [Fact]
    public void Hilvan_starts_with_the_second_body_inside_an_11_room()
    {
        var setups = Setups(5);
        var (state, _) = MatchFactory.Create(setups, new SeededRandom(5));
        var hilvan = state.Heroes.Single(h => h.Class == HeroClass.Hilvan);

        Assert.NotNull(hilvan.SecondBody);
        Assert.True(state.Rooms[0].IsInside(hilvan.SecondBody!.Value));
        Assert.DoesNotContain(hilvan.SecondBody.Value, state.Heroes.Where(h => h != hilvan).Select(h => h.Position));
    }

    [Fact]
    public void The_mason_shift_wraps_around_an_11_room()
    {
        var room = new Room { Index = 0, Size = 11, SpawnPoints = MatchFactory.SpawnPointsFor(11) };
        var mason = Hero(A, HeroClass.Mampostero, 0, 5);
        var enemy = Enemy(1, EnemyKind.Totem, 10, 5) with { Hp = 9, MaxHp = 9 };

        var (next, _) = Resolve(State([mason], [enemy], room), (A, Skill(Direction.East)));

        // El tótem de la columna 10 sale por el borde este y entra por la columna 0.
        Assert.Equal(At(0, 5), next.E(1)!.Position);
        Assert.Equal(At(1, 5), next.H(A).Position);
    }

    // ───── §3: dificultad

    [Fact]
    public void The_warden_and_the_rooms_scale_with_the_players()
    {
        var hp = Enumerable.Range(2, 4).Select(n => EnemyCatalog.WardenHp(n, [])).ToList();
        Assert.Equal(hp.Order(), hp);
        Assert.True(Difficulty.Monsters(0, 2).Count() < Difficulty.Monsters(0, 3).Count());
        Assert.True(Difficulty.Monsters(0, 5).Count() > Difficulty.Monsters(0, 4).Count());
        Assert.Equal([EnemyKind.Warden], Difficulty.Monsters(4, 5));
        Assert.True(Difficulty.TotemMaxSummons(5) >= Difficulty.TotemMaxSummons(4));
    }

    // ───── §6: escalera directa, tipo de sala al azar, sin votos

    [Fact]
    public void The_stairs_lead_straight_to_the_next_room_and_its_type_is_announced()
    {
        var first = EmptyRoom().WithTerrain(Stairs, Terrain.Stairs);
        var second = EmptyRoom(1) with { Type = RoomType.Dark };
        var state = TwoRooms([Hero(A, HeroClass.Mampostero, Stairs.X, Stairs.Y + 1)], first: first, second: second);

        var (next, events) = Resolve(state, FixedRandom.Never, (A, Move(Direction.North)));

        Assert.Equal(MatchPhase.InProgress, next.Phase);
        Assert.Equal(1, next.RoomIndex);
        Assert.Equal(2, next.Round);
        Assert.Contains(events, e => e is RoomEntered { RoomIndex: 1, Type: RoomType.Dark });
        Assert.Equal(RoomType.Dark, PlayerViewProjector.For(next, A).Room.Type);
    }

    [Fact]
    public void Middle_rooms_get_a_random_type_never_equal_to_the_previous_one()
    {
        var seen = new HashSet<RoomType>();
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var (state, _) = MatchFactory.Create(Setups(3), new SeededRandom(seed));
            Assert.Equal(RoomType.Normal, state.Rooms[0].Type);
            Assert.Equal(RoomType.Normal, state.Rooms[4].Type);
            for (var i = 1; i < 4; i++)
            {
                Assert.Contains(state.Rooms[i].Type, RoomTypes.Random);
                Assert.NotEqual(state.Rooms[i - 1].Type, state.Rooms[i].Type);
                seen.Add(state.Rooms[i].Type);
            }
        }

        Assert.Equal(RoomTypes.Random.Count, seen.Count);
    }

    [Fact]
    public void The_view_never_shows_the_type_of_the_next_room()
    {
        var (state, _) = MatchFactory.Create(Setups(2), new SeededRandom(11));

        var view = PlayerViewProjector.For(state, state.Heroes[0].Player);

        Assert.Equal(RoomType.Normal, view.Room.Type);
        Assert.Equal(0, view.RoomIndex);
    }

    [Fact]
    public void Only_five_missions_are_offered_and_the_two_options_differ()
    {
        Assert.Equal(5, PactRules.Offered.Count);
        Assert.DoesNotContain(PactKind.BetrayTwiceUnconvicted, PactRules.Offered);
        Assert.DoesNotContain(PactKind.CorrectConviction, PactRules.Offered);

        var (state, _) = MatchFactory.Create(Setups(5), new SeededRandom(9));
        Assert.All(state.Heroes, h => Assert.NotEqual(h.PactOptions[0].Kind, h.PactOptions[1].Kind));
    }

    // ───── §7: sin límite de rondas y el frío

    [Theory]
    [InlineData(79, 0)]
    [InlineData(80, 0)]
    [InlineData(82, 0)]
    [InlineData(81, 1)]
    [InlineData(93, 1)]
    [InlineData(96, 2)]
    [InlineData(111, 3)]
    public void The_chill_starts_at_round_80_every_3_rounds_and_grows(int round, int damage) =>
        Assert.Equal(damage, GameState.ChillDamage(round));

    [Fact]
    public void The_chill_ignores_guard_and_shield_and_hits_renegades_too()
    {
        var a = Hero(A, HeroClass.Mampostero, 1, 1) with { Shield = 2 };
        var renegade = PresentRenegade(B, HeroClass.Errata, 6, 6, seat: 1);

        var (next, events) = Resolve(State([a, renegade], round: 81), FixedRandom.Never, (A, Order.DefaultGuard));

        Assert.Contains(events, e => e is Chilled { Damage: 1 });
        Assert.Equal(a.Hp - 1, next.H(A).Hp);
        Assert.Equal(2, next.H(A).Shield);
        Assert.Equal(renegade.Hp - 1, next.H(B).Hp);
    }

    [Fact]
    public void The_gold_the_chill_takes_from_the_miser_is_lost_not_dropped()
    {
        var miser = Hero(A, HeroClass.Avaro, 3, 3) with { Embers = 5 };

        var (next, _) = Resolve(State([miser], round: 81), FixedRandom.Never, (A, Order.DefaultGuard));

        Assert.Equal(4, next.H(A).Embers);
        Assert.Empty(next.CurrentRoom.Ground);
    }

    [Fact]
    public void Without_any_active_hero_left_after_the_chill_the_match_ends()
    {
        var a = Hero(A, HeroClass.Mampostero, 1, 1) with { Hp = 1 };

        var (next, _) = Resolve(State([a], [Enemy(1, EnemyKind.Totem, 7, 7)], round: 81), FixedRandom.Never, (A, Order.DefaultGuard));

        Assert.Equal(MatchPhase.Finished, next.Phase);
        Assert.Equal(OutcomeKind.CollectiveDefeat, next.Outcome!.Kind);
    }
}
