using Ascua.Domain.Rules;

namespace Ascua.Domain.Model;

/// <summary>Misiones secretas: cada jugador elige 1 entre 2 y se revelan al final (06-version-3.md §6).</summary>
public enum PactKind
{
    /// <summary>Obsoleta: solo existe para poder leer partidas viejas.</summary>
    SlayWarden,

    /// <summary>Obsoleta: solo existe para poder leer partidas viejas.</summary>
    NeverFall,

    /// <summary>Abrir 3 cofres.</summary>
    OpenThreeChests,

    /// <summary>Levantar a 2 aliados.</summary>
    ReviveTwoAllies,

    /// <summary>Obsoleta: solo existe para poder leer partidas viejas.</summary>
    BetrayUnseen,

    /// <summary>Hacer el mayor daño al Celador.</summary>
    MostWardenDamage,

    /// <summary>Obsoleta desde la versión 4 (ya no hay sabotaje ni condenas): nunca se ofrece ni se cumple.</summary>
    BetrayTwiceUnconvicted,

    /// <summary>Que un jugador concreto (secreto) termine con menos oro que tú.</summary>
    OutgoldTarget,

    /// <summary>Empujar a 2 enemigos a pinchos, fuego o grietas.</summary>
    PushTwoIntoHazard,

    /// <summary>Obsoleta desde la versión 4 (ya no hay El Juicio): nunca se ofrece ni se cumple.</summary>
    CorrectConviction,
}

/// <summary>Una misión ofrecida. <see cref="Target"/> solo en <see cref="PactKind.OutgoldTarget"/>.</summary>
public sealed record PactOffer(PactKind Kind, PlayerId? Target);

/// <summary>Contadores de lo que hizo cada héroe: deciden las misiones y los títulos finales.</summary>
public sealed record PactProgress
{
    public int ChestsOpened { get; init; }
    public int Revives { get; init; }
    public bool KilledWarden { get; init; }
    public bool HasFallen { get; init; }

    /// <summary>Traiciones anónimas cometidas.</summary>
    public int Betrayals { get; init; }

    public int HazardPushes { get; init; }
    public int Pushes { get; init; }
    public int Kills { get; init; }
    public int WardenDamage { get; init; }

    /// <summary>El mayor oro con el que murió.</summary>
    public int GoldAtDeath { get; init; }
}

public static class PactRules
{
    public const int Reward = 3;
    public const int OfferedOptions = 2;

    /// <summary>Misiones que se ofrecen hoy.</summary>
    public static IReadOnlyList<PactKind> Offered { get; } =
    [
        PactKind.MostWardenDamage,
        PactKind.OpenThreeChests,
        PactKind.ReviveTwoAllies,
        PactKind.OutgoldTarget,
        PactKind.PushTwoIntoHazard,
    ];

    public static bool IsFulfilled(Hero hero, IReadOnlyCollection<Hero> heroes)
    {
        ArgumentNullException.ThrowIfNull(hero);
        ArgumentNullException.ThrowIfNull(heroes);
        var progress = hero.Progress;
        return hero.Pact switch
        {
            PactKind.MostWardenDamage => progress.WardenDamage > 0 && progress.WardenDamage >= heroes.Max(h => h.Progress.WardenDamage),
            PactKind.OpenThreeChests => progress.ChestsOpened >= 3,
            PactKind.ReviveTwoAllies => progress.Revives >= 2,
            PactKind.OutgoldTarget => heroes.FirstOrDefault(h => h.Player == hero.PactTarget) is { } target
                && Outcomes.FinalEmbers(hero) > Outcomes.FinalEmbers(target),
            PactKind.PushTwoIntoHazard => progress.HazardPushes >= 2,
            _ => false,
        };
    }
}
