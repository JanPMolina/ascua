using System.Collections.Immutable;
using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>Fin de partida (sección 7 de las reglas y planteamiento/04-caminos-tras-morir.md).</summary>
public static class Outcomes
{
    internal static GameOutcome? Evaluate(RoundContext ctx)
    {
        var ended = ctx.WardenSlain || !ctx.Heroes.Any(h => h.IsActive) || ctx.Round >= ctx.Start.MaxRounds;
        if (!ended)
        {
            return null;
        }

        // Al terminar la Usurera cobra lo que quede, antes de decidir al ganador.
        SettleDebts(ctx);
        GameOutcome outcome;
        if (ctx.WardenSlain)
        {
            outcome = Victory(ctx.Heroes);
        }
        else if (!ctx.Heroes.Any(h => h.IsActive))
        {
            outcome = ctx.Heroes.Any(h => h.IsPresentRenegade)
                ? ThresholdClaimed(ctx.Heroes)
                : Defeat(ctx.Heroes, DefeatReason.AllFallen);
        }
        else
        {
            outcome = Defeat(ctx.Heroes, DefeatReason.RoundLimit);
        }

        return outcome with { Betrayals = Summarize(ctx.Betrayals) };
    }

    /// <summary>Traiciones sumadas por traidor y víctima, en el orden en que ocurrieron.</summary>
    public static ImmutableList<Betrayal> Summarize(IEnumerable<Betrayal> betrayals) =>
    [
        .. betrayals
            .GroupBy(b => (b.Traitor, b.Victim))
            .Select(g => new Betrayal(g.Key.Traitor, g.Key.Victim, g.Sum(b => b.Damage))),
    ];

    private static void SettleDebts(RoundContext ctx)
    {
        foreach (var debtor in ctx.Heroes.Where(h => h is { Debt: > 0, DebtTo: not null }).OrderBy(h => h.Seat).ToList())
        {
            var hero = ctx.Hero(debtor.Player);
            var creditorId = hero.DebtTo!.Value;
            var final = FinalEmbers(hero);
            var paid = Math.Min(hero.Debt, final);
            if (paid == 0)
            {
                continue;
            }

            // Las ascuas de una Brasa o de un extinto se fijan con su fórmula antes de cobrar.
            ctx.Update(hero with { Embers = final - paid, SavedEmbers = 0, Debt = hero.Debt - paid });
            Combat.EmitDebt(ctx, hero.Player, creditorId, -paid, hero.Debt - paid, DebtReason.MatchEnd);
            ctx.Emit(new EmbersChanged(hero.Player, -paid, EmberReason.DebtSettled, final - paid) { Audience = Audience.Only(hero.Player) });
            var creditor = ctx.Hero(creditorId);
            ctx.Update(creditor with { Embers = creditor.Embers + paid });
            ctx.Emit(new EmbersChanged(creditorId, paid, EmberReason.DebtCollected, creditor.Embers + paid) { Audience = Audience.Only(creditorId) });
        }
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
            HeroStatus.EmberSpirit => hero.Embers + PathRules.RebirthEmbers(hero, hero.Echoes),
            HeroStatus.Extinct => hero.Embers + PathRules.RenegadeEmbers(hero),
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
