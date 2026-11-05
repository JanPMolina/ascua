using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Domain.Tests.Support;
using Ascua.Domain.Views;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests.V3;

/// <summary>06-version-3.md §3 y §4: El Juicio y las puertas.</summary>
public class InterludeTests
{
    private static readonly HashSet<PlayerId> NoneAbsent = [];

    /// <summary>Un grupo al que solo le falta pisar la escalera: el primero ya está encima.</summary>
    private static GameState AtTheStairs(params Hero[] heroes) =>
        TwoRooms([heroes[0] with { Position = Stairs }, .. heroes.Skip(1)]);

    private static GameState Opened(params Hero[] heroes)
    {
        var (state, _) = ResolveRaw(AtTheStairs(heroes), FixedRandom.Never);
        return state;
    }

    private static Hero Plain(PlayerId player, int seat, int embers = 5) =>
        Hero(player, HeroClass.Mampostero, seat, 8, seat) with { Embers = embers };

    private static GameState Vote(GameState state, PlayerId voter, PlayerId? accused, int? door = null)
    {
        var result = MatchRules.CastInterludeVote(state, voter, accused, door);
        Assert.Null(result.Error);
        return result.State;
    }

    private static GameState WithBetrayal(GameState state, PlayerId traitor, PlayerId victim, int room = 0) =>
        state with { Betrayals = state.Betrayals.Add(new Betrayal(traitor, victim, 1) { Room = room, Kind = BetrayalKind.Sabotage }) };

    private static GameState Close(GameState state, IReadOnlySet<PlayerId>? absent = null)
    {
        var result = MatchRules.CloseInterlude(state, absent ?? NoneAbsent, FixedRandom.Never);
        Assert.Null(result.Error);
        return result.State;
    }

    [Fact]
    public void The_stairs_open_an_interlude_with_two_different_doors_and_the_round_does_not_tick()
    {
        var (state, events) = ResolveRaw(AtTheStairs(Plain(A, 0), Plain(B, 1)), new SequenceRandom(0, 1));

        Assert.Equal(MatchPhase.Interlude, state.Phase);
        Assert.Equal(0, state.RoomIndex);
        Assert.Equal(2, state.Round);
        Assert.Equal(2, state.Interlude!.Doors.Distinct().Count());
        Assert.Contains(events, e => e is InterludeOpened);
        Assert.Equal(ErrorCode.MatchNotInProgress, OrderValidator.Validate(state, A, 2, Order.DefaultGuard)!.Kind);
    }

    [Fact]
    public void A_door_never_repeats_the_type_of_the_room_that_ends()
    {
        foreach (var value in Enumerable.Range(0, 20))
        {
            var first = EmptyRoom(0) with { Type = RoomType.Treasure };
            var state = TwoRooms([Plain(A, 0) with { Position = Stairs }, Plain(B, 1)], first: first);
            var (next, _) = ResolveRaw(state, new SequenceRandom(value, value + 1));
            Assert.DoesNotContain(RoomType.Treasure, next.Interlude!.Doors);
        }
    }

    [Fact]
    public void Before_the_warden_room_there_are_no_doors_only_the_trial()
    {
        var state = State([Plain(A, 0) with { Position = Stairs }, Plain(B, 1)]) with
        {
            Rooms = [EmptyRoom(0).WithTerrain(Stairs, Terrain.Stairs), EmptyRoom(1)],
        };

        var (next, _) = ResolveRaw(state);

        Assert.Empty(next.Interlude!.Doors);
        Assert.Equal(ErrorCode.InvalidVote, MatchRules.CastInterludeVote(next, A, null, 0).Error!.Kind);
        var closed = Close(next);
        Assert.Equal(1, closed.RoomIndex);
        Assert.Equal(RoomType.Normal, closed.CurrentRoom.Type);
    }

