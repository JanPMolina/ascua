using Ascua.Application.Abstractions;
using Ascua.Application.Matches;
using Ascua.Domain.Model;
using Microsoft.AspNetCore.SignalR;

namespace Ascua.Api.Realtime;

/// <summary>
/// Difusión por SignalR (con backplane de Redis entre réplicas). Lo público (decisiones, rondas
/// abiertas, conexión, resultado) va al grupo de la partida; las vistas van a cada usuario con
/// <c>Clients.User</c> y se proyectan por jugador: nunca se difunde el estado completo.
/// </summary>
public sealed class SignalRMatchNotifier(
    IHubContext<MatchHub> hub,
    IMatchHotState hot,
    IClock clock,
    RealtimeOptions options) : IMatchNotifier
{
    public Task PlayerDecidedAsync(MatchSnapshot snapshot, Guid userId, int decidedCount, int expectedPlayers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return hub.Clients.Group(MatchHub.Group(snapshot.MatchId)).SendAsync(
            "PlayerDecided",
            new PlayerDecidedMessage
            {
                MatchId = RealtimeMapper.Id(snapshot.MatchId),
                Round = snapshot.State.Round,
                PlayerId = RealtimeMapper.Id(userId),
                DecidedCount = Math.Clamp(decidedCount, 1, 4),
                ExpectedPlayers = Math.Clamp(expectedPlayers, 1, 4),
            },
            cancellationToken);
    }

    public async Task MatchChangedAsync(MatchChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var (before, after) = (change.Before, change.After);
        var now = clock.UtcNow;
        var context = await ContextAsync(after, cancellationToken);

        var sends = new List<Task>();
        foreach (var participant in after.Participants)
        {
            var user = hub.Clients.User(RealtimeMapper.Id(participant.UserId));
            if (change.RoundResolved)
            {
                var message = RealtimeMapper.RoundResolved(
                    before, after, change.Events, change.DefaultGuards.Contains(participant.PlayerId), participant, context);
                sends.Add(user.SendAsync("RoundResolved", message, cancellationToken));
            }
            else
            {
                sends.Add(user.SendAsync("Snapshot", RealtimeMapper.Snapshot(after, participant, context, now), cancellationToken));
            }

            if (IsNewOffer(before, after, participant.PlayerId)
                && RealtimeMapper.DraftOffer(after, participant, now, after.RoundDuration) is { } offer)
            {
                sends.Add(user.SendAsync("DraftOffer", offer, cancellationToken));
            }
        }

        await Task.WhenAll(sends);

        var group = hub.Clients.Group(MatchHub.Group(after.MatchId));
        if (after.Status == MatchStatus.InProgress && after.Deadline is { } deadline
            && (before.Status != MatchStatus.InProgress || before.State.Round != after.State.Round))
        {
            await group.SendAsync(
                "RoundOpened",
                new RoundOpenedMessage
                {
                    MatchId = RealtimeMapper.Id(after.MatchId),
                    Round = after.State.Round,
                    Deadline = RealtimeMapper.Timestamp(deadline),
                    DurationMs = (int)Math.Clamp(after.RoundDuration.TotalMilliseconds, 1000, 60000),
                    ExpectedPlayers = Math.Clamp(after.ActivePlayers().Count, 1, 4),
                },
                cancellationToken);
        }

        if (!after.Status.IsActive())
        {
            await group.SendAsync("MatchFinished", RealtimeMapper.Finished(after, options.PublicBaseUrl, now), cancellationToken);
        }
    }

    public Task PlayerConnectionChangedAsync(
        Guid matchId, Guid userId, ConnectionState state, DateTimeOffset? graceDeadline, CancellationToken cancellationToken) =>
        hub.Clients.Group(MatchHub.Group(matchId)).SendAsync(
            "PlayerConnectionChanged",
            new PlayerConnectionChangedMessage
            {
                MatchId = RealtimeMapper.Id(matchId),
                PlayerId = RealtimeMapper.Id(userId),
                State = state switch
                {
                    ConnectionState.Connected => "CONNECTED",
                    ConnectionState.Disconnected => "DISCONNECTED",
                    _ => "ABANDONED",
                },
                GraceDeadline = state == ConnectionState.Disconnected && graceDeadline is { } grace ? RealtimeMapper.Timestamp(grace) : null,
            },
            cancellationToken);

    public async Task MatchStoppedAsync(MatchSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var group = hub.Clients.Group(MatchHub.Group(snapshot.MatchId));
        var message = snapshot.Status == MatchStatus.Faulted
            ? "La partida quedó en estado FAULTED y se detuvo."
            : "La partida expiró por inactividad.";
        await group.SendAsync(
            "Error",
            RealtimeReplies.Push(snapshot.MatchId, new MatchError(RealtimeErrors.MatchNotActive, message) { Status = snapshot.Status }),
            cancellationToken);

        if (snapshot.Status == MatchStatus.Faulted)
        {
            await group.SendAsync("MatchFinished", RealtimeMapper.Finished(snapshot, options.PublicBaseUrl, clock.UtcNow), cancellationToken);
        }
    }

    /// <summary>Quién está conectado y qué órdenes hay en la ronda abierta (para <c>decided</c> y <c>pendingOrder</c>).</summary>
    public static async Task<ViewContext> BuildContextAsync(IMatchHotState hot, MatchSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hot);
        ArgumentNullException.ThrowIfNull(snapshot);
        var connected = await hot.ConnectedUsersAsync(snapshot.MatchId, cancellationToken);
        var orders = snapshot.Status == MatchStatus.InProgress
            ? await hot.GetOrdersAsync(snapshot.MatchId, snapshot.State.Round, cancellationToken)
            : new Dictionary<PlayerId, Order>();
        return new ViewContext(connected, orders);
    }

    private Task<ViewContext> ContextAsync(MatchSnapshot snapshot, CancellationToken cancellationToken) =>
        BuildContextAsync(hot, snapshot, cancellationToken);

    private static bool IsNewOffer(MatchSnapshot before, MatchSnapshot after, PlayerId player)
    {
        var now = after.State.FindHero(player)?.DraftOptions ?? [];
        var previous = before.State.FindHero(player)?.DraftOptions ?? [];
        return !now.IsEmpty && !now.SequenceEqual(previous);
    }
}
