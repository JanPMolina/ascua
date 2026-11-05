using System.Collections.Immutable;
using Ascua.Domain.Model;

namespace Ascua.Domain.Events;

public enum AudienceKind
{
    /// <summary>Todos los jugadores de la partida.</summary>
    Everyone,

    /// <summary>Solo un jugador (información privada).</summary>
    Player,

    /// <summary>Quien tenga la casilla en su visión.</summary>
    Location,

    /// <summary>
    /// El grupo (héroes y Brasas) siempre; los Renegados solo si ven la casilla. Para lo que hace una
    /// Brasa errante, que el grupo ve aunque esté lejos.
    /// </summary>
    Group,
}

/// <summary>Fases de la resolución de una ronda (06-version-3.md §1): el cliente agrupa los eventos con esto.</summary>
public enum RoundPhase
{
    Reveal,
    Guard,
    Shift,
    Movement,
    Actions,
    EnemyAttacks,
    EnemyMoves,
    Cleanup,
    End,
}

/// <summary>Quién puede recibir un evento. Lo usa <c>PlayerViewProjector</c> para filtrar.</summary>
public readonly record struct Audience(AudienceKind Kind, PlayerId? Player, Position? Position)
{
    public static Audience Everyone => new(AudienceKind.Everyone, null, null);

    public static Audience Only(PlayerId player) => new(AudienceKind.Player, player, null);

    public static Audience At(Position position) => new(AudienceKind.Location, null, position);

    public static Audience Group(Position position) => new(AudienceKind.Group, null, position);
}

/// <summary>
/// Hecho ocurrido en la partida. <see cref="Seq"/> crece de uno en uno a lo largo de toda
/// la partida, lo que permite reproducirla ronda a ronda desde el log de eventos.
/// </summary>
public abstract record DomainEvent
{
    public long Seq { get; init; }
    public int Round { get; init; }
    public Audience Audience { get; init; } = Audience.Everyone;

    /// <summary>Fase de la ronda en la que ocurrió; <c>null</c> fuera de una resolución.</summary>
    public RoundPhase? Phase { get; init; }
}

public enum PreventionReason
{
    /// <summary>El Celador en fase 1 es inmune a los golpes de frente.</summary>
    FrontImmunity,

    /// <summary>GUARDAR redujo el daño a 0.</summary>
    Guard,

    /// <summary>Primer golpe de un Renegado a ese héroe: lo absorbe el escudo de aviso.</summary>
    WarningShield,
}

public enum StatusEffect
{
    Burning,
    Frozen,
}

public enum ExtinctionReason
{
    /// <summary>Pasó 3 rondas caído sin que nadie lo levantara.</summary>
    NotRevived,

    /// <summary>Cayó en una grieta.</summary>
    Chasm,

    /// <summary>Era un Renegado y lo derrotaron.</summary>
    Defeated,
}

public enum EmberReason
{
    EnemySlain,
    WardenSlain,

    /// <summary>Parte del reparto de 6 por el daño hecho al Celador.</summary>
    WardenBounty,

    /// <summary>Asistencia: su ataque también alcanzó al enemigo que murió.</summary>
    Assist,

    /// <summary>Condenado en El Juicio.</summary>
    TrialPenalty,

    /// <summary>Acusó a un inocente que resultó condenado.</summary>
    TrialFine,
    ChestOpened,
    AllyRevived,
    StolenFromAlly,
    StolenByAlly,
    Extinction,

    /// <summary>La Brasa renació: recupera parte de sus ascuas guardadas.</summary>
    Rebirth,

    /// <summary>Tomó el Camino de la Venganza: conserva la mitad de sus ascuas guardadas.</summary>
    RenegadeReturn,

    /// <summary>Un Renegado dejó caído a un héroe (+2).</summary>
    HeroDowned,

    /// <summary>Derrotó a un Renegado (+2).</summary>
    RenegadeDefeated,

