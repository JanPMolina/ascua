using System.Collections.Immutable;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>Títulos de la pantalla final (06-version-3.md §10; sin El Juicio desde la versión 4): solo los ganados por alguien; los empates los comparten.</summary>
public static class Awards
{
    public static ImmutableList<Award> Compute(IReadOnlyCollection<Hero> heroes)
    {
        ArgumentNullException.ThrowIfNull(heroes);
        var awards = ImmutableList.CreateBuilder<Award>();
        void Add(AwardKind kind, Func<PactProgress, int> value)
        {
            var best = heroes.Max(h => value(h.Progress));
            if (best > 0)
            {
                awards.AddRange(heroes.Where(h => value(h.Progress) == best).OrderBy(h => h.Seat).Select(h => new Award(kind, h.Player, best)));
            }
        }

        Add(AwardKind.MostBetrayals, p => p.Betrayals);
        Add(AwardKind.MostWardenDamage, p => p.WardenDamage);
        Add(AwardKind.MostRevives, p => p.Revives);
        Add(AwardKind.RichestDeath, p => p.GoldAtDeath);
        Add(AwardKind.MostKills, p => p.Kills);
        Add(AwardKind.MostChests, p => p.ChestsOpened);
        Add(AwardKind.MostPushes, p => p.Pushes);
        return awards.ToImmutable();
    }
}