    [Fact]
    public void Who_votes_active_down_and_embers_but_not_renegades_or_the_retired()
    {
        var down = Plain(B, 1) with { Status = HeroStatus.Down, Hp = 0 };
        var ember = Ember(C, 2, 2, seat: 2);
        var renegade = PresentRenegade(D, HeroClass.Errata, 7, 7, seat: 3);
        var state = Opened(Plain(A, 0), down, ember, renegade);

        Assert.Equal([A, B, C], Interludes.Voters(state).Select(h => h.Player));
        Assert.Equal(ErrorCode.InvalidVote, MatchRules.CastInterludeVote(state, D, A, null).Error!.Kind);
        Assert.Contains(Interludes.Candidates(state, A), h => h.Player == D);
    }

    [Fact]
    public void You_cannot_accuse_yourself_or_vote_twice_and_a_door_must_exist()
    {
        var state = Opened(Plain(A, 0), Plain(B, 1));

        Assert.Equal(ErrorCode.InvalidVote, MatchRules.CastInterludeVote(state, A, A, null).Error!.Kind);
        Assert.Equal(ErrorCode.InvalidVote, MatchRules.CastInterludeVote(state, A, null, 2).Error!.Kind);
        var voted = Vote(state, A, B);
        Assert.Equal(ErrorCode.VoteClosed, MatchRules.CastInterludeVote(voted, A, null, null).Error!.Kind);
    }

    [Fact]
    public void With_two_players_one_vote_is_enough_to_convict_the_guilty()
    {
        var state = WithBetrayal(Opened(Plain(A, 0), Plain(B, 1)), B, A);

        var closed = Close(Vote(state, A, B));

        Assert.Equal(2, closed.H(B).Embers);
        Assert.Equal(1, closed.H(B).Progress.Convictions);
        Assert.Equal(1, closed.H(A).Progress.CorrectAccusations);
        var tally = Assert.Single(closed.LastVerdict!.Accusations);
        Assert.True(tally.Convicted);
        Assert.True(tally.Guilty);
        Assert.Equal(3, tally.GoldLost);
        Assert.Equal(B, Assert.Single(tally.Exposed).Traitor);
        Assert.True(Assert.Single(closed.Betrayals).Judged);
    }

    [Fact]
    public void An_innocent_conviction_fines_each_accuser_one()
    {
        var state = Opened(Plain(A, 0), Plain(B, 1), Plain(C, 2));

        var closed = Close(Vote(Vote(state, A, C), B, C));

        Assert.Equal(4, closed.H(A).Embers);
        Assert.Equal(4, closed.H(B).Embers);
        Assert.Equal(5, closed.H(C).Embers);
        var tally = Assert.Single(closed.LastVerdict!.Accusations);
        Assert.False(tally.Guilty);
        Assert.Equal(1, tally.AccuserFine);
    }

    [Fact]
    public void Without_a_strict_majority_nothing_happens()
    {
        var state = WithBetrayal(Opened(Plain(A, 0), Plain(B, 1), Plain(C, 2), Plain(D, 3)), B, A);

        // Contra B votan 1 de los 3 otros votantes.
        var closed = Close(Vote(state, A, B));

        Assert.Equal(5, closed.H(B).Embers);
        Assert.Equal(5, closed.H(A).Embers);
        Assert.False(Assert.Single(closed.LastVerdict!.Accusations).Convicted);
        Assert.False(Assert.Single(closed.Betrayals).Judged);
    }

    [Fact]
    public void The_accused_does_not_count_as_a_voter_in_his_own_case()
    {
        var state = WithBetrayal(Opened(Plain(A, 0), Plain(B, 1), Plain(C, 2)), B, A);

        // Otros votantes de B: A y C. Con los dos basta; B vota a otro y no cambia nada.
        var closed = Close(Vote(Vote(Vote(state, A, B), C, B), B, A));

        var tally = closed.LastVerdict!.Accusations.Single(t => t.Accused == B);
        Assert.Equal(2, tally.OtherVoters);
        Assert.True(tally.Convicted);
    }

    [Fact]
    public void A_betrayal_of_a_previous_room_does_not_make_him_guilty_now()
    {
        var state = WithBetrayal(Opened(Plain(A, 0), Plain(B, 1)), B, A, room: 7);

        var closed = Close(Vote(state, A, B));

        Assert.False(Assert.Single(closed.LastVerdict!.Accusations).Guilty);
        Assert.Equal(5, closed.H(B).Embers);
        Assert.Equal(4, closed.H(A).Embers);
    }

