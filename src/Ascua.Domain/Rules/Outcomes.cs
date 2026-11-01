using System.Collections.Immutable;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>Fin de partida (sección 7 de las reglas y planteamiento/04-caminos-tras-morir.md).</summary>
public static class Outcomes
{
    internal static GameOutcome? Evaluate(RoundContext ctx)
    {
        if (ctx.WardenSlain)
        {
            return Victory(ctx.Heroes);
        }

        if (!ctx.Heroes.Any(h => h.IsActive))
        {
            return ctx.Heroes.Any(h => h.IsPresentRenegade)
                ? ThresholdClaimed(ctx.Heroes)
                : Defeat(ctx.Heroes, DefeatReason.AllFallen);
        }

        return ctx.Round >= ctx.Start.MaxRounds ? Defeat(ctx.Heroes, DefeatReason.RoundLimit) : null;
    }

    /// <summary>
    /// Ascuas con las que termina: la Brasa con la fórmula de renacer y el extinto que no llegó a elegir
    /// con la mitad de las guardadas (lo que conservaría como Renegado).
    /// </summary>
    public static int FinalEmbers(Hero hero)
    {
        ArgumentNullException.ThrowIfNull(hero);
        return hero.Status switch
        {
            HeroStatus.EmberSpirit => hero.Embers + PathRules.RebirthEmbers(hero.SavedEmbers, hero.Echoes),
            HeroStatus.Extinct => hero.Embers + PathRules.RenegadeEmbers(hero.SavedEmbers),
            _ => hero.Embers,
        };
    }

    /// <summary>Puntuación final: ascuas más el pacto cumplido (+4), que se revela ahora.</summary>
    public static ImmutableList<FinalScore> Scores(IEnumerable<Hero> heroes) =>
    [
        .. heroes.OrderBy(h => h.Seat).Select(h =>
        {
            var fulfilled = PactRules.IsFulfilled(h.Pact, h.Progress);
            var embers = FinalEmbers(h);
            return new FinalScore(h.Player, embers, h.Pact, fulfilled, embers + (fulfilled ? PactRules.Reward : 0));
        }),
    ];

    /// <summary>
    /// Gana quien tenga más ascuas entre los héroes no extintos, las Brasas y los Renegados en pie;
    /// empate en el máximo = empate.
    /// </summary>
    public static GameOutcome Victory(IReadOnlyCollection<Hero> heroes)
    {
        var scores = Scores(heroes);
        var eligible = heroes.Where(h => h.IsOnBoard || h.IsEmber).Select(h => h.Player).ToHashSet();
        var contenders = scores.Where(s => eligible.Contains(s.Player)).ToList();
        if (contenders.Count == 0)
        {
            return new GameOutcome(OutcomeKind.CollectiveDefeat, DefeatReason.AllFallen, [], scores);
        }

        var best = contenders.Max(s => s.Total);
        ImmutableList<PlayerId> winners = [.. contenders.Where(s => s.Total == best).Select(s => s.Player)];
        return new GameOutcome(winners.Count == 1 ? OutcomeKind.Victory : OutcomeKind.Draw, DefeatReason.None, winners, scores);
    }

    /// <summary>"El Umbral reclama": todos los héroes caídos o extintos; ganan los Renegados en pie con más ascuas.</summary>
    public static GameOutcome ThresholdClaimed(IReadOnlyCollection<Hero> heroes)
    {
        var scores = Scores(heroes);
        var standing = heroes.Where(h => h.IsPresentRenegade).Select(h => h.Player).ToHashSet();
        var contenders = scores.Where(s => standing.Contains(s.Player)).ToList();
        var best = contenders.Max(s => s.Total);
        return new GameOutcome(
            OutcomeKind.ThresholdClaimed,
            DefeatReason.AllFallen,
            [.. contenders.Where(s => s.Total == best).Select(s => s.Player)],
            scores);
    }

    public static GameOutcome Defeat(IReadOnlyCollection<Hero> heroes, DefeatReason reason) =>
        new(OutcomeKind.CollectiveDefeat, reason, [], Scores(heroes));
}
