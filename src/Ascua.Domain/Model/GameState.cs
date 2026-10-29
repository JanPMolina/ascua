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
}

public enum DefeatReason
{
    None,
    AllFallen,
    RoundLimit,
}

public sealed record FinalScore(PlayerId Player, int Embers, PactKind Pact, bool PactFulfilled, int Total);

public sealed record GameOutcome(
    OutcomeKind Kind,
    DefeatReason Reason,
    ImmutableList<PlayerId> Winners,
    ImmutableList<FinalScore> Scores);

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

    public Room CurrentRoom => Rooms[RoomIndex];

    public Hero? FindHero(PlayerId player) => Heroes.Find(h => h.Player == player);
}
