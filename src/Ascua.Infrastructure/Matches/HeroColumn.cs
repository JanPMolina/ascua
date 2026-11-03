using Ascua.Domain.Model;

namespace Ascua.Infrastructure.Matches;

/// <summary>Forma de la columna <c>match_players.hero</c>: la misma del contrato (ERRATA, ANTEAYER…).</summary>
internal static class HeroColumn
{
    public static string From(HeroClass hero) => hero.ToString().ToUpperInvariant();

    public static HeroClass Parse(string value) => Enum.Parse<HeroClass>(value, ignoreCase: true);
}