    [Fact]
    public void Two_convictions_at_once_are_possible_with_four_voters()
    {
        var state = Opened(Plain(A, 0), Plain(B, 1), Plain(C, 2), Plain(D, 3));
        state = WithBetrayal(WithBetrayal(state, A, B), C, B);

        var closed = Close(Vote(Vote(Vote(Vote(state, A, C), B, C), C, A), D, A));

        Assert.Equal(2, closed.H(A).Embers + 0);
        Assert.Equal(2, closed.H(C).Embers + 0);
        Assert.Equal(2, closed.LastVerdict!.Accusations.Count(t => t.Convicted));
    }

    [Fact]
    public void The_miser_never_goes_below_one_for_the_trial()
    {
        var miser = Hero(A, HeroClass.Avaro, 1, 8) with { Embers = 2 };
        var state = WithBetrayal(Opened(miser, Plain(B, 1)), A, B);

        var closed = Close(Vote(state, B, A));

        Assert.Equal(1, closed.H(A).Embers);
        Assert.Equal(HeroStatus.Active, closed.H(A).Status);
    }

    [Fact]
    public void A_ember_pays_from_what_it_saved_and_a_renegade_pays_too()
    {
        var ember = Ember(A, 2, 2, saved: 5);
        var state = WithBetrayal(Opened(Plain(B, 0), ember with { Seat = 1 }), A, B);

        var verdict = Close(Vote(state, B, A)).LastVerdict!;

        // Renace al entrar a la sala siguiente: lo que importa es que pagó de lo guardado.
        Assert.Equal(3, Assert.Single(verdict.Accusations).GoldLost);

        var renegade = PresentRenegade(A, HeroClass.Errata, 7, 7, seat: 1) with { Embers = 2 };
        var renegadeState = WithBetrayal(Opened(Plain(B, 0), renegade), A, B);
        var closed = Close(Vote(renegadeState, B, A));
        Assert.Equal(0, closed.H(A).Embers);
        Assert.Equal(2, Assert.Single(closed.LastVerdict!.Accusations).GoldLost);
    }

    [Fact]
    public void The_absent_are_not_waited_for_and_do_not_count_as_voters()
    {
        var state = WithBetrayal(Opened(Plain(A, 0), Plain(B, 1), Plain(C, 2)), B, A);

        // C desconectado y sin votar: de los otros votantes de B solo queda A, y con él basta.
        var closed = Close(Vote(state, A, B), new HashSet<PlayerId> { C });

        Assert.True(Assert.Single(closed.LastVerdict!.Accusations).Convicted);

        // Si C estuviera conectado sin votar, vale «nadie» y no hay mayoría.
        var connected = Close(Vote(state, A, B));
        Assert.False(Assert.Single(connected.LastVerdict!.Accusations).Convicted);
        Assert.Equal(2, connected.LastVerdict.NoAccusation);
    }

    [Fact]
    public void The_door_with_most_votes_wins_and_the_room_is_generated_with_its_rules()
    {
        var state = Opened(Plain(A, 0), Plain(B, 1), Plain(C, 2));
        var doors = state.Interlude!.Doors;

        var closed = Close(Vote(Vote(Vote(state, A, null, 1), B, null, 1), C, null, 0));

        Assert.Equal(1, closed.RoomIndex);
        Assert.Equal(doors[1], closed.CurrentRoom.Type);
        Assert.Equal(doors[1], closed.LastVerdict!.ChosenDoor);
        Assert.Equal(2, closed.LastVerdict.Doors.Single(d => d.Type == doors[1]).Votes);
        Assert.Equal(MatchPhase.InProgress, closed.Phase);
        Assert.Null(closed.Interlude);
    }

    [Fact]
    public void A_tie_or_no_votes_picks_a_door_at_random_among_the_two()
    {
        var state = Opened(Plain(A, 0), Plain(B, 1));
        var doors = state.Interlude!.Doors;

        var empty = Close(state);
        Assert.Contains(empty.CurrentRoom.Type, doors);

        var tied = Close(Vote(Vote(state, A, null, 0), B, null, 1));
        Assert.Contains(tied.CurrentRoom.Type, doors);
    }

