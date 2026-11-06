using System.Collections.Immutable;

namespace Ascua.Domain.Model;

public enum MatchPhase
{
    /// <summary>Draft inicial de bendiciones (1 entre 2, privado).</summary>
    Drafting,
    InProgress,
    Finished,
}

public enum OutcomeKind
{
    /// <summary>El Celador cayó y un único portador tiene más ascuas.</summary>
    Victory,

    /// <summary>El Celador cayó y varios empatan en el máximo de ascuas.</summary>
    Draw,

    /// <summary>"El Umbral se apagó": todos caídos o extintos.</summary>
    CollectiveDefeat,

    /// <summary>No queda ningún humano activo.</summary>
    Abandoned,

    /// <summary>"El Umbral reclama": todos los héroes caídos o extintos con Renegados en pie; ganan ellos.</summary>
    ThresholdClaimed,
}

public enum DefeatReason
{
    None,
    AllFallen,

    /// <summary>Obsoleto desde la versión 4 (no hay límite de rondas): solo aparece en partidas viejas.</summary>
    RoundLimit,
}

public sealed record FinalScore(PlayerId Player, int Embers, PactKind Pact, bool PactFulfilled, int Total);

public enum BetrayalKind
{
    /// <summary>Un golpe desviado por Errata.</summary>
    Diverted,

    /// <summary>Un golpe dado desde un huésped por Polizón.</summary>
    FromHost,

    /// <summary>Un sabotaje (obsoleto desde la versión 4: solo en partidas viejas).</summary>
    Sabotage,
}

/// <summary>
/// Daño anónimo a un aliado. El registro no muestra al autor; esto se guarda en secreto y se revela en la pantalla final.
/// </summary>
public sealed record Betrayal(PlayerId Traitor, PlayerId Victim, int Damage)
{
    public BetrayalKind Kind { get; init; }

    /// <summary>Sala en la que ocurrió.</summary>
    public int Room { get; init; }
}

/// <summary>Título de la pantalla final, calculado con datos reales.</summary>
public sealed record Award(AwardKind Kind, PlayerId Player, int Value);

public sealed record GameOutcome(
    OutcomeKind Kind,
    DefeatReason Reason,
    ImmutableList<PlayerId> Winners,
    ImmutableList<FinalScore> Scores)
{
    /// <summary>Traiciones anónimas de la partida, sumadas por traidor y víctima (se revelan al final).</summary>
    public ImmutableList<Betrayal> Betrayals { get; init; } = [];

    /// <summary>Títulos ganados por alguien. Los empates comparten título.</summary>
    public ImmutableList<Award> Awards { get; init; } = [];
}

/// <summary>
/// Verdad completa de una partida, incluida la información oculta. Inmutable: cada
/// resolución produce un estado nuevo. Nunca se envía tal cual a un cliente; para eso
/// está <c>PlayerViewProjector</c>.
/// </summary>
public sealed record GameState
{
    /// <summary>Desde esta ronda llega el frío (07-version-4.md §7). No hay límite de rondas.</summary>
    public const int ChillStartRound = 80;

    /// <summary>El frío golpea en las rondas múltiplo de esto.</summary>
    public const int ChillEvery = 3;

    /// <summary>El daño del frío sube 1 cada tantas rondas pasadas de <see cref="ChillStartRound"/>.</summary>
    public const int ChillGrowth = 15;

    public int Round { get; init; } = 1;
    public MatchPhase Phase { get; init; } = MatchPhase.Drafting;

    /// <summary>Héroes ordenados por asiento.</summary>
    public ImmutableList<Hero> Heroes { get; init; } = [];

    public ImmutableList<Room> Rooms { get; init; } = [];
    public int RoomIndex { get; init; }

    /// <summary>Enemigos vivos de la sala actual.</summary>
    public ImmutableList<Enemy> Enemies { get; init; } = [];

    public int NextEnemyId { get; init; } = 1;

    public int NextChestId { get; init; } = 1;

    /// <summary>Maldiciones de la partida: públicas y fijas desde que se crea.</summary>
    public ImmutableList<Curse> Curses { get; init; } = [];

    /// <summary>Número de secuencia del último evento emitido.</summary>
    public long LastSeq { get; init; }

    public GameOutcome? Outcome { get; init; }

    /// <summary>Traiciones anónimas hasta ahora. Secretas: solo salen en <see cref="GameOutcome.Betrayals"/>.</summary>
    public ImmutableList<Betrayal> Betrayals { get; init; } = [];

    /// <summary>Héroes que hicieron ruido en la última resolución (lo que oye La Escucha).</summary>
    public ImmutableList<PlayerId> NoisyHeroes { get; init; } = [];

    /// <summary>Enemigos que hicieron ruido en la última resolución.</summary>
    public ImmutableList<int> NoisyEnemies { get; init; } = [];

    /// <summary>Casillas donde se oyó ruido en la última resolución.</summary>
    public ImmutableList<Position> NoiseTiles { get; init; } = [];

    /// <summary>Ronda en la que suena el Repique del Campanero (0 = ninguna): los demás tienen menos tiempo.</summary>
    public int BellRound { get; init; }

    /// <summary>Quién hizo sonar el Repique; él conserva todo el reloj.</summary>
    public PlayerId? BellRinger { get; init; }

    /// <summary>Venció el plazo reducido del Repique: ya no se aceptan órdenes de los demás.</summary>
    public bool BellClosed { get; init; }

    public Room CurrentRoom => Rooms[RoomIndex];

    public Hero? FindHero(PlayerId player) => Heroes.Find(h => h.Player == player);

    public bool HasCurse(Curse curse) => Curses.Contains(curse);

    /// <summary>Radio de visión real de un héroe: el suyo, menos la sala Oscura y la maldición DIM. Mínimo 1.</summary>
    public int VisionOf(Hero hero)
    {
        ArgumentNullException.ThrowIfNull(hero);
        var penalty = (CurrentRoom.Type == RoomType.Dark ? 1 : 0) + (HasCurse(Curse.Dim) ? 1 : 0);
        return Math.Max(1, hero.VisionRadius - penalty);
    }

    /// <summary>Daño del frío en esa ronda (0 si no toca).</summary>
    public static int ChillDamage(int round) =>
        round >= ChillStartRound && round % ChillEvery == 0 ? 1 + ((round - ChillStartRound) / ChillGrowth) : 0;

    /// <summary>¿Suena el Repique en la ronda abierta?</summary>
    public bool IsBellRound => BellRinger is not null && BellRound == Round;
}
