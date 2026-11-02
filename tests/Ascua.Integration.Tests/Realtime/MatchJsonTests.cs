using Ascua.Domain.Abstractions;
using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Infrastructure.Matches;

namespace Ascua.Integration.Tests.Realtime;

/// <summary>La instantánea guardada en Redis/Postgres reproduce exactamente el estado del dominio.</summary>
public sealed class MatchJsonTests
{
    [Fact]
    public void State_round_trips_and_resolves_identically()
    {
        PlayerSetup[] players = [new(new PlayerId("a"), HeroClass.Hilvan), new(new PlayerId("b"), HeroClass.Anteayer)];
        var (created, _) = MatchFactory.Create(players, new SeededRandom(7));
        var state = MatchRules.AutoChooseBlessings(created).State;

        var restored = MatchJson.DeserializeState(MatchJson.Serialize(state));
        Assert.Equal(MatchJson.Serialize(state), MatchJson.Serialize(restored));

        var orders = new Dictionary<PlayerId, Order>
        {
            [players[0].Player] = new(OrderType.Move, Direction.North),
            [players[1].Player] = new(OrderType.Attack, Direction.North),
        };
        var (expected, expectedEvents) = Resolver.Resolve(state, orders, new SeededRandom(11));
        var (actual, actualEvents) = Resolver.Resolve(restored, orders, new SeededRandom(11));
        Assert.Equal(MatchJson.Serialize(expected), MatchJson.Serialize(actual));
        Assert.Equal(expectedEvents.Select(MatchJson.Serialize), actualEvents.Select(MatchJson.Serialize));
    }

    [Fact]
    public void The_new_hero_state_survives_a_round_trip()
    {
        PlayerSetup[] players = [new(new PlayerId("a"), HeroClass.Polizon), new(new PlayerId("b"), HeroClass.Avaro)];
        var (created, _) = MatchFactory.Create(players, new SeededRandom(7));
        var state = created with
        {
            Heroes =
            [
                created.Heroes[0] with
                {
                    SecondBody = new Position(1, 1),
                    HostEnemyId = 3,
                    BoardedRound = 2,
                    Shouting = true,
                    Debt = 2,
                    DebtTo = new PlayerId("b"),
                    DebtSinceRound = 2,
                    EchoQueue = [new PendingEcho(4, new Position(1, 2), Direction.East)],
                    History = [new HeroTrace(2, new Position(1, 3), 3)],
                },
                created.Heroes[1],
            ],
            Betrayals = [new Betrayal(new PlayerId("a"), new PlayerId("b"), 2)],
            NoisyHeroes = [new PlayerId("a")],
            NoisyEnemies = [3],
            NoiseTiles = [new Position(2, 2)],
            BellRound = 2,
            BellRinger = new PlayerId("a"),
            BellClosed = true,
        };

        var restored = MatchJson.DeserializeState(MatchJson.Serialize(state));

        Assert.Equal(MatchJson.Serialize(state), MatchJson.Serialize(restored));
        Assert.Equal(new PlayerId("a"), restored.BellRinger);
        Assert.Equal(3, restored.Heroes[0].HostEnemyId);
    }

    [Fact]
    public void Heroes_that_no_longer_exist_do_not_load()
    {
        PlayerSetup[] players = [new(new PlayerId("a"), HeroClass.Errata), new(new PlayerId("b"), HeroClass.Campanero)];
        var (state, _) = MatchFactory.Create(players, new SeededRandom(7));
        var json = MatchJson.Serialize(state);
        Assert.Contains("\"Errata\"", json, StringComparison.Ordinal);

        var old = json.Replace("\"Errata\"", "\"Yunque\"", StringComparison.Ordinal);

        Assert.ThrowsAny<System.Text.Json.JsonException>(() => MatchJson.DeserializeState(old));
    }

    [Fact]
    public void Every_domain_event_type_round_trips()
    {
        DomainEvent[] events =
        [
            new EchoFired(new PlayerId("a"), new Position(1, 1), Direction.North),
            new AttackCorrected(3, [new Position(1, 1)]),
            new BetrayalRecorded(new PlayerId("a"), new PlayerId("b"), 2),
            new BetrayalRevealed(new PlayerId("a"), new PlayerId("b"), 2),
            new HostBoarded(new PlayerId("a"), 3),
            new HostExited(new PlayerId("a"), new Position(1, 1)),
            new HostDrained(new PlayerId("a"), 3),
            new WardenTurned(1, Direction.North),
            new Shouted(new PlayerId("a")),
            new DebtChanged(new PlayerId("a"), new PlayerId("b"), 2, 2, DebtReason.Loan),
            new EmbersDropped(new Position(1, 1), 2),
            new EmbersPickedUp(new PlayerId("a"), new Position(1, 1), 2),
            new EnemyBribed(new PlayerId("a"), 3, 1),
            new RoomShifted(new PlayerId("a"), ShiftAxis.Row, 4, Direction.East),
            new BellRung(new PlayerId("a"), 3),
            new BellExpired(new PlayerId("a")),
        ];

        foreach (var domainEvent in events)
        {
            var json = MatchJson.Serialize(domainEvent);
            var back = System.Text.Json.JsonSerializer.Deserialize(json, domainEvent.GetType(), MatchJson.Options);
            Assert.Equal(json, MatchJson.Serialize((DomainEvent)back!));
        }
    }
}
