using System.Diagnostics;
using System.Text.RegularExpressions;
using Ascua.Application.Matches;
using Ascua.Domain.Model;

namespace Ascua.Api.Realtime;

/// <summary>Respuestas de error del contrato: <c>InvocationError</c> y el mensaje <c>Error</c>.</summary>
public static class RealtimeReplies
{
    /// <summary><c>{ error: { code, message, retryAfterMs?, details? } }</c>; las claves opcionales se omiten.</summary>
    public static IDictionary<string, object> Error(MatchError error, int? retryAfterMs = null) =>
        new Dictionary<string, object> { ["error"] = Body(error, retryAfterMs) };

    public static IDictionary<string, object> Error(string code, string message, int? retryAfterMs = null) =>
        Error(new MatchError(code, message), retryAfterMs);

    public static IDictionary<string, object> Push(Guid matchId, MatchError error) =>
        new Dictionary<string, object> { ["matchId"] = RealtimeMapper.Id(matchId), ["error"] = Body(error, null) };

    private static Dictionary<string, object> Body(MatchError error, int? retryAfterMs)
    {
        ArgumentNullException.ThrowIfNull(error);
        var body = new Dictionary<string, object>
        {
            ["code"] = error.Code,
            ["message"] = error.Message.Length > 200 ? error.Message[..200] : error.Message,
        };
        if (retryAfterMs is { } retry)
        {
            body["retryAfterMs"] = Math.Clamp(retry, 0, 60000);
        }

        var details = new Dictionary<string, object>();
        if (error.Field is { } field)
        {
            details["field"] = field;
        }

        if (error.CurrentRound is { } round)
        {
            details["currentRound"] = Math.Clamp(round, 1, RealtimeMapper.MaxRound);
        }

        if (error.Status is { } status)
        {
            details["status"] = status.ToCode();
        }

        if (error.Code == RealtimeErrors.InternalError && Activity.Current is { } activity)
        {
            details["traceId"] = activity.TraceId.ToHexString();
        }

        if (details.Count > 0)
        {
            body["details"] = details;
        }

        return body;
    }
}

/// <summary>
/// Validación de esquema, rangos y tamaño de cada mensaje entrante, antes de tocar el dominio
/// (los esquemas *Request de contracts/asyncapi.yaml). Un mensaje inválido devuelve INVALID_PAYLOAD.
/// </summary>
public static partial class RealtimeValidation
{
    public static MatchError? MatchId(string? value, out Guid matchId) => Uuid(value, "matchId", out matchId);

    public static MatchError? SubmitOrder(SubmitOrderRequest? request, out Guid matchId, out Guid clientActionId, out Order? order)
    {
        clientActionId = Guid.Empty;
        order = null;
        if (request is null)
        {
            matchId = Guid.Empty;
            return Invalid("payload", "Falta el cuerpo del mensaje.");
        }

        var error = Uuid(request.MatchId, "matchId", out matchId) ?? Uuid(request.ClientActionId, "clientActionId", out clientActionId);
        if (error is not null)
        {
            return error;
        }

        if (request.Round < 1 || request.Round > RealtimeMapper.MaxRound)
        {
            return Invalid("round", $"round debe estar entre 1 y {RealtimeMapper.MaxRound}.");
        }

        if (request.Order is null || RealtimeMapper.ParseOrderType(request.Order.Type) is not { } type)
        {
            return Invalid("order.type", "order.type debe ser MOVE, ATTACK, ABILITY, INTERACT, PUSH o GUARD.");
        }

        Direction? step = null;
        if (request.Order.Step is not null)
        {
            if (type is OrderType.Guard or OrderType.Move)
            {
                return Invalid("order.step", "GUARD y MOVE no llevan paso.");
            }

            if (RealtimeMapper.ParseDirection(request.Order.Step) is not { } parsedStep)
            {
                return Invalid("order.step", "order.step debe ser N, S, E o W.");
            }

            step = parsedStep;
        }

        if (type == OrderType.Guard)
        {
            if (request.Order.Direction is not null)
            {
                return Invalid("order.direction", "GUARD no lleva dirección.");
            }

            order = Order.DefaultGuard;
            return null;
        }

        if (RealtimeMapper.ParseDirection(request.Order.Direction) is not { } direction)
        {
            return Invalid("order.direction", "order.direction debe ser N, S, E o W.");
        }

        order = new Order(type, direction) { Step = step };
        return null;
    }

