using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Microsoft.Extensions.Logging;

namespace Ascua.Application.Matches;

/// <summary>Una partida en el historial de un usuario, con su asiento en ella.</summary>
public sealed record MatchHistoryEntry(
    Guid MatchId,
    MatchStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    int Rounds,
    int PlayerCount,
    MatchParticipant Participant,
    GameOutcome? Outcome);

public sealed record MatchHistoryPage(IReadOnlyList<MatchHistoryEntry> Items, int Page, int PageSize, long Total);

/// <summary>
/// Una confirmación guardada: la verdad completa tras ella y los eventos que la produjeron
/// (seq en (<see cref="FromSeq"/>, <c>State.LastSeq</c>]). <see cref="Round"/> 0 = draft inicial.
/// </summary>
public sealed record MatchStep(long Version, int Round, long FromSeq, GameState State, IReadOnlyList<DomainEvent> Events);

/// <summary>Lectura durable del historial y de las confirmaciones de una partida (Postgres).</summary>
public interface IMatchHistoryStore
{
    /// <summary>Partidas del usuario de la más nueva a la más vieja; <paramref name="page"/> empieza en 1.</summary>
    Task<MatchHistoryPage> ListForUserAsync(Guid userId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Cuándo terminó la partida (<c>null</c> si sigue viva o no existe).</summary>
    Task<DateTimeOffset?> GetFinishedAtAsync(Guid matchId, CancellationToken cancellationToken);

    /// <summary>Confirmaciones con ronda en [<paramref name="fromRound"/>, <paramref name="toRound"/>], en orden de versión.</summary>
    Task<IReadOnlyList<MatchStep>> GetStepsAsync(Guid matchId, int fromRound, int toRound, CancellationToken cancellationToken);
}

/// <summary>
/// Registro de los accesos de un administrador a la vista completa de una partida
/// (planteamiento/02-arquitectura.md, sección 4: "esa acción queda auditada").
/// La implementación real (Infrastructure) escribe en <c>audit_log</c>.
/// </summary>
public interface IReplayAccessAudit
{
    Task FullReplayViewedAsync(Guid adminId, Guid matchId, int fromRound, int toRound, CancellationToken cancellationToken);
}

/// <summary>Implementación provisional: deja el acceso en el log estructurado.</summary>
public sealed partial class LoggingReplayAccessAudit(ILogger<LoggingReplayAccessAudit> logger) : IReplayAccessAudit
{
    public Task FullReplayViewedAsync(Guid adminId, Guid matchId, int fromRound, int toRound, CancellationToken cancellationToken)
    {
        LogFullReplay(logger, adminId, matchId, fromRound, toRound);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auditoría: el administrador {AdminId} vio la reproducción completa de la partida {MatchId} (rondas {FromRound}-{ToRound})")]
    private static partial void LogFullReplay(ILogger logger, Guid adminId, Guid matchId, int fromRound, int toRound);
}

/// <summary>Reproducción pedida: la partida, quién la mira (<c>null</c> = vista completa) y sus confirmaciones.</summary>
public sealed record MatchReplay(MatchSnapshot Match, MatchParticipant? Viewer, int FromRound, int ToRound, IReadOnlyList<MatchStep> Steps);

/// <summary>
/// Historial, detalle y reproducción. La reproducción NO vuelve a ejecutar el motor: el azar sale de un
/// CSPRNG sin semilla y las órdenes no se guardan, así que se leen las instantáneas confirmadas
/// (<c>match_states</c>) y sus eventos (<c>match_events</c>). Leer lo confirmado es determinista y
/// muestra exactamente lo que se difundió en vivo; la proyección por jugador la hace la API.
/// </summary>
public sealed class MatchHistoryService(IMatchStore matches, IMatchHistoryStore history, IReplayAccessAudit audit)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    public const int MaxRound = 9999;

    public Task<MatchHistoryPage> ListAsync(Guid userId, int page, int pageSize, CancellationToken cancellationToken) =>
        history.ListForUserAsync(userId, page, pageSize, cancellationToken);

    /// <summary>La partida si <paramref name="userId"/> jugó en ella; si no existe o es ajena, <c>null</c> (no se distingue).</summary>
    public async Task<(MatchSnapshot Match, MatchParticipant Participant)?> GetForParticipantAsync(
        Guid matchId, Guid userId, CancellationToken cancellationToken)
    {
        var match = await matches.LoadAsync(matchId, cancellationToken);
        return match?.FindParticipant(userId) is { } participant ? (match, participant) : null;
    }

    public Task<DateTimeOffset?> FinishedAtAsync(Guid matchId, CancellationToken cancellationToken) =>
        history.GetFinishedAtAsync(matchId, cancellationToken);

    /// <summary>
    /// Reproducción con la vista de <paramref name="requesterId"/> o, con <paramref name="full"/> (solo
    /// administradores: lo verifica quien llama), la vista completa, que queda auditada.
    /// </summary>
    public async Task<MatchReplay?> GetReplayAsync(
        Guid matchId, Guid requesterId, bool full, int fromRound, int? toRound, CancellationToken cancellationToken)
    {
        var match = await matches.LoadAsync(matchId, cancellationToken);
        var viewer = match?.FindParticipant(requesterId);
        if (match is null || (viewer is null && !full))
        {
            return null;
        }

        var to = Math.Min(toRound ?? MaxRound, MaxRound);
        if (full)
        {
            await audit.FullReplayViewedAsync(requesterId, matchId, fromRound, to, cancellationToken);
        }

        var steps = await history.GetStepsAsync(matchId, fromRound, to, cancellationToken);
        return new MatchReplay(match, full ? null : viewer, fromRound, to, steps);
    }
}
