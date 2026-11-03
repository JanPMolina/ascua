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

    /// <summary>"El Umbral se apagó": todos caídos o extintos, o se agotaron las rondas.</summary>
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
    RoundLimit,
}

public sealed record FinalScore(PlayerId Player, int Embers, PactKind Pact, bool PactFulfilled, int Total);

/// <summary>
/// Daño anónimo a un aliado: un golpe desviado por Errata o dado desde un huésped por Polizón. El registro culpa
/// al monstruo; esto se guarda en secreto y se revela en la pantalla final.
/// </summary>
public sealed record Betrayal(PlayerId Traitor, PlayerId Victim, int Damage);

public sealed record GameOutcome(
    OutcomeKind Kind,
    DefeatReason Reason,
    ImmutableList<PlayerId> Winners,
    ImmutableList<FinalScore> Scores)
{
    /// <summary>Traiciones anónimas de la partida, sumadas por traidor y víctima (se revelan al final).</summary>
    public ImmutableList<Betrayal> Betrayals { get; init; } = [];
}

/// <summary>
/// Verdad completa de una partida, incluida la información oculta. Inmutable: cada
/// resolución produce un estado nuevo. Nunca se envía tal cual a un cliente; para eso
/// está <c>PlayerViewProjector</c>.
/// </summary>
public sealed record GameState
{
    public const int DefaultMaxRounds = 60;

    public int Round { get; init; } = 1;
    public int MaxRounds { get; init; } = DefaultMaxRounds;
    public MatchPhase Phase { get; init; } = MatchPhase.Drafting;

    /// <summary>Héroes ordenados por asiento.</summary>
    public ImmutableList<Hero> Heroes { get; init; } = [];

    public ImmutableList<Room> Rooms { get; init; } = [];
    public int RoomIndex { get; init; }

    /// <summary>Enemigos vivos de la sala actual.</summary>
    public ImmutableList<Enemy> Enemies { get; init; } = [];

    public int NextEnemyId { get; init; } = 1;

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

    /// <summary>¿Suena el Repique en la ronda abierta?</summary>
    public bool IsBellRound => BellRinger is not null && BellRound == Round;
}