    /// <summary>El Avaro eligió una bendición que cambia sus ascuas (Corazón doble, Puño de piedra).</summary>
    Blessing,

    /// <summary>El Avaro pagó un Soborno.</summary>
    Bribe,

    /// <summary>El Avaro perdió ascuas por un golpe (caen al suelo).</summary>
    MiserHit,

    /// <summary>Se levantó el Avaro: sus ascuas son su vida.</summary>
    MiserRevived,

    /// <summary>Recogió ascuas del suelo.</summary>
    PickedUp,

    /// <summary>La Usurera cobró una deuda.</summary>
    DebtCollected,

    /// <summary>El deudor pagó lo que debía al terminar la partida.</summary>
    DebtSettled,
}

public sealed record MatchCreated(int PlayerCount) : DomainEvent;

public sealed record DraftOffered(PlayerId Player, ImmutableList<Blessing> Options) : DomainEvent;

public sealed record BlessingChosen(PlayerId Player, Blessing Blessing) : DomainEvent;

public sealed record BlessingCountChanged(PlayerId Player, int Count) : DomainEvent;

public sealed record MatchStarted : DomainEvent;

public sealed record HeroGuarded(PlayerId Player) : DomainEvent;

/// <summary>La entidad estaba congelada y perdió su acción.</summary>
public sealed record ActionLost(EntityRef Entity) : DomainEvent;

/// <summary><see cref="Body"/>: 0 el cuerpo principal, 1 el segundo cuerpo de Hilván.</summary>
public sealed record HeroMoved(PlayerId Player, Position From, Position To) : DomainEvent
{
    public int Body { get; init; }
}

/// <summary>El movimiento rebotó: casilla bloqueada, ocupada o disputada.</summary>
public sealed record MoveBounced(PlayerId Player, Position Attempted) : DomainEvent
{
    public int Body { get; init; }
}

public sealed record HeroAttacked(PlayerId Player, Direction Direction) : DomainEvent;

public sealed record SkillUsed(PlayerId Player, HeroClass Hero, Direction Direction) : DomainEvent;

public sealed record EntityDamaged(EntityRef Target, int Amount, EntityRef? Source, int HpAfter) : DomainEvent;

public sealed record HitPrevented(EntityRef Target, EntityRef? Source, PreventionReason Reason) : DomainEvent;

public sealed record Healed(EntityRef Target, int Amount, int HpAfter) : DomainEvent;

public sealed record StatusApplied(EntityRef Target, StatusEffect Effect, int Rounds) : DomainEvent;

public sealed record HeroDowned(PlayerId Player) : DomainEvent;

public sealed record LastBreathTriggered(PlayerId Player) : DomainEvent;

public sealed record HeroRevived(PlayerId Reviver, PlayerId Target) : DomainEvent;

public sealed record HeroExtinguished(PlayerId Player, ExtinctionReason Reason, int EmbersLost) : DomainEvent;

public sealed record HeroRetired(PlayerId Player) : DomainEvent;

public sealed record EmbersChanged(PlayerId Player, int Delta, EmberReason Reason, int Total) : DomainEvent;

public sealed record ChestOpened(PlayerId Player, int ChestId) : DomainEvent;

/// <summary>Contenido del cofre: solo lo recibe quien lo abre.</summary>
public sealed record ChestLooted(PlayerId Player, int ChestId, ChestLoot Loot, bool SilverKey) : DomainEvent;

public sealed record AltarActivated(PlayerId Player, Position Altar) : DomainEvent;

public sealed record EnemySpawned(int EnemyId, EnemyKind Kind, Position Position) : DomainEvent;

public sealed record EnemyMoved(int EnemyId, Position From, Position To) : DomainEvent;

/// <summary>
/// <see cref="HiddenKiller"/>: el golpe final fue anónimo (desviado por Errata o desde un huésped); solo el autor
/// lo sabe, los demás ven el monstruo muerto sin autor.
/// </summary>
public sealed record EnemyKilled(int EnemyId, EnemyKind Kind, PlayerId? Killer) : DomainEvent
{
    public bool HiddenKiller { get; init; }
}

