using System.Collections.Immutable;
using Ascua.Domain.Model;

namespace Ascua.Domain.Views;

/// <summary>
/// Lo que un jugador puede ver. Se construye con <see cref="PlayerViewProjector"/> y es lo único
/// que sale hacia el cliente: no contiene información oculta de otros jugadores ni la semilla.
/// </summary>
public sealed record PlayerView(
    PlayerId Viewer,
    int Round,
    MatchPhase Phase,
    int RoomIndex,
    int RoomCount,
    long LastSeq,
    SelfView Self,
    ImmutableList<AllyView> Allies,
    ImmutableList<TileView> Tiles,
    ImmutableList<EnemyView> Enemies,
    ImmutableList<ChestView> Chests,
    ImmutableList<FireView> Fires,
    ImmutableList<TelegraphView> Telegraphs,
    OutcomeView? Outcome)
{
    /// <summary>Próximos Ecos de Anteayer cuya casilla se ve; ella ve todos los suyos.</summary>
    public ImmutableList<EchoView> Echoes { get; init; } = [];

    /// <summary>Ascuas en el suelo en casillas que se ven.</summary>
    public ImmutableList<GroundEmbersView> Ground { get; init; } = [];

    /// <summary>La Escucha: casillas donde oyó ruido en la última resolución. Vacío para los demás.</summary>
    public ImmutableList<Position> Noise { get; init; } = [];

    /// <summary>Quién tocó el Repique que suena en la ronda abierta (es público: se oye).</summary>
    public PlayerId? BellRinger { get; init; }

    /// <summary>Venció el plazo reducido del Repique en la ronda abierta.</summary>
    public bool BellClosed { get; init; }

    /// <summary>Tipo, lado de la sala y hasta dónde subió el agua: público.</summary>
    public RoomInfoView Room { get; init; } = new(RoomType.Normal, 0);

    /// <summary>Maldiciones de la partida: públicas.</summary>
    public ImmutableList<Curse> Curses { get; init; } = [];

    /// <summary>Ronda desde la que llega el frío (07-version-4.md §7).</summary>
    public int ChillRound { get; init; } = GameState.ChillStartRound;
}

/// <summary>Tipo de la sala, filas inundadas y lado (9 con 2 a 4 jugadores, 11 con 5).</summary>
public sealed record RoomInfoView(RoomType Type, int FloodedRows)
{
    public int Size { get; init; } = Model.Room.DefaultSize;
}

/// <summary>
/// Datos completos del propio héroe, incluidos sus secretos (pacto, bendiciones, draft) y los de los caminos:
/// la elección pendiente, el camino elegido sin revelar, los ecos y las ascuas guardadas.
/// </summary>
public sealed record SelfView(
    HeroClass Class,
    Position Position,
    Direction Facing,
    int Hp,
    int MaxHp,
    int Shield,
    int Embers,
    HeroStatus Status,
    int DownRounds,
    int SkillCooldown,
    bool Frozen,
    int BurnRounds,
    int SilverKeys,
    int VisionRadius,
    ImmutableList<Blessing> Blessings,
    ImmutableList<Synergy> Synergies,
    ImmutableList<Blessing> DraftOptions,
    PactKind Pact,
    PathView Path)
{
    /// <summary>Hilván: su segundo cuerpo.</summary>
    public Position? SecondBody { get; init; }

    /// <summary>Polizón: el enemigo en el que va (nadie más lo sabe).</summary>
    public int? HostEnemyId { get; init; }

    /// <summary>Lo que debe a la Usurera.</summary>
    public int Debt { get; init; }

    /// <summary>A quién le debe.</summary>
    public PlayerId? DebtTo { get; init; }

    /// <summary>Anteayer: adónde saltaría Volver en la ronda abierta; <c>null</c> sin 2 rondas de historia.</summary>
    public Position? RewindTile { get; init; }

    /// <summary>Anteayer: vida que tenía allí.</summary>
    public int? RewindHp { get; init; }

    /// <summary>Misiones entre las que elegir; vacío si ya eligió.</summary>
    public ImmutableList<PactOffer> PactOptions { get; init; } = [];

    /// <summary>Objetivo de su misión, si tiene (solo él lo ve).</summary>
    public PlayerId? PactTarget { get; init; }

    /// <summary>Rondas que faltan para poder empujar.</summary>
    public int PushCooldown { get; init; }
}

/// <summary>
/// Lo privado de los caminos. <see cref="OfferPending"/>: hay que elegir (oferta número <see cref="Extinctions"/>);
/// <see cref="Choice"/>: lo elegido, que los demás no ven hasta que se resuelve la ronda.
/// </summary>
public sealed record PathView(
    bool OfferPending,
    int Extinctions,
    PathKind? Choice,
    int SavedEmbers,
    int Echoes,
    int HinderCooldown,
    bool AwaitingSpawn,
    int? SpawnRound);

/// <summary>
/// Otro portador. La posición es <c>null</c> si va dentro de un huésped o está fuera de la partida, si es del otro
/// bando (Renegado frente al grupo) y está fuera de la visión, y para La Escucha si no lo percibe. De sus bendiciones
/// solo se ve cuántas tiene; la vida máxima se omite porque las delataría. Sus ecos y ascuas guardadas nunca se ven.
/// </summary>
public sealed record AllyView(
    PlayerId Player,
    int Seat,
    HeroClass Class,
    Position? Position,
    Direction? Facing,
    int Hp,
    HeroStatus Status,
    int Embers,
    int BlessingCount)
{
    /// <summary>Hilván: su segundo cuerpo, con la misma regla de visibilidad que el primero.</summary>
    public Position? SecondBody { get; init; }

    /// <summary>Lo que le debe a quien mira: solo lo ven el deudor y la Usurera.</summary>
    public int? DebtToMe { get; init; }
}

/// <summary>Casilla conocida: la visión, o toda la sala para La Escucha. Las de fuera no se envían.</summary>
public sealed record TileView(Position Position, Terrain Terrain);

public sealed record EnemyView(int Id, EnemyKind Kind, Position Position, Direction Facing, int Hp, int MaxHp, int Phase);

/// <summary>
/// Cofre visible. <see cref="HasSilverKey"/> solo se informa al dueño de la sinergia Saqueador
/// y <see cref="Loot"/> solo a quien lo abrió.
/// </summary>
public sealed record ChestView(int Id, Position Position, bool Opened, bool? HasSilverKey, ChestLoot? Loot);

public sealed record FireView(Position Position, int Rounds);

public sealed record TelegraphView(int? EnemyId, ImmutableList<Position> Tiles);

public sealed record EchoView(PlayerId Owner, Position Origin, Direction Direction, int FireRound);

public sealed record GroundEmbersView(Position Position, int Amount);

/// <summary>Resultado final; al terminar se revelan los pactos y las traiciones de todos.</summary>
public sealed record OutcomeView(OutcomeKind Kind, DefeatReason Reason, ImmutableList<PlayerId> Winners, ImmutableList<FinalScore> Scores)
{
    public ImmutableList<Betrayal> Betrayals { get; init; } = [];

    public ImmutableList<Award> Awards { get; init; } = [];
}
