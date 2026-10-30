using Ascua.Domain.Abstractions;
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
        PlayerSetup[] players = [new(new PlayerId("a"), HeroClass.Anvil), new(new PlayerId("b"), HeroClass.Raven)];
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
}