/// <summary>Anuncio (telegraph) de las casillas que el enemigo golpeará la ronda siguiente.</summary>
public sealed record AttackTelegraphed(int? EnemyId, ImmutableList<Position> Tiles) : DomainEvent;

public sealed record WardenEnraged(int EnemyId) : DomainEvent;

public sealed record TotemExtinguished(int EnemyId) : DomainEvent;

public sealed record FireStarted(Position Position, int Rounds) : DomainEvent;

public sealed record BrazierToppled(Position Position) : DomainEvent;

public sealed record TrapTriggered(Position Position) : DomainEvent;

public sealed record RoomEntered(int RoomIndex) : DomainEvent
{
    public RoomType Type { get; init; }
}

public sealed record PactRevealed(PlayerId Player, PactKind Pact, bool Fulfilled) : DomainEvent;

public sealed record MatchEnded(GameOutcome Outcome) : DomainEvent;

public sealed record RoundResolved(int ResolvedRound) : DomainEvent;

/// <summary>Se abrió la elección de camino tras extinguirse (privado). <see cref="Extinctions"/> identifica la oferta.</summary>
public sealed record PathOffered(PlayerId Player, int Extinctions, int SavedEmbers) : DomainEvent;

/// <summary>El jugador eligió camino; privado hasta que se resuelve la ronda. <see cref="Auto"/>: venció el plazo.</summary>
public sealed record PathSelected(PlayerId Player, PathKind Path, bool Auto) : DomainEvent;

/// <summary>Al resolverse la ronda, todos ven qué camino tomó.</summary>
public sealed record PathChosen(PlayerId Player, PathKind Path) : DomainEvent;

/// <summary>La Brasa renació como héroe al entrar a la sala nueva.</summary>
public sealed record EmberReborn(PlayerId Player, Position Position, int Hp) : DomainEvent;

/// <summary>Un Renegado apareció en la sala. La posición se quita para quien no la ve.</summary>
public sealed record RenegadeSpawned(PlayerId Player, Position? Position) : DomainEvent;

/// <summary><see cref="HiddenBy"/>: lo derrotó un golpe anónimo; solo el autor sabe quién fue.</summary>
public sealed record RenegadeDefeated(PlayerId Renegade, PlayerId? By) : DomainEvent
{
    public bool HiddenBy { get; init; }
}

/// <summary>Una Brasa ganó ecos (privado: el grupo solo ve que la Brasa ayudó).</summary>
public sealed record EchoGained(PlayerId Player, int Amount, EchoReason Reason, int Total) : DomainEvent;

/// <summary>Una Brasa usó una habilidad (Deriva se informa como <see cref="HeroMoved"/>).</summary>
public sealed record EmberAbilityUsed(PlayerId Player, EmberAbility Ability, EntityRef? Target, Position? Tile) : DomainEvent;

/// <summary>El Eco de Anteayer repitió un ataque desde donde estaba.</summary>
public sealed record EchoFired(PlayerId Player, Position Origin, Direction Direction) : DomainEvent;

/// <summary>Errata desvió un anuncio: no dice quién fue y solo lleva las casillas que se ven.</summary>
public sealed record AttackCorrected(int? EnemyId, ImmutableList<Position> Tiles) : DomainEvent;

/// <summary>Traición anónima (privado: solo la ve quien la cometió hasta la pantalla final).</summary>
public sealed record BetrayalRecorded(PlayerId Traitor, PlayerId Victim, int Damage) : DomainEvent
{
    public BetrayalKind Kind { get; init; }
}

/// <summary>Al terminar se revelan las traiciones, sumadas por traidor y víctima.</summary>
public sealed record BetrayalRevealed(PlayerId Traitor, PlayerId Victim, int Damage) : DomainEvent;

