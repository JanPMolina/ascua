using Ascua.Application.Abstractions;
using Ascua.Application.Matches;
using Ascua.Application.Rooms;
using Ascua.Domain.Model;
using Microsoft.Extensions.Logging;

namespace Ascua.Application.Matchmaking;

/// <summary>Casos de uso REST de la cola: entrar, salir y consultar el estado.</summary>
public sealed class MatchmakingService(IMatchmakingQueue queue, IClock clock)
{
    public async Task<LobbyResult<QueueStatus>> EnqueueAsync(
        Guid userId, string username, HeroClass? preferredHero, CancellationToken cancellationToken)
    {
        var error = await queue.EnqueueAsync(new QueueTicket(userId, username, preferredHero, clock.UtcNow), cancellationToken);
        return error is { } e ? e : await queue.GetStatusAsync(userId, cancellationToken);
    }

    public async Task<LobbyResult<bool>> LeaveAsync(Guid userId, CancellationToken cancellationToken)
    {
        var error = await queue.LeaveAsync(userId, cancellationToken);
        return error is { } e ? e : true;
    }

    public Task<QueueStatus> GetStatusAsync(Guid userId, CancellationToken cancellationToken) =>
        queue.GetStatusAsync(userId, cancellationToken);
}

/// <summary>
/// Una pasada del agrupador: forma grupos de forma atómica, lanza sus partidas y saca de la cola a quienes
/// esperaron demasiado. Puede correr a la vez en varias réplicas: la atomicidad está en <see cref="IMatchmakingQueue"/>
/// y <see cref="IMatchLauncher"/> es idempotente por id de grupo.
/// </summary>
public sealed partial class MatchmakingProcessor(
    IMatchmakingQueue queue,
    IMatchLauncher launcher,
    MatchmakingPolicy policy,
    LobbyPolicy lobby,
    IClock clock,
    ILogger<MatchmakingProcessor> logger)
{
    /// <summary>Un grupo sin completar durante este tiempo se da por huérfano (réplica caída) y se reintenta.</summary>
    internal static readonly TimeSpan PendingRetryAfter = TimeSpan.FromSeconds(15);

    /// <summary>Tope de grupos por pasada, para no acaparar el ciclo si la cola es enorme.</summary>
    private const int MaxGroupsPerPass = 50;

    /// <summary>Devuelve cuántas partidas se lanzaron en esta pasada.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var launched = 0;
        foreach (var orphan in await queue.GetStalePendingGroupsAsync(clock.UtcNow - PendingRetryAfter, cancellationToken))
        {
            LogRetryingOrphan(orphan.GroupId);
            var outcome = await LaunchAsync(orphan, cancellationToken);
            if (outcome == LaunchOutcome.Unavailable)
            {
                return launched;
            }

            if (outcome == LaunchOutcome.Launched)
            {
                launched++;
            }
        }

        for (var i = 0; i < MaxGroupsPerPass; i++)
        {
            var group = await queue.TryFormGroupAsync(clock.UtcNow, policy.MaxWait, Guid.NewGuid(), cancellationToken);
            if (group is null)
            {
                break;
            }

            var outcome = await LaunchAsync(group, cancellationToken);
            if (outcome == LaunchOutcome.Unavailable)
            {
                return launched;
            }

            if (outcome == LaunchOutcome.Launched)
            {
                launched++;
            }
        }

        var expired = await queue.ExpireAsync(clock.UtcNow - policy.QueueTimeout, cancellationToken);
        if (expired > 0)
        {
            LogExpired(expired);
        }

        return launched;
    }

    private async Task<LaunchOutcome> LaunchAsync(MatchGroup group, CancellationToken cancellationToken)
    {
        var heroes = HeroAssignment.Assign([.. group.Players.Select(p => p.PreferredHero)]);
        var seats = group.Players.Select((p, i) => new MatchSeat(p.UserId, heroes[i])).ToList();
        Guid matchId;
        try
        {
            matchId = await launcher.LaunchAsync(new LaunchMatchRequest(group.GroupId, seats), cancellationToken);
        }
        catch (MatchLaunchUnavailableException)
        {
            await queue.AbortGroupAsync(group.GroupId, CancellationToken.None);
            LogUnavailable(group.Players.Count);
            return LaunchOutcome.Unavailable;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await queue.AbortGroupAsync(group.GroupId, CancellationToken.None);
            LogLaunchFailed(ex, group.GroupId);
            return LaunchOutcome.Failed;
        }

        await queue.CompleteGroupAsync(group.GroupId, matchId, lobby.ActiveMatchTtl, CancellationToken.None);
        LogMatched(group.GroupId, matchId, seats.Count);
        return LaunchOutcome.Launched;
    }

    private enum LaunchOutcome
    {
        Launched,
        Failed,
        Unavailable,
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Emparejamiento: grupo {GroupId} → partida {MatchId} con {Players} jugadores")]
    private partial void LogMatched(Guid groupId, Guid matchId, int players);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Emparejamiento: el servicio de partidas no está disponible; {Players} jugadores vuelven a la cola")]
    private partial void LogUnavailable(int players);

    [LoggerMessage(Level = LogLevel.Error, Message = "Emparejamiento: no se pudo lanzar el grupo {GroupId}; vuelve a la cola")]
    private partial void LogLaunchFailed(Exception exception, Guid groupId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Emparejamiento: reintentando el grupo huérfano {GroupId}")]
    private partial void LogRetryingOrphan(Guid groupId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Emparejamiento: {Count} jugadores salieron de la cola por tiempo de espera")]
    private partial void LogExpired(int count);
}
