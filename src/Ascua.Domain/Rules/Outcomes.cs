using System.Collections.Immutable;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>Fin de partida (sección 7 de las reglas).</summary>
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
            return Defeat(ctx.Heroes, DefeatReason.AllFallen);
        }

        return ctx.Round >= ctx.Start.MaxRounds ? Defeat(ctx.Heroes, DefeatReason.RoundLimit) : null;
    }

    /// <summary>Puntuación final: ascuas más el pacto cumplido (+4), que se revela ahora.</summary>
    public static ImmutableList<FinalScore> Scores(IEnumerable<Hero> heroes) =>
    [
        .. heroes.OrderBy(h => h.Seat).Select(h =>
        {
            var fulfilled = PactRules.IsFulfilled(h.Pact, h.Progress);
            return new FinalScore(h.Player, h.Embers, h.Pact, fulfilled, h.Embers + (fulfilled ? PactRules.Reward : 0));
        }),
    ];

    /// <summary>Gana quien tenga más ascuas entre los no extintos; empate en el máximo = empate.</summary>
    public static GameOutcome Victory(IReadOnlyCollection<Hero> heroes)
    {
        var scores = Scores(heroes);
        var eligible = heroes.Where(h => h.IsOnBoard).Select(h => h.Player).ToHashSet();
        var contenders = scores.Where(s => eligible.Contains(s.Player)).ToList();
        if (contenders.Count == 0)
        {
            return new GameOutcome(OutcomeKind.CollectiveDefeat, DefeatReason.AllFallen, [], scores);
        }

        var best = contenders.Max(s => s.Total);
        ImmutableList<PlayerId> winners = [.. contenders.Where(s => s.Total == best).Select(s => s.Player)];
        return new GameOutcome(winners.Count == 1 ? OutcomeKind.Victory : OutcomeKind.Draw, DefeatReason.None, winners, scores);
    }

    public static GameOutcome Defeat(IReadOnlyCollection<Hero> heroes, DefeatReason reason) =>
        new(OutcomeKind.CollectiveDefeat, reason, [], Scores(heroes));
}