/// <summary>Polizón entró en un monstruo (privado: para los demás, desaparece).</summary>
public sealed record HostBoarded(PlayerId Player, int EnemyId) : DomainEvent;

/// <summary>Polizón salió del huésped: reaparece en esa casilla.</summary>
public sealed record HostExited(PlayerId Player, Position Tile) : DomainEvent;

/// <summary>Polizón drenó a su huésped (privado).</summary>
public sealed record HostDrained(PlayerId Player, int EnemyId) : DomainEvent;

/// <summary>Abordar giró 180° al Celador hasta su próximo anuncio.</summary>
public sealed record WardenTurned(int EnemyId, Direction Facing) : DomainEvent;

/// <summary>La Escucha gritó: su bando ve la sala entera hasta la próxima resolución.</summary>
public sealed record Shouted(PlayerId Player) : DomainEvent;

public enum DebtReason
{
    /// <summary>Nuevo Préstamo de la Usurera.</summary>
    Loan,

    /// <summary>Subió por el interés (cada 5 rondas).</summary>
    Interest,

    /// <summary>Se pagó con ascuas ganadas.</summary>
    Paid,

    /// <summary>Se cobró de las ascuas guardadas al extinguirse.</summary>
    Extinction,

    /// <summary>Se cobró lo que quedaba al terminar la partida.</summary>
    MatchEnd,

    /// <summary>Se canceló porque la Usurera abandonó la partida.</summary>
    Cancelled,
}

/// <summary>Cambio en una deuda. Cada cambio se emite dos veces: una para el deudor y otra para la Usurera.</summary>
public sealed record DebtChanged(PlayerId Debtor, PlayerId Creditor, int Delta, int Total, DebtReason Reason) : DomainEvent;

/// <summary>Cayeron ascuas al suelo.</summary>
public sealed record EmbersDropped(Position Tile, int Amount) : DomainEvent;

/// <summary>Alguien recogió ascuas del suelo.</summary>
public sealed record EmbersPickedUp(PlayerId Player, Position Tile, int Amount) : DomainEvent;

/// <summary>El Avaro sobornó a un enemigo: canceló su anuncio.</summary>
public sealed record EnemyBribed(PlayerId Player, int EnemyId, int Cost) : DomainEvent;

public enum ShiftAxis
{
    Row,
    Column,
}

/// <summary>Corrimiento: la sala tiembla y todos ven qué línea se movió.</summary>
public sealed record RoomShifted(PlayerId Player, ShiftAxis Axis, int Line, Direction Direction) : DomainEvent;

/// <summary>Repique: en <see cref="ForRound"/> los demás tendrán menos tiempo.</summary>
public sealed record BellRung(PlayerId Player, int ForRound) : DomainEvent;

/// <summary>Venció el plazo reducido del Repique en la ronda abierta.</summary>
public sealed record BellExpired(PlayerId Ringer) : DomainEvent;

/// <summary>El jugador eligió misión (privado).</summary>
public sealed record PactChosen(PlayerId Player, PactKind Pact) : DomainEvent;

/// <summary>Empujón: es visible y lleva autor. <see cref="Target"/> es un héroe o un enemigo.</summary>
public sealed record Pushed(PlayerId Pusher, EntityRef Target, Position From, Position To) : DomainEvent;

/// <summary>El agua subió: ya hay tantas filas inundadas desde el norte.</summary>
public sealed record RoomFlooded(int Rows) : DomainEvent;

/// <summary>Se activó la escalera: abre El Juicio y, si toca, la votación de puertas.</summary>
public sealed record InterludeOpened(int EndedRoom, ImmutableList<RoomType> Doors) : DomainEvent;

/// <summary>Su voto (privado: nadie más sabe qué votó).</summary>
public sealed record VoteCast(PlayerId Voter, PlayerId? Accused, int? Door) : DomainEvent;

/// <summary>Se cerró el interludio: el recuento público.</summary>
public sealed record InterludeClosed(InterludeVerdict Verdict) : DomainEvent;
