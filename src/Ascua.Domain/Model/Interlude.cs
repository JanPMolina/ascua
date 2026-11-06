using System.Collections.Immutable;

namespace Ascua.Domain.Model;

/// <summary>Maldiciones: dificultad opcional que elige el anfitrión (06-version-3.md §9).</summary>
public enum Curse
{
    /// <summary>Monstruos +1 de vida.</summary>
    Fierce,

    /// <summary>Celador con 50 % más de vida.</summary>
    WardenPlus,

    /// <summary>Visión −1 para todos.</summary>
    Dim,

    /// <summary>Rondas de 7 s.</summary>
    Haste,
}

/// <summary>Texto de una maldición o de un tipo de sala, para el catálogo.</summary>
public sealed record CatalogEntry(string Name, string Effect);

public static class CurseRules
{
    /// <summary>12 s × 11 / 20, hacia arriba (07-version-4.md §8).</summary>
    public static readonly TimeSpan HasteRoundDuration = TimeSpan.FromSeconds(7);

    public static CatalogEntry Describe(Curse curse) => curse switch
    {
        Curse.Fierce => new("Fieros", "Los monstruos tienen 1 de vida más."),
        Curse.WardenPlus => new("Celador imponente", "El Celador tiene 50 % más de vida."),
        Curse.Dim => new("Penumbra", "Todos ven 1 casilla menos."),
        _ => new("Prisa", "Las rondas duran 7 segundos."),
    };

    public static CatalogEntry Describe(RoomType type) => type switch
    {
        RoomType.Treasure => new("Tesoro", "4 cofres y 1 monstruo más."),
        RoomType.Arena => new("Arena", "Sin cofres, 1 o 2 monstruos más y cada golpe final da 2 de oro."),
        RoomType.Sanctuary => new("Santuario", "Pocos monstruos, un altar que no pide llave y más trampas."),
        RoomType.Dark => new("Oscura", "Todos ven 1 casilla menos y hay 1 cofre más."),
        RoomType.Flooded => new("Inundada", "Desde el norte, cada 6 rondas se inunda una fila más: 1 de daño por ronda a quien no vuela."),
        _ => new("Normal", "Sin regla propia."),
    };
}

public enum AwardKind
{
    MostBetrayals,
    MostWardenDamage,
    MostRevives,
    /// <summary>Obsoleto desde la versión 4 (no hay El Juicio): solo en partidas viejas.</summary>
    MostTrialGoldLost,
    RichestDeath,
    MostKills,
    MostChests,
    MostPushes,
}

