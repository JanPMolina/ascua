using Ascua.Domain.Abstractions;
using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Domain.Tests.Support;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests;

public sealed class MatchFactoryTests
{
    private static readonly PlayerSetup[] ThreePlayers =
    [
        new(A, HeroClass.Mampostero),
        new(B, HeroClass.Usurera),
        new(C, HeroClass.Campanero),
    ];

    [Fact]
    public void Creates_four_rooms_and_the_warden_room_from_a_seed()
    {
        var (state, _) = MatchFactory.Create(ThreePlayers, new SeededRandom(42));

        Assert.Equal(MatchFactory.RoomCount, state.Rooms.Count);
        Assert.All(state.Rooms, room => Assert.True(RoomValidator.IsValid(room), $"Sala {room.Index} inválida"));
        Assert.All(state.Rooms.Take(4), room => Assert.Equal(Terrain.Stairs, room.TerrainAt(MatchFactory.StairsPosition)));
        Assert.DoesNotContain(state.Rooms[4].Features, f => f.Terrain == Terrain.Stairs);
        var warden = Assert.Single(state.Rooms[4].InitialEnemies);
        Assert.Equal(EnemyKind.Warden, warden.Kind);
        Assert.Equal(Difficulty.WardenHpPerPlayer(3) * 3, warden.Hp);
        Assert.Equal(state.Rooms[0].InitialEnemies, state.Enemies);
    }

    [Fact]
    public void Monsters_appear_near_the_stairs_and_away_from_the_entrance()
    {
        foreach (var seed in Enumerable.Range(1, 20))
        {
            var (state, _) = MatchFactory.Create(ThreePlayers, new SeededRandom((ulong)seed));
            var monsters = state.Rooms.Take(4).SelectMany(room => room.InitialEnemies).ToList();

            Assert.NotEmpty(monsters);
            Assert.All(monsters, e => Assert.True(e.Position.DistanceTo(MatchFactory.StairsPosition) <= MatchFactory.MonsterSpread, $"{e.Kind} en {e.Position}"));
            Assert.All(monsters, e => Assert.DoesNotContain(e.Position, MatchFactory.SpawnPoints));
        }
    }

    [Fact]
    public void With_two_players_each_room_brings_at_most_two_monsters()
    {
        var (duo, _) = MatchFactory.Create([new(A, HeroClass.Mampostero), new(B, HeroClass.Usurera)], new SeededRandom(42));
        var (trio, _) = MatchFactory.Create(ThreePlayers, new SeededRandom(42));

        // La primera sala es normal: el tipo al azar de las demás suma o quita monstruos (07-version-4.md §6).
        Assert.Equal(2, duo.Rooms[0].InitialEnemies.Count);
        Assert.Equal(3, trio.Rooms[0].InitialEnemies.Count);
        Assert.Equal(2, Difficulty.Monsters(1, 2).Count());
    }

    [Fact]
    public void The_miser_and_the_second_body_start_with_the_catalog_values()
    {
        var (state, _) = MatchFactory.Create([new(A, HeroClass.Avaro), new(B, HeroClass.Hilvan)], new SeededRandom(42));

        Assert.Equal(HeroRules.MiserStartEmbers, state.H(A).Embers);
        Assert.Equal(HeroCatalog.For(HeroClass.Hilvan).MaxHp, state.H(B).Hp);
    }

    [Fact]
    public void Every_bearer_gets_a_distinct_secret_pact_and_a_private_draft_of_two()
    {
        var (state, events) = MatchFactory.Create(ThreePlayers, new SeededRandom(7));

        Assert.Equal(MatchPhase.Drafting, state.Phase);
        Assert.Equal(3, state.Heroes.Select(h => h.Pact).Distinct().Count());
        Assert.All(state.Heroes, h => Assert.Equal(2, h.DraftOptions.Distinct().Count()));
        Assert.All(events.OfType<DraftOffered>(), e => Assert.Equal(Audience.Only(e.Player), e.Audience));
        Assert.Equal(MatchFactory.SpawnPoints.Take(3), state.Heroes.Select(h => h.Position));
    }

    [Fact]
    public void The_same_seed_produces_the_same_match()
    {
        var first = MatchFactory.Create(ThreePlayers, new SeededRandom(1234));
        var second = MatchFactory.Create(ThreePlayers, new SeededRandom(1234));
        var other = MatchFactory.Create(ThreePlayers, new SeededRandom(4321));

        Assert.Equal(Json.Of(first.State), Json.Of(second.State));
        Assert.Equal(Json.Of(first.Events), Json.Of(second.Events));
        Assert.NotEqual(Json.Of(first.State), Json.Of(other.State));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void Rejects_invalid_player_counts(int count)
    {
        var players = Enumerable.Range(0, count).Select(i => new PlayerSetup(new PlayerId($"p{i}"), HeroClass.Mampostero)).ToList();

        Assert.Throws<ArgumentException>(() => MatchFactory.Create(players, new SeededRandom(1)));
    }

    [Fact]
    public void Choosing_blessings_starts_the_match_once_everyone_has_chosen()
    {
        var (state, _) = MatchFactory.Create(ThreePlayers, new SeededRandom(9));
        var offered = state.H(A).DraftOptions[1];

        var afterA = MatchRules.ChooseBlessing(state, A, 1);
        Assert.True(afterA.IsSuccess);
        Assert.Equal(offered, Assert.Single(afterA.State.H(A).Blessings));
        Assert.Equal(MatchPhase.Drafting, afterA.State.Phase);
        Assert.Equal(AudienceKind.Player, afterA.Events.OfType<BlessingChosen>().Single().Audience.Kind);

        var rest = MatchRules.AutoChooseBlessings(afterA.State);
        Assert.Equal(MatchPhase.InProgress, rest.State.Phase);
        Assert.Contains(rest.Events, e => e is MatchStarted);
        Assert.Equal(rest.Events[^1].Seq, rest.State.LastSeq);
    }

    [Fact]
    public void Invalid_draft_choices_return_a_typed_error()
    {
        var (state, _) = MatchFactory.Create(ThreePlayers, new SeededRandom(9));

        var result = MatchRules.ChooseBlessing(state, A, 2);

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_DRAFT_CHOICE", result.Error!.Code);
        Assert.Same(state, result.State);
    }

    [Fact]
    public void Retiring_forfeits_embers_and_the_last_retirement_abandons_the_match()
    {
        var state = State([Hero(A, HeroClass.Mampostero, 3, 8, seat: 0) with { Embers = 4 }, Hero(B, HeroClass.Usurera, 5, 8, seat: 1)]);

        var first = MatchRules.Retire(state, A);
        Assert.Equal(HeroStatus.Retired, first.State.H(A).Status);
        Assert.Equal(0, first.State.H(A).Embers);
        Assert.Null(first.State.Outcome);

        var second = MatchRules.Retire(first.State, B);
        Assert.Equal(OutcomeKind.Abandoned, second.State.Outcome!.Kind);
        Assert.Equal(MatchPhase.Finished, second.State.Phase);
    }
}
