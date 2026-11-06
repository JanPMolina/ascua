using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

public enum ErrorCode
{
    InvalidOrder,
    NotAParticipant,
    PlayerDown,
    PlayerOut,
    StaleRound,
    MatchNotInProgress,
    SkillOnCooldown,
    InvalidDraftChoice,

    /// <summary>No hay una elección de camino abierta (no está extinto, ya eligió o ya se reveló).</summary>
    NoPathOffer,

    InvalidPathChoice,

    /// <summary>No hay una elección de misión abierta.</summary>
    NoPactOffer,
}

/// <summary>Error de dominio tipado. <see cref="Code"/> es estable y es lo que viaja al cliente.</summary>
public sealed record DomainError(ErrorCode Kind, string Message)
{
    public string Code => Kind switch
    {
        ErrorCode.InvalidOrder => "INVALID_ORDER",
        ErrorCode.NotAParticipant => "NOT_A_PARTICIPANT",
        ErrorCode.PlayerDown => "PLAYER_DOWN",
        ErrorCode.PlayerOut => "PLAYER_OUT",
        ErrorCode.StaleRound => "STALE_ROUND",
        ErrorCode.MatchNotInProgress => "MATCH_NOT_IN_PROGRESS",
        ErrorCode.SkillOnCooldown => "SKILL_ON_COOLDOWN",
        ErrorCode.InvalidDraftChoice => "INVALID_DRAFT_CHOICE",
        ErrorCode.NoPathOffer => "NO_PATH_OFFER",
        ErrorCode.InvalidPathChoice => "INVALID_PATH_CHOICE",
        ErrorCode.NoPactOffer => "NO_PACT_OFFER",
        _ => "UNKNOWN",
    };
}

/// <summary>Resultado de un comando de dominio distinto de la resolución de ronda.</summary>
public sealed record CommandResult(GameState State, IReadOnlyList<DomainEvent> Events, DomainError? Error)
{
    public bool IsSuccess => Error is null;

    public static CommandResult Ok(GameState state, IReadOnlyList<DomainEvent> events) => new(state, events, null);

    public static CommandResult Fail(GameState state, ErrorCode kind, string message) =>
        new(state, [], new DomainError(kind, message));
}
