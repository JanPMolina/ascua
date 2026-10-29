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
}

/// <summary>Quién puede recibir un evento. Lo usa <c>PlayerViewProjector</c> para filtrar.</summary>
public readonly record struct Audience(AudienceKind Kind, PlayerId? Player, Position? Position)
{
    public static Audience Everyone => new(AudienceKind.Everyone, null, null);

    public static Audience Only(PlayerId player) => new(AudienceKind.Player, player, null);

    public static Audience At(Position position) => new(AudienceKind.Location, null, position);
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
}

public enum PreventionReason
{
    /// <summary>Bloqueo frontal de Yunque.</summary>
    Block,

    /// <summary>Muralla de Yunque.</summary>
    Rampart,

    /// <summary>El Celador en fase 1 es inmune a los golpes de frente.</summary>
    FrontImmunity,

    /// <summary>GUARDAR redujo el daño a 0.</summary>
    Guard,
}

public enum StatusEffect
{
    Burning,
    Frozen,
    Stealth,
}

public enum ExtinctionReason
{
    /// <summary>Pasó 3 rondas caído sin que nadie lo levantara.</summary>
    NotRevived,

    /// <summary>Cayó en una grieta.</summary>
    Chasm,
}

public enum EmberReason
{
    EnemySlain,
    WardenSlain,
    ChestOpened,
    AllyRevived,
    StolenFromAlly,
    StolenByAlly,
    Extinction,
}

public sealed record MatchCreated(int PlayerCount) : DomainEvent;

public sealed record DraftOffered(PlayerId Player, ImmutableList<Blessing> Options) : DomainEvent;

public sealed record BlessingChosen(PlayerId Player, Blessing Blessing) : DomainEvent;

public sealed record BlessingCountChanged(PlayerId Player, int Count) : DomainEvent;

public sealed record MatchStarted : DomainEvent;

public sealed record HeroGuarded(PlayerId Player) : DomainEvent;

/// <summary>La entidad estaba congelada y perdió su acción.</summary>
public sealed record ActionLost(EntityRef Entity) : DomainEvent;

public sealed record HeroMoved(PlayerId Player, Position From, Position To) : DomainEvent;

/// <summary>El movimiento rebotó: casilla bloqueada, ocupada o disputada.</summary>
public sealed record MoveBounced(PlayerId Player, Position Attempted) : DomainEvent;

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

public sealed record EnemyKilled(int EnemyId, EnemyKind Kind, PlayerId? Killer) : DomainEvent;

/// <summary>Anuncio (telegraph) de las casillas que el enemigo golpeará la ronda siguiente.</summary>
public sealed record AttackTelegraphed(int? EnemyId, ImmutableList<Position> Tiles) : DomainEvent;

public sealed record WardenEnraged(int EnemyId) : DomainEvent;

public sealed record TotemExtinguished(int EnemyId) : DomainEvent;

public sealed record FireStarted(Position Position, int Rounds) : DomainEvent;

public sealed record BrazierToppled(Position Position) : DomainEvent;

public sealed record TrapTriggered(Position Position) : DomainEvent;

public sealed record RoomEntered(int RoomIndex) : DomainEvent;

public sealed record PactRevealed(PlayerId Player, PactKind Pact, bool Fulfilled) : DomainEvent;

public sealed record MatchEnded(GameOutcome Outcome) : DomainEvent;

public sealed record RoundResolved(int ResolvedRound) : DomainEvent;
