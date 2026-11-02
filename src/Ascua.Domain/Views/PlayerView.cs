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
    int MaxRounds,
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
    OutcomeView? Outcome);

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
    int Charges,
    int StealthRounds,
    bool Frozen,
    int BurnRounds,
    int SilverKeys,
    int VisionRadius,
    ImmutableList<Blessing> Blessings,
    ImmutableList<Synergy> Synergies,
    ImmutableList<Blessing> DraftOptions,
    PactKind Pact,
    PathView Path);

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
/// Otro portador. La posición es <c>null</c> si está en sigilo o fuera de la partida, y también si es del otro
/// bando (Renegado frente al grupo) y está fuera de la visión. De sus bendiciones solo se ve cuántas tiene;
/// la vida máxima se omite porque las delataría. Sus ecos y ascuas guardadas nunca se ven.
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
    int BlessingCount);

/// <summary>Casilla dentro de la visión. Las de fuera no se envían.</summary>
public sealed record TileView(Position Position, Terrain Terrain);

public sealed record EnemyView(int Id, EnemyKind Kind, Position Position, Direction Facing, int Hp, int MaxHp, int Phase);

/// <summary>
/// Cofre visible. <see cref="HasSilverKey"/> solo se informa al dueño de la sinergia Saqueador
/// y <see cref="Loot"/> solo a quien lo abrió.
/// </summary>
public sealed record ChestView(int Id, Position Position, bool Opened, bool? HasSilverKey, ChestLoot? Loot);

public sealed record FireView(Position Position, int Rounds);

public sealed record TelegraphView(int? EnemyId, ImmutableList<Position> Tiles);

/// <summary>Resultado final; al terminar se revelan los pactos de todos.</summary>
public sealed record OutcomeView(OutcomeKind Kind, DefeatReason Reason, ImmutableList<PlayerId> Winners, ImmutableList<FinalScore> Scores);
