namespace Ascua.Domain.Model;

/// <summary>Pactos secretos: uno por jugador, al azar; se revelan al final.</summary>
public enum PactKind
{
    /// <summary>Dar el golpe final al Celador.</summary>
    SlayWarden,

    /// <summary>Terminar sin haber caído.</summary>
    NeverFall,

    /// <summary>Abrir 3 cofres.</summary>
    OpenThreeChests,

    /// <summary>Levantar a 2 aliados.</summary>
    ReviveTwoAllies,

    /// <summary>Golpear a un aliado al menos 2 veces sin que te descubran (por la espalda).</summary>
    BetrayUnseen,
}

/// <summary>Contadores que deciden si un pacto se cumplió.</summary>
public sealed record PactProgress
{
    public int ChestsOpened { get; init; }
    public int Revives { get; init; }
    public int SneakyAllyHits { get; init; }
    public bool KilledWarden { get; init; }
    public bool HasFallen { get; init; }
}

public static class PactRules
{
    public const int Reward = 4;

    public static bool IsFulfilled(PactKind pact, PactProgress progress) => pact switch
    {
        PactKind.SlayWarden => progress.KilledWarden,
        PactKind.NeverFall => !progress.HasFallen,
        PactKind.OpenThreeChests => progress.ChestsOpened >= 3,
        PactKind.ReviveTwoAllies => progress.Revives >= 2,
        PactKind.BetrayUnseen => progress.SneakyAllyHits >= 2,
        _ => false,
    };
}