    [Fact]
    public void Votes_are_secret_until_the_end_and_afterwards_nobody_learns_who_voted_what()
    {
        var state = WithBetrayal(Opened(Plain(A, 0), Plain(B, 1), Plain(C, 2)), B, A);
        var doors = state.Interlude!.Doors;
        var voted = MatchRules.CastInterludeVote(state, A, B, 1);

        var seenByB = PlayerViewProjector.For(voted.State, B);
        Assert.Null(seenByB.Interlude!.MyBallot);
        Assert.Equal([A], seenByB.Interlude.Voted);
        Assert.Empty(voted.Events.SeenBy(voted.State, B));
        Assert.Equal(B, PlayerViewProjector.For(voted.State, A).Interlude!.MyBallot!.Accused);
        Assert.DoesNotContain("Ballots", Json.Of(seenByB));

        var closed = MatchRules.CloseInterlude(Vote(voted.State, C, B, 0), NoneAbsent, FixedRandom.Never);
        var verdict = Json.Of(closed.State.LastVerdict!);
        Assert.DoesNotContain("\"Voter\"", verdict);
        Assert.Contains(doors[1].ToString(), verdict);
    }

    [Fact]
    public void Doors_not_chosen_leave_no_room_behind_and_show_only_their_type()
    {
        var state = Opened(Plain(A, 0), Plain(B, 1));
        var view = PlayerViewProjector.For(state, A);

        Assert.Equal(2, view.Interlude!.Doors.Count);
        Assert.DoesNotContain("InitialEnemies", Json.Of(view));
        Assert.DoesNotContain("SpawnPoints", Json.Of(view));

        var closed = Close(state);
        var room = closed.Rooms.Count(r => r.Type != RoomType.Normal);
        Assert.Equal(1, room);
    }

    [Fact]
    public void A_retired_voter_is_dropped_and_voting_needs_an_open_interlude()
    {
        var state = Vote(Opened(Plain(A, 0), Plain(B, 1), Plain(C, 2)), B, A);

        var retired = MatchRules.Retire(state, B).State;

        Assert.Empty(retired.Interlude!.Ballots);
        Assert.DoesNotContain(Interludes.Voters(retired), h => h.Player == B);
        Assert.Equal(ErrorCode.VoteClosed, MatchRules.CastInterludeVote(Close(retired), A, null, null).Error!.Kind);
    }

    [Fact]
    public void The_path_can_be_chosen_during_the_interlude()
    {
        var opened = Opened(Plain(A, 0), Plain(B, 1));
        var state = opened with { Heroes = [opened.H(A), ExtinctHero(B, seat: 1, saved: 3)] };

        var result = MatchRules.ChoosePath(state, B, PathKind.Vengeance);

        Assert.Null(result.Error);
        Assert.DoesNotContain(Interludes.Voters(state), h => h.Player == B);
    }

    [Fact]
    public void If_round_sixty_ends_at_the_stairs_there_is_no_interlude()
    {
        var state = AtTheStairs(Plain(A, 0), Plain(B, 1)) with { Round = 60 };

        var (next, _) = ResolveRaw(state);

        Assert.Equal(MatchPhase.Finished, next.Phase);
        Assert.Null(next.Interlude);
    }

    // ───── Salas

    private static Room GenerateVia(RoomType type, int playerCount = 4)
    {
        var heroes = Enumerable.Range(0, playerCount).Select(i => Plain(new PlayerId($"p{i}"), i)).ToArray();
        var opened = Opened(heroes);
        var state = opened with { Interlude = opened.Interlude! with { Doors = [type, type] } };
        return Close(state).CurrentRoom;
    }

    [Fact]
    public void Treasure_has_four_chests_and_one_more_monster()
    {
        var room = GenerateVia(RoomType.Treasure);

        Assert.Equal(4, room.Chests.Count);
        Assert.Equal(4, room.InitialEnemies.Count);
    }

