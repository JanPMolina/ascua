using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Domain.Tests.Support;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests;

public sealed class FallAndEndTests
{
    private static Enemy StrikingAt(int x, int y) =>
        Enemy(1, EnemyKind.Scavenger, x, y - 1) with { Telegraph = [new Position(x, y)] };

    [Fact]
    public void A_hero_at_zero_life_falls()
    {
        var errata = Hero(A, HeroClass.Errata, 4, 5, seat: 0) with { Hp = 1 };
        var ally = Hero(B, HeroClass.Mampostero, 0, 8, seat: 1);

        var (next, events) = Resolve(State([errata, ally], [StrikingAt(4, 5)]), (A, Interact(Direction.East)));

        Assert.Equal(HeroStatus.Down, next.H(A).Status);
        Assert.Equal(0, next.H(A).Hp);
        Assert.True(next.H(A).Progress.HasFallen);
        Assert.Contains(events, e => e is HeroDowned { Player.Value: "alice" });
    }

    [Fact]
    public void Last_breath_saves_the_hero_once()
    {
        var errata = Hero(A, HeroClass.Errata, 4, 5, seat: 0) with { Hp = 1, Blessings = [Blessing.LastBreath] };
        var ally = Hero(B, HeroClass.Mampostero, 0, 8, seat: 1);
        var state = State([errata, ally], [StrikingAt(4, 5)]);

        var (saved, _) = Resolve(state, (A, Interact(Direction.East)));
        Assert.Equal(1, saved.H(A).Hp);
        Assert.True(saved.H(A).LastBreathUsed);

        var again = saved with { Enemies = [StrikingAt(4, 5)] };
        var (fallen, _) = Resolve(again, (A, Interact(Direction.East)));
        Assert.Equal(HeroStatus.Down, fallen.H(A).Status);
    }

    [Fact]
    public void An_adjacent_ally_revives_with_two_life_and_earns_two_embers()
    {
        var fallen = Hero(A, HeroClass.Usurera, 4, 4, seat: 0) with { Hp = 0, Status = HeroStatus.Down, DownRounds = 1 };
        var rescuer = Hero(B, HeroClass.Campanero, 4, 5, seat: 1);

        var (next, events) = Resolve(State([fallen, rescuer]), (B, Interact(Direction.North)));

        Assert.Equal(HeroStatus.Active, next.H(A).Status);
        Assert.Equal(HeroCatalog.ReviveHp, next.H(A).Hp);
        Assert.Equal(2, next.H(B).Embers);
        Assert.Equal(1, next.H(B).Progress.Revives);
        Assert.Contains(events, e => e is HeroRevived);
    }

    [Fact]
    public void Three_rounds_down_without_help_extinguishes_and_opens_the_path_choice()
    {
        var fallen = Hero(A, HeroClass.Usurera, 4, 4, seat: 0) with { Hp = 0, Status = HeroStatus.Down, Embers = 5 };
        var bystander = Hero(B, HeroClass.Campanero, 0, 8, seat: 1);
        var state = State([fallen, bystander]);

        for (var round = 1; round <= 2; round++)
        {
            state = Resolve(state, FixedRandom.Never).State;
            Assert.Equal(HeroStatus.Down, state.H(A).Status);
            Assert.Equal(round, state.H(A).DownRounds);
        }

        var (next, events) = Resolve(state, FixedRandom.Never);

        Assert.Equal(HeroStatus.Extinct, next.H(A).Status);
        Assert.Equal(0, next.H(A).Embers);
        Assert.Equal(5, next.H(A).SavedEmbers);
        Assert.True(next.H(A).IsChoosingPath);
        Assert.Contains(events, e => e is HeroExtinguished { Reason: ExtinctionReason.NotRevived, EmbersLost: 0 });
        var offer = Assert.Single(events.OfType<PathOffered>());
        Assert.Equal(AudienceKind.Player, offer.Audience.Kind);
    }

    [Fact]
    public void Downed_heroes_cannot_submit_orders()
    {
        var fallen = Hero(A, HeroClass.Usurera, 4, 4) with { Hp = 0, Status = HeroStatus.Down };
        var extinct = Hero(B, HeroClass.Campanero, 0, 8, seat: 1) with { Status = HeroStatus.Extinct };
        var state = State([fallen, extinct, Hero(C, HeroClass.Mampostero, 8, 8, seat: 2)]);

        Assert.Equal("PLAYER_DOWN", OrderValidator.Validate(state, A, 1, Order.DefaultGuard)?.Code);
        Assert.Equal("PLAYER_OUT", OrderValidator.Validate(state, B, 1, Order.DefaultGuard)?.Code);
    }

    [Fact]
    public void Friendly_fire_deals_half_damage_and_steals_an_ember()
    {
        var mampostero = Hero(A, HeroClass.Mampostero, 4, 4, seat: 0) with { Blessings = [Blessing.StoneFist] };
        var errata = Hero(B, HeroClass.Errata, 5, 4, seat: 1, facing: Direction.East) with { Embers = 2 };

        var (next, _) = Resolve(State([mampostero, errata]), (A, Attack(Direction.East)), (B, Interact(Direction.East)));

        Assert.Equal(3, next.H(B).Hp);
        Assert.Equal(1, next.H(B).Embers);
        Assert.Equal(1, next.H(A).Embers);
        Assert.Equal(0, next.H(A).Progress.Betrayals);
    }

