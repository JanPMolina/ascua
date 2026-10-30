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
        PlayerSetup[] players = [new(new PlayerId("a"), HeroClass.Yunque), new(new PlayerId("b"), HeroClass.Cuervo)];
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
    public void Matches_saved_with_the_old_hero_names_still_load()
    {
        PlayerSetup[] players = [new(new PlayerId("a"), HeroClass.Yunque), new(new PlayerId("b"), HeroClass.Raiz)];
        var (state, _) = MatchFactory.Create(players, new SeededRandom(7));
        var json = MatchJson.Serialize(state);
        Assert.Contains("\"Yunque\"", json, StringComparison.Ordinal);

        // Así quedaban en Redis y en el jsonb de Postgres antes de ADR-0008.
        var legacy = json.Replace("\"Yunque\"", "\"Anvil\"", StringComparison.Ordinal).Replace("\"Raiz\"", "\"Root\"", StringComparison.Ordinal);
        var restored = MatchJson.DeserializeState(legacy);
        Assert.Equal(json, MatchJson.Serialize(restored));

        var skill = MatchJson.Serialize(new SkillUsed(new PlayerId("b"), HeroClass.Cuervo, Direction.North));
        var legacySkill = MatchJson.Deserialize<SkillUsed>(skill.Replace("\"Cuervo\"", "\"Raven\"", StringComparison.Ordinal));
        Assert.Equal(HeroClass.Cuervo, legacySkill!.Hero);
    }

    [Theory]
    [InlineData("YUNQUE", HeroClass.Yunque)]
    [InlineData("Chispa", HeroClass.Chispa)]
    [InlineData("Raven", HeroClass.Cuervo)]
    [InlineData("Root", HeroClass.Raiz)]
    public void Hero_column_accepts_new_and_legacy_names(string stored, HeroClass expected)
    {
        Assert.Equal(expected, HeroNames.Parse(stored));
        Assert.Equal(expected.ToString().ToUpperInvariant(), HeroNames.ToColumn(expected));
        Assert.Throws<FormatException>(() => HeroNames.Parse("Dragon"));
    }
}