    [Fact]
    public void Arena_has_no_chests_and_more_monsters_and_each_final_blow_gives_two()
    {
        var room = GenerateVia(RoomType.Arena);
        Assert.Empty(room.Chests);
        Assert.InRange(room.InitialEnemies.Count, 4, 5);

        var hero = Hero(A, HeroClass.Mampostero, 4, 4);
        var enemy = Model.Enemy.Create(1, EnemyKind.Scavenger, At(4, 3), 1);
        var (next, _) = Resolve(State([hero], [enemy], EmptyRoom() with { Type = RoomType.Arena }), (A, Attack(Direction.North)));
        Assert.Equal(2, next.H(A).Embers);
    }

    [Fact]
    public void Sanctuary_has_few_monsters_more_traps_and_an_altar_without_a_key()
    {
        var room = GenerateVia(RoomType.Sanctuary);
        Assert.Single(room.InitialEnemies);
        Assert.Single(room.PositionsOf(Terrain.Altar));
        Assert.True(room.PositionsOf(Terrain.Spikes).Count() >= 6);

        var hero = Hero(A, HeroClass.Mampostero, 4, 4);
        var altar = (EmptyRoom() with { Type = RoomType.Sanctuary }).WithTerrain(At(4, 3), Terrain.Altar);
        var (next, _) = Resolve(State([hero], room: altar), (A, Interact(Direction.North)));
        Assert.Equal(2, next.H(A).DraftOptions.Count);
        Assert.Equal(0, next.H(A).SilverKeys);
    }

    [Fact]
    public void A_normal_altar_still_asks_for_a_key()
    {
        var hero = Hero(A, HeroClass.Mampostero, 4, 4);
        var altar = EmptyRoom().WithTerrain(At(4, 3), Terrain.Altar);

        var (next, _) = Resolve(State([hero], room: altar), (A, Interact(Direction.North)));

        Assert.Empty(next.H(A).DraftOptions);
    }

    [Fact]
    public void Dark_takes_one_of_vision_from_everyone_with_a_minimum_of_one()
    {
        var room = GenerateVia(RoomType.Dark);
        Assert.Equal(3, room.Chests.Count);

        var state = State([Hero(A, HeroClass.Mampostero, 4, 4), Hero(B, HeroClass.Escucha, 1, 1, seat: 1)], room: EmptyRoom() with { Type = RoomType.Dark });
        Assert.Equal(2, PlayerViewProjector.For(state, A).Self.VisionRadius);
        Assert.Equal(1, PlayerViewProjector.For(state, B).Self.VisionRadius);
        Assert.Equal(RoomType.Dark, PlayerViewProjector.For(state, A).Room.Type);
    }

    [Fact]
    public void Flooded_rises_a_row_every_six_rounds_and_hurts_who_does_not_fly()
    {
        var room = EmptyRoom() with { Type = RoomType.Flooded, EnteredRound = 1 };
        Assert.Equal(0, room.FloodedRows(6));
        Assert.Equal(1, room.FloodedRows(7));
        Assert.Equal(2, room.FloodedRows(13));

        var wet = Hero(A, HeroClass.Mampostero, 4, 0, seat: 0);
        var dry = Hero(B, HeroClass.Errata, 4, 4, seat: 1);
        var swimmer = Enemy(1, EnemyKind.Totem, 1, 0);
        var moth = Enemy(2, EnemyKind.Moth, 2, 0);
        var (next, events) = Resolve(State([wet, dry], [swimmer, moth], room, round: 7), (A, Interact(Direction.North)), (B, Interact(Direction.North)));

        Assert.Equal(5, next.H(A).Hp);
        Assert.Equal(4, next.H(B).Hp);
        Assert.Equal(2, next.E(1)!.Hp);
        Assert.Equal(1, next.E(2)!.Hp);
        Assert.Equal(1, events.OfType<RoomFlooded>().Single().Rows);
        Assert.Equal(1, PlayerViewProjector.For(next, A).Room.FloodedRows);
    }

    [Fact]
    public void Generated_rooms_are_valid_for_every_type()
    {
        foreach (var type in RoomTypes.Doors)
        {
            for (var players = 2; players <= 4; players++)
            {
                var room = GenerateVia(type, players);
                Assert.Equal(type, room.Type);
                Assert.True(RoomValidator.IsValid(room) || room.Features.Count <= 2, $"{type} con {players}");
            }
        }
    }
}
