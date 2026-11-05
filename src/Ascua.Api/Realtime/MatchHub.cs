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
    RealtimeOptions options,
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

    /// <summary>Elige camino tras extinguirse (contrato 1.3.0): <c>FRIENDSHIP</c> (Brasa errante) o <c>VENGEANCE</c> (Renegado).</summary>
    public async Task<object> ChoosePath(ChoosePathRequest request)
    {
        if (RealtimeValidation.ChoosePath(request, out var matchId, out var offerId, out var path) is { } invalid)
        {
            return RealtimeReplies.Error(invalid);
        }

        if (NotJoined(matchId) is { } notJoined)
        {
            return notJoined;
        }

        using var scope = Scope(matchId);
        var result = await engine.ChoosePathAsync(matchId, UserId, offerId, path, Context.ConnectionAborted);
        if (result.Error is { } error)
        {
            return RealtimeReplies.Error(error);
        }

        var (ack, duplicate) = result.Value;
        return new ChoosePathAck
        {
            MatchId = RealtimeMapper.Id(matchId),
            OfferId = RealtimeMapper.Id(ack.OfferId),
            Path = RealtimeMapper.Path(ack.Path),
            Result = duplicate ? "DUPLICATE_ACTION" : "ACCEPTED",
            AcceptedAt = RealtimeMapper.Timestamp(ack.AcceptedAt),
        };
    }

    /// <summary>Elige 1 de las 2 misiones (contrato 3.0.0).</summary>
    public async Task<object> ChoosePact(ChoosePactRequest request)
    {
        if (RealtimeValidation.ChoosePact(request, out var matchId, out var offerId, out var pact) is { } invalid)
        {
            return RealtimeReplies.Error(invalid);
        }

        if (NotJoined(matchId) is { } notJoined)
        {
            return notJoined;
        }

        using var scope = Scope(matchId);
        var result = await engine.ChoosePactAsync(matchId, UserId, offerId, pact, Context.ConnectionAborted);
        if (result.Error is { } error)
        {
            return RealtimeReplies.Error(error);
        }

        var ack = result.Value!;
        return new ChoosePactAck
        {
            MatchId = RealtimeMapper.Id(matchId),
            OfferId = RealtimeMapper.Id(ack.OfferId),
            PactId = RealtimeMapper.PactId(ack.Pact),
            Result = ack.Duplicate ? "DUPLICATE_ACTION" : "ACCEPTED",
            AcceptedAt = RealtimeMapper.Timestamp(ack.AcceptedAt),
        };
    }

    /// <summary>Voto secreto del interludio (contrato 3.0.0): a quién acusa y qué puerta prefiere.</summary>
    public async Task<object> CastInterludeVote(CastInterludeVoteRequest request)
    {
        if (RealtimeValidation.CastInterludeVote(request, out var matchId, out var offerId, out var accused, out var door) is { } invalid)
        {
            return RealtimeReplies.Error(invalid);
        }

        if (NotJoined(matchId) is { } notJoined)
        {
            return notJoined;
        }

        using var scope = Scope(matchId);
        var result = await engine.CastVoteAsync(matchId, UserId, offerId, accused, door, Context.ConnectionAborted);
        if (result.Error is { } error)
        {
            return RealtimeReplies.Error(error);
        }

        var ack = result.Value!;
        return new CastInterludeVoteAck
        {
            MatchId = RealtimeMapper.Id(matchId),
            OfferId = RealtimeMapper.Id(ack.OfferId),
            Result = ack.Duplicate ? "DUPLICATE_ACTION" : "ACCEPTED",
            AcceptedAt = RealtimeMapper.Timestamp(ack.AcceptedAt),
        };
    }

    /// <summary>Señal rápida a su bando (contrato 2.0.0): frase fija y, si quiere, una casilla. No se guarda.</summary>
    public async Task<object> SendSignal(SendSignalRequest request)
    {
        if (RealtimeValidation.SendSignal(request, out var matchId, out var signal) is { } invalid)
        {
            return RealtimeReplies.Error(invalid);
        }

        if (NotJoined(matchId) is { } notJoined)
        {
            return notJoined;
        }

        using var scope = Scope(matchId);
        var result = await engine.SendSignalAsync(matchId, UserId, signal!, Context.ConnectionAborted);
        if (result.Error is { } error)
        {
            return RealtimeReplies.Error(error, error.Code == RealtimeErrors.RateLimited ? (int)options.SignalInterval.TotalMilliseconds : null);
        }

        return new SendSignalAck { MatchId = RealtimeMapper.Id(matchId), SentAt = RealtimeMapper.Timestamp(result.Value) };
    }

    private IDictionary<string, object>? NotJoined(Guid matchId) =>
        JoinedMatch == matchId ? null : RealtimeReplies.Error(RealtimeErrors.NotJoined, "Primero hay que llamar a JoinMatch en esta conexión.");

    private async Task<SnapshotMessage> SnapshotAsync(MatchSnapshot snapshot, MatchParticipant participant)
    {
        var context = await SignalRMatchNotifier.BuildContextAsync(hot, snapshot, Context.ConnectionAborted);
        return RealtimeMapper.Snapshot(snapshot, participant, context, clock.UtcNow);
    }

    /// <summary>
    /// Las ofertas pendientes (draft y camino) y lo que oye el Campanero se reenvían al (re)conectar o pedir el
    /// snapshot: el cliente pudo perder el original.
    /// </summary>
    private async Task PushDraftOfferAsync(MatchSnapshot snapshot, MatchParticipant participant)
    {
        if (RealtimeMapper.DraftOffer(snapshot, participant, clock.UtcNow, snapshot.RoundDuration) is { } offer)
        {
            await Clients.Caller.SendAsync("DraftOffer", offer, Context.ConnectionAborted);
        }

        if (RealtimeMapper.PathOffer(snapshot, participant, clock.UtcNow) is { } pathOffer)
        {
            await Clients.Caller.SendAsync("PathOffer", pathOffer, Context.ConnectionAborted);
        }

        if (RealtimeMapper.PactOffer(snapshot, participant) is { } pactOffer)
        {
            await Clients.Caller.SendAsync("PactOffer", pactOffer, Context.ConnectionAborted);
        }

        if (RealtimeMapper.InterludeOffer(snapshot, participant) is { } interludeOffer)
        {
            await Clients.Caller.SendAsync("InterludeOffer", interludeOffer, Context.ConnectionAborted);
        }

        if (await engine.LastWordForAsync(snapshot, participant.PlayerId, Context.ConnectionAborted) is { } heard)
        {
            await Clients.Caller.SendAsync("LastWord", RealtimeMapper.LastWord(snapshot.MatchId, heard), Context.ConnectionAborted);
        }
    }

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