    public static MatchError? ChoosePact(ChoosePactRequest? request, out Guid matchId, out Guid offerId, out PactKind pact)
    {
        offerId = Guid.Empty;
        pact = default;
        if (request is null)
        {
            matchId = Guid.Empty;
            return Invalid("payload", "Falta el cuerpo del mensaje.");
        }

        var error = Uuid(request.MatchId, "matchId", out matchId) ?? Uuid(request.OfferId, "offerId", out offerId);
        if (error is not null)
        {
            return error;
        }

        if (RealtimeMapper.ParsePact(request.PactId) is not { } parsed)
        {
            return Invalid("pactId", "pactId no es una misión válida.");
        }

        pact = parsed;
        return null;
    }

    public static MatchError? SendSignal(SendSignalRequest? request, out Guid matchId, out QuickSignal? signal)
    {
        signal = null;
        if (request is null)
        {
            matchId = Guid.Empty;
            return Invalid("payload", "Falta el cuerpo del mensaje.");
        }

        if (Uuid(request.MatchId, "matchId", out matchId) is { } error)
        {
            return error;
        }

        if (RealtimeMapper.ParseSignal(request.Kind) is not { } kind)
        {
            return Invalid("kind", "kind debe ser HERE, DANGER, HELP o MINE.");
        }

        // La casilla la elige quien envía: solo se comprueba que esté en el tablero.
        Position? tile = null;
        if (request.Position is { } position)
        {
            tile = new Position(position.X, position.Y);
            if (!tile.Value.IsInside(Room.LargeSize, Room.LargeSize))
            {
                return Invalid("position", "position debe estar dentro de la sala (0-10).");
            }
        }

        signal = new QuickSignal(kind, tile);
        return null;
    }

    public static MatchError? DraftPick(DraftPickRequest? request, out Guid matchId, out Guid offerId, out Blessing blessing)
    {
        offerId = Guid.Empty;
        blessing = default;
        if (request is null)
        {
            matchId = Guid.Empty;
            return Invalid("payload", "Falta el cuerpo del mensaje.");
        }

        var error = Uuid(request.MatchId, "matchId", out matchId) ?? Uuid(request.OfferId, "offerId", out offerId);
        if (error is not null)
        {
            return error;
        }

        if (RealtimeMapper.ParseBlessing(request.BlessingId) is not { } parsed)
        {
            return Invalid("blessingId", "blessingId no es una mejora conocida.");
        }

        blessing = parsed;
        return null;
    }

    public static MatchError? ChoosePath(ChoosePathRequest? request, out Guid matchId, out Guid offerId, out PathKind path)
    {
        offerId = Guid.Empty;
        path = default;
        if (request is null)
        {
            matchId = Guid.Empty;
            return Invalid("payload", "Falta el cuerpo del mensaje.");
        }

        var error = Uuid(request.MatchId, "matchId", out matchId) ?? Uuid(request.OfferId, "offerId", out offerId);
        if (error is not null)
        {
            return error;
        }

        if (RealtimeMapper.ParsePath(request.Path) is not { } parsed)
        {
            return Invalid("path", "path debe ser FRIENDSHIP o VENGEANCE.");
        }

        path = parsed;
        return null;
    }

    private static MatchError? Uuid(string? value, string field, out Guid id)
    {
        id = Guid.Empty;
        if (value is null || !UuidPattern().IsMatch(value) || !Guid.TryParseExact(value, "D", out id))
        {
            return Invalid(field, $"{field} debe ser un UUID en minúsculas.");
        }

        return null;
    }

    private static MatchError Invalid(string field, string message) =>
        new(RealtimeErrors.InvalidPayload, message) { Field = field };

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex UuidPattern();
}