    [Fact]
    public void Slaying_the_warden_gives_victory_to_the_richest_bearer()
    {
        var (next, events) = Resolve(BossFight(aEmbers: 0, bEmbers: 3), (A, Attack(Direction.North)));

        Assert.Equal(MatchPhase.Finished, next.Phase);
        Assert.Equal(OutcomeKind.Victory, next.Outcome!.Kind);
        Assert.Equal(A, Assert.Single(next.Outcome.Winners));
        Assert.Equal(7, next.H(A).Embers);
        Assert.Contains(events, e => e is MatchEnded);
        Assert.Equal(2, events.OfType<PactRevealed>().Count());
    }

    [Fact]
    public void A_tie_at_the_top_is_a_draw()
    {
        var (next, _) = Resolve(BossFight(aEmbers: 0, bEmbers: 7), (A, Attack(Direction.North)));

        Assert.Equal(OutcomeKind.Draw, next.Outcome!.Kind);
        Assert.Equal(2, next.Outcome.Winners.Count);
    }

    [Fact]
    public void A_fulfilled_mission_adds_three_embers_at_the_end()
    {
        var state = BossFight(aEmbers: 0, bEmbers: 9);
        state = state with { Heroes = [state.H(A) with { Pact = PactKind.MostWardenDamage }, state.H(B)] };

        var (next, _) = Resolve(state, (A, Attack(Direction.North)));

        Assert.Equal(A, Assert.Single(next.Outcome!.Winners));
        var score = next.Outcome.Scores.Single(s => s.Player == A);
        Assert.True(score.PactFulfilled);
        Assert.Equal(10, score.Total);
    }

    [Fact]
    public void Extinct_bearers_cannot_win()
    {
        var state = BossFight(aEmbers: 0, bEmbers: 10);
        state = state with { Heroes = [state.H(A), state.H(B) with { Status = HeroStatus.Extinct, Hp = 0 }] };

        var (next, _) = Resolve(state, (A, Attack(Direction.North)));

        Assert.Equal(A, Assert.Single(next.Outcome!.Winners));
    }

    [Fact]
    public void Everyone_down_is_a_collective_defeat()
    {
        var errata = Hero(A, HeroClass.Errata, 4, 5) with { Hp = 1 };

        var (next, _) = Resolve(State([errata], [StrikingAt(4, 5)]), (A, Interact(Direction.East)));

        Assert.Equal(OutcomeKind.CollectiveDefeat, next.Outcome!.Kind);
        Assert.Equal(DefeatReason.AllFallen, next.Outcome.Reason);
        Assert.Empty(next.Outcome.Winners);
    }

    [Fact]
    public void There_is_no_round_limit()
    {
        var state = State([Hero(A, HeroClass.Mampostero, 4, 4)], round: 61);

        var (next, _) = Resolve(state, FixedRandom.Never);

        Assert.Null(next.Outcome);
        Assert.Equal(MatchPhase.InProgress, next.Phase);
        Assert.Equal(62, next.Round);
    }

    [Fact]
    public void Clearing_a_room_and_reaching_the_stairs_leads_to_the_next_room()
    {
        var first = EmptyRoom(0).WithTerrain(new Position(4, 0), Terrain.Stairs);
        var second = EmptyRoom(1) with { InitialEnemies = [Enemy(7, EnemyKind.Totem, 4, 4)] };
        var state = State([Hero(A, HeroClass.Mampostero, 4, 1, seat: 0), Hero(B, HeroClass.Usurera, 8, 8, seat: 1)]) with { Rooms = [first, second] };

        var (next, events) = Resolve(state, (A, Move(Direction.North)));

        Assert.Equal(1, next.RoomIndex);
        Assert.Equal(MatchFactory.SpawnPoints[0], next.H(A).Position);
        Assert.Equal(MatchFactory.SpawnPoints[1], next.H(B).Position);
        Assert.Equal(7, Assert.Single(next.Enemies).Id);
        Assert.Contains(events, e => e is RoomEntered { RoomIndex: 1 });
    }

    private static GameState BossFight(int aEmbers, int bEmbers)
    {
        var warden = Model.Enemy.Create(1, EnemyKind.Warden, new Position(4, 2), maxHp: 24) with { Hp = 1, Phase = 2 };
        var mampostero = Hero(A, HeroClass.Mampostero, 4, 3, seat: 0) with { Embers = aEmbers, Pact = PactKind.OpenThreeChests };
        var usurera = Hero(B, HeroClass.Usurera, 8, 8, seat: 1) with { Embers = bEmbers, Pact = PactKind.ReviveTwoAllies };
        return State([mampostero, usurera], [warden]);
    }
}
