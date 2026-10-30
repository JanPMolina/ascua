using System.Security.Claims;
using Ascua.Application.Abstractions;
using Ascua.Application.Matches;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Ascua.Api.Realtime;

/// <summary>
/// Hub de partidas <c>/realtime/match</c>. Contrato: contracts/asyncapi.yaml. La conexión llega ya
/// autenticada por un ticket de un solo uso atado a usuario y partida (<see cref="TicketAuthentication"/>).
/// Cada método valida el mensaje antes del dominio y responde su ACK o un <c>InvocationError</c> tipado.
/// </summary>
[Authorize(AuthenticationSchemes = TicketAuthentication.SchemeName)]
public sealed class MatchHub(
    MatchEngine engine,
    IMatchHotState hot,
    RealtimeConnections connections,
    MatchMetrics metrics,
    IClock clock,
    ILogger<MatchHub> logger) : Hub
{
    public const string Path = "/realtime/match";

    private const string JoinedKey = "ascua:joined";

    public static string Group(Guid matchId) => $"match:{matchId:D}";

    private Guid UserId => Guid.Parse(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private Guid TicketMatchId => Guid.Parse(Context.User!.FindFirstValue(TicketAuthentication.MatchClaim)!);

    private Guid? JoinedMatch => Context.Items.TryGetValue(JoinedKey, out var value) ? value as Guid? : null;

    public override Task OnConnectedAsync()
    {
        connections.Register(Context);
        metrics.ConnectionOpened();
        return base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Unregister(Context.ConnectionId);
        metrics.ConnectionClosed();
        if (JoinedMatch is { } matchId)
        {
            await engine.DisconnectAsync(matchId, UserId, Context.ConnectionId, CancellationToken.None);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task<object> JoinMatch(MatchRequest request)
    {
        if (RealtimeValidation.MatchId(request?.MatchId, out var matchId) is { } invalid)
        {
            return RealtimeReplies.Error(invalid);
        }

        if (matchId != TicketMatchId)
        {
            return RealtimeReplies.Error(RealtimeErrors.NotParticipant, "El ticket de esta conexión es de otra partida.");
        }

        using var scope = Scope(matchId);
        var joined = await engine.JoinAsync(matchId, UserId, Context.ConnectionId, Context.ConnectionAborted);
        if (joined.Error is { } error)
        {
            return RealtimeReplies.Error(error);
        }

        var (snapshot, participant, replaced) = joined.Value;
        Context.Items[JoinedKey] = matchId;
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(matchId), Context.ConnectionAborted);
        if (replaced is not null)
        {
            await connections.AbortAsync(replaced, "SESSION_REPLACED");
        }

        logger.LogInformation("Jugador unido a la partida (asiento {Seat})", participant.Seat + 1);
        var message = await SnapshotAsync(snapshot, participant);
        await PushDraftOfferAsync(snapshot, participant);
        return new JoinMatchAck
        {
            MatchId = RealtimeMapper.Id(matchId),
            PlayerId = RealtimeMapper.Id(participant.UserId),
            Seat = participant.Seat + 1,
            Snapshot = message,
        };
    }

    public async Task<object> SubmitOrder(SubmitOrderRequest request)
    {
        if (RealtimeValidation.SubmitOrder(request, out var matchId, out var clientActionId, out var order) is { } invalid)
        {
            return RealtimeReplies.Error(invalid);
        }

        if (NotJoined(matchId) is { } notJoined)
        {
            return notJoined;
        }

        using var scope = Scope(matchId, request.Round);
        var result = await engine.SubmitOrderAsync(matchId, UserId, request.Round, clientActionId, order!, Context.ConnectionAborted);
        if (result.Error is { } error)
        {
            return RealtimeReplies.Error(error);
        }

        var (ack, duplicate) = result.Value;
        return new SubmitOrderAck
        {
            MatchId = RealtimeMapper.Id(ack.MatchId),
            Round = ack.Round,
            ClientActionId = RealtimeMapper.Id(ack.ClientActionId),
            Result = duplicate ? "DUPLICATE_ACTION" : "ACCEPTED",
            ReceivedAt = RealtimeMapper.Timestamp(ack.ReceivedAt),
        };
    }

    public async Task<object> GetSnapshot(MatchRequest request)
    {
        if (RealtimeValidation.MatchId(request?.MatchId, out var matchId) is { } invalid)
        {
            return RealtimeReplies.Error(invalid);
        }

        if (NotJoined(matchId) is { } notJoined)
        {
            return notJoined;
        }

        var found = await engine.GetForPlayerAsync(matchId, UserId, Context.ConnectionAborted);
        if (found.Error is { } error)
        {
            return RealtimeReplies.Error(error);
        }

        var (snapshot, participant) = found.Value;
        var message = await SnapshotAsync(snapshot, participant);
        await PushDraftOfferAsync(snapshot, participant);
        return message;
    }

    public async Task<object> LeaveMatch(MatchRequest request)
    {
        if (RealtimeValidation.MatchId(request?.MatchId, out var matchId) is { } invalid)
        {
            return RealtimeReplies.Error(invalid);
        }

        if (NotJoined(matchId) is { } notJoined)
        {
            return notJoined;
        }

        using var scope = Scope(matchId);
        var result = await engine.RetireAsync(matchId, UserId, Context.ConnectionAborted);
        if (result.Error is { } error)
        {
            return RealtimeReplies.Error(error);
        }

        // Ya no está en la partida: al desconectarse no se abre gracia.
        Context.Items.Remove(JoinedKey);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(matchId), Context.ConnectionAborted);
        return new LeaveMatchAck
        {
            MatchId = RealtimeMapper.Id(matchId),
            LeftAt = RealtimeMapper.Timestamp(clock.UtcNow),
            Abandoned = result.Value,
        };
    }

    public async Task<object> DraftPick(DraftPickRequest request)
    {
        if (RealtimeValidation.DraftPick(request, out var matchId, out var offerId, out var blessing) is { } invalid)
        {
            return RealtimeReplies.Error(invalid);
        }

        if (NotJoined(matchId) is { } notJoined)
        {
            return notJoined;
        }

        using var scope = Scope(matchId);
        var result = await engine.DraftPickAsync(matchId, UserId, offerId, blessing, Context.ConnectionAborted);
        if (result.Error is { } error)
        {
            return RealtimeReplies.Error(error);
        }

        return new DraftPickAck
        {
            MatchId = RealtimeMapper.Id(matchId),
            OfferId = RealtimeMapper.Id(offerId),
            BlessingId = RealtimeMapper.BlessingId(result.Value!.Blessing),
            AcceptedAt = RealtimeMapper.Timestamp(result.Value.AcceptedAt),
        };
    }

    private IDictionary<string, object>? NotJoined(Guid matchId) =>
        JoinedMatch == matchId ? null : RealtimeReplies.Error(RealtimeErrors.NotJoined, "Primero hay que llamar a JoinMatch en esta conexión.");

    private async Task<SnapshotMessage> SnapshotAsync(MatchSnapshot snapshot, MatchParticipant participant)
    {
        var context = await SignalRMatchNotifier.BuildContextAsync(hot, snapshot, Context.ConnectionAborted);
        return RealtimeMapper.Snapshot(snapshot, participant, context, clock.UtcNow);
    }

    /// <summary>La oferta de draft pendiente se reenvía al (re)conectar: el cliente pudo perder la original.</summary>
    private Task PushDraftOfferAsync(MatchSnapshot snapshot, MatchParticipant participant) =>
        RealtimeMapper.DraftOffer(snapshot, participant, clock.UtcNow, snapshot.RoundDuration) is { } offer
            ? Clients.Caller.SendAsync("DraftOffer", offer, Context.ConnectionAborted)
            : Task.CompletedTask;

    private IDisposable? Scope(Guid matchId, int? round = null)
    {
        var state = new Dictionary<string, object> { ["MatchId"] = matchId, ["PlayerId"] = UserId };
        if (round is { } r)
        {
            state["Round"] = r;
        }

        return logger.BeginScope(state);
    }
}
