using Ascua.Domain.Model;
using Ascua.Sim;

namespace Ascua.Domain.Tests;

/// <summary>
/// Versión corta del simulador de balance para el CI (tools/Ascua.Sim, 05-heroes.md §14): un puñado de partidas por
/// héroe, sin excepciones ni partidas colgadas. El balance en sí se mide con la corrida completa (docs/balance.md).
/// </summary>
public sealed class SimulatorTests
{
    private static readonly BotOptions Options = new();

    public static TheoryData<HeroClass> AllHeroes()
    {
        var data = new TheoryData<HeroClass>();
        foreach (var hero in Enum.GetValues<HeroClass>())
        {
            data.Add(hero);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllHeroes))]
    public void A_handful_of_matches_with_each_hero_finish_without_exceptions(HeroClass hero)
    {
        var others = Enum.GetValues<HeroClass>().Where(h => h != hero).ToList();
        for (var game = 0; game < 12; game++)
        {
            var players = 2 + (game % 4);
            var heroes = new List<HeroClass> { hero };
            heroes.AddRange(Enumerable.Range(0, players - 1).Select(i => others[(game + i) % others.Count]));

            var record = MatchSimulator.Play(heroes, (ulong)((int)hero * 1000) + (ulong)game, Options);

            Assert.InRange(record.Rounds, 1, MatchSimulator.MaxRounds);
            Assert.Equal(players, record.Stats.Count);
        }
    }

    [Fact]
    public void Each_hero_uses_their_skill_in_a_short_run()
    {
        var report = new BalanceReport(BalanceRun.Run([4], 2, 77, Options));

        foreach (var hero in Enum.GetValues<HeroClass>())
        {
            Assert.True(report.AverageSkillUses(hero) > 0, $"{hero} no usó su habilidad.");
        }
    }

    [Fact]
    public void The_same_seed_gives_the_same_match()
    {
        HeroClass[] heroes = [HeroClass.Polizon, HeroClass.Avaro, HeroClass.Hilvan, HeroClass.Campanero];

        var first = MatchSimulator.Play(heroes, 12345, Options);
        var second = MatchSimulator.Play(heroes, 12345, Options);

        Assert.Equal(first.Outcome, second.Outcome);
        Assert.Equal(first.Rounds, second.Rounds);
        Assert.Equal(first.Winners, second.Winners);
        Assert.Equal(first.Stats, second.Stats);
    }

    [Theory]
    [InlineData(2, 36)]
    [InlineData(3, 84)]
    [InlineData(4, 126)]
    public void Runs_cover_every_combination_of_players(int size, int combinations)
    {
        var all = BalanceRun.Combinations(size).ToList();

        Assert.Equal(combinations, all.Count);
        Assert.Equal(combinations, all.Select(c => string.Join(',', c)).Distinct().Count());
    }

    [Fact]
    public void The_report_has_all_the_metrics_of_the_table()
    {
        var games = BalanceRun.Run([2, 3, 4], 1, 5, Options);

        var markdown = new BalanceReport(games).ToMarkdown("Prueba", 1, 5, Options, TimeSpan.FromSeconds(1));

        foreach (var metric in new[] { "Celador", "Tasa de victoria", "dúo", "Rondas por partida", "caídos o extintos", "El Umbral reclama" })
        {
            Assert.Contains(metric, markdown, StringComparison.Ordinal);
        }
    }
}
