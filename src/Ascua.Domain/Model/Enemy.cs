using System.Collections.Immutable;

namespace Ascua.Domain.Model;

/// <summary>
/// Enemigos y jefe. En el juego: Carroñero, Polilla, Gelatina, Saltasapo, Ballestero, Tótem y el Celador.
/// </summary>
public enum EnemyKind
{
    /// <summary>Carroñero: se acerca y golpea una casilla adyacente.</summary>
    Scavenger,

    /// <summary>Polilla: vuela (ignora trampas de suelo y grietas); 1 de vida.</summary>
    Moth,

    /// <summary>Gelatina: al morir se divide en 2 gelatinas de 1 de vida.</summary>
    Slime,

    /// <summary>Saltasapo: se mueve 2 casillas por ronda.</summary>
    Hopper,

    /// <summary>Ballestero: anuncia una línea completa y dispara la ronda siguiente.</summary>
    Crossbowman,

    /// <summary>Tótem: inmóvil; invoca 1 carroñero cada 3 rondas (máx. 3) y luego se apaga.</summary>
    Totem,

    /// <summary>El Celador: jefe de 2 fases.</summary>
    Warden,
}

public sealed record EnemyStats(int MaxHp, int Damage, int Speed, bool Flies);

public static class EnemyCatalog
{
    public const int WardenHpPerPlayer = 5;
    public const int TotemSummonEvery = 5;
    public const int TotemMaxSummons = 3;

    /// <summary>Vida con la que nace un monstruo en una partida con estas maldiciones (el Celador va aparte).</summary>
    public static int HpOf(EnemyKind kind, int baseHp, IReadOnlyCollection<Curse> curses)
    {
        ArgumentNullException.ThrowIfNull(curses);
        return kind != EnemyKind.Warden && curses.Contains(Curse.Fierce) ? baseHp + 1 : baseHp;
    }

    /// <summary>Vida del Celador según jugadores y maldiciones.</summary>
    public static int WardenHp(int playerCount, IReadOnlyCollection<Curse> curses)
    {
        ArgumentNullException.ThrowIfNull(curses);
        var hp = WardenHpPerPlayer * playerCount;
        return curses.Contains(Curse.WardenPlus) ? ((hp * 3) + 1) / 2 : hp;
    }

    public static EnemyStats For(EnemyKind kind) => kind switch
    {
        EnemyKind.Scavenger => new(MaxHp: 1, Damage: 1, Speed: 1, Flies: false),
        EnemyKind.Moth => new(MaxHp: 1, Damage: 1, Speed: 1, Flies: true),
        EnemyKind.Slime => new(MaxHp: 1, Damage: 1, Speed: 1, Flies: false),
        EnemyKind.Hopper => new(MaxHp: 1, Damage: 1, Speed: 2, Flies: false),
        EnemyKind.Crossbowman => new(MaxHp: 1, Damage: 2, Speed: 1, Flies: false),
        EnemyKind.Totem => new(MaxHp: 2, Damage: 0, Speed: 0, Flies: false),
        EnemyKind.Warden => new(MaxHp: WardenHpPerPlayer, Damage: 2, Speed: 1, Flies: false),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

public sealed record Enemy
{
    public required int Id { get; init; }
    public required EnemyKind Kind { get; init; }
    public Position Position { get; init; }
    public Direction Facing { get; init; } = Direction.South;
    public int Hp { get; init; }
    public int MaxHp { get; init; }

    /// <summary>Casillas que golpeará en la próxima ronda (anunciadas en la fase 5).</summary>
    public ImmutableList<Position> Telegraph { get; init; } = [];

    public bool Frozen { get; init; }

    /// <summary>Estorbo de una Brasa: su ataque anunciado se ejecuta una ronda más tarde.</summary>
    public bool Delayed { get; init; }

    /// <summary>Errata desvió su anuncio: al ejecutarse golpea todo lo que haya en esas casillas.</summary>
    public PlayerId? DivertedBy { get; init; }

    /// <summary>Ascuas que recibió del Avaro; las suelta al morir.</summary>
    public int Bribes { get; init; }

    public int BurnRounds { get; init; }
    public int BurnDamage { get; init; }

    /// <summary>Gelatina nacida de una división: ya no se divide.</summary>
    public bool IsSplit { get; init; }

    public int SummonTimer { get; init; }
    public int Summoned { get; init; }

    /// <summary>Fase del Celador (1 o 2).</summary>
    public int Phase { get; init; } = 1;

    /// <summary>Celador en fase 2: el próximo anuncio será la cruz completa.</summary>
    public bool CrossNext { get; init; }

    /// <summary>Celador: acaba de anunciar; en su próximo turno toma aire (no se mueve, ni se gira, ni anuncia).</summary>
    public bool WardenRests { get; init; }

    public EnemyStats Stats => EnemyCatalog.For(Kind);
    public bool Flies => Stats.Flies;

    public static Enemy Create(int id, EnemyKind kind, Position position, int? maxHp = null)
    {
        var hp = maxHp ?? EnemyCatalog.For(kind).MaxHp;
        return new Enemy { Id = id, Kind = kind, Position = position, Hp = hp, MaxHp = hp };
    }
}
