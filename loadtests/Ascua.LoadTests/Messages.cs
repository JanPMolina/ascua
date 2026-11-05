using MessagePack;

namespace Ascua.LoadTests;

// Mensajes del hub (contracts/asyncapi.yaml) con lo mínimo que usa la prueba. Mapas MessagePack con
// claves de texto camelCase, igual que un cliente real; las claves que no se declaran se ignoran.

[MessagePackObject]
public sealed class MatchRequest
{
    [Key("matchId")]
    public string MatchId { get; set; } = "";
}

[MessagePackObject]
public sealed class SubmitOrderRequest
{
    [Key("matchId")]
    public string MatchId { get; set; } = "";

    [Key("round")]
    public int Round { get; set; }

    [Key("clientActionId")]
    public string ClientActionId { get; set; } = "";

    [Key("order")]
    public OrderPayload Order { get; set; } = new();
}

[MessagePackObject]
public sealed class OrderPayload
{
    [Key("type")]
    public string Type { get; set; } = "";

    [Key("direction")]
    public string? Direction { get; set; }

    [Key("step")]
    public string? Step { get; set; }
}

[MessagePackObject]
public sealed class ChoosePactRequest
{
    [Key("matchId")]
    public string MatchId { get; set; } = "";

    [Key("offerId")]
    public string OfferId { get; set; } = "";

    [Key("pactId")]
    public string PactId { get; set; } = "";
}

[MessagePackObject]
public sealed class CastInterludeVoteRequest
{
    [Key("matchId")]
    public string MatchId { get; set; } = "";

    [Key("offerId")]
    public string OfferId { get; set; } = "";

    [Key("accusedId")]
    public string? AccusedId { get; set; }

    [Key("door")]
    public int? Door { get; set; }
}

[MessagePackObject]
public sealed class PactOption
{
    [Key("pactId")]
    public string PactId { get; set; } = "";
}

[MessagePackObject]
public sealed class PactOffer
{
    [Key("offerId")]
    public string OfferId { get; set; } = "";

    [Key("options")]
    public IReadOnlyList<PactOption> Options { get; set; } = [];
}

[MessagePackObject]
public sealed class InterludeDoor
{
    [Key("type")]
    public string Type { get; set; } = "";
}

[MessagePackObject]
public sealed class InterludeOffer
{
    [Key("offerId")]
    public string OfferId { get; set; } = "";

    [Key("doors")]
    public IReadOnlyList<InterludeDoor> Doors { get; set; } = [];

    [Key("canVote")]
    public bool CanVote { get; set; }
}

[MessagePackObject]
public sealed class DraftPickRequest
{
    [Key("matchId")]
    public string MatchId { get; set; } = "";

    [Key("offerId")]
    public string OfferId { get; set; } = "";

    [Key("blessingId")]
    public string BlessingId { get; set; } = "";
}

[MessagePackObject]
public sealed class ChoosePathRequest
{
    [Key("matchId")]
    public string MatchId { get; set; } = "";

    [Key("offerId")]
    public string OfferId { get; set; } = "";

    [Key("path")]
    public string Path { get; set; } = "";
}

[MessagePackObject]
public sealed class ErrorBody
{
    [Key("code")]
    public string Code { get; set; } = "";

    [Key("message")]
    public string Message { get; set; } = "";
}

/// <summary>ACK de <c>JoinMatch</c> o <c>{ error }</c>.</summary>
[MessagePackObject]
public sealed class JoinReply
{
    [Key("playerId")]
    public string? PlayerId { get; set; }

    [Key("snapshot")]
    public SeqOnly? Snapshot { get; set; }

    [Key("error")]
    public ErrorBody? Error { get; set; }
}

/// <summary>ACK de <c>SubmitOrder</c> o <c>{ error }</c>.</summary>
[MessagePackObject]
public sealed class SubmitReply
{
    [Key("result")]
    public string? Result { get; set; }

    [Key("receivedAt")]
    public string? ReceivedAt { get; set; }

    [Key("error")]
    public ErrorBody? Error { get; set; }
}

[MessagePackObject]
public sealed class DraftReply
{
    [Key("error")]
    public ErrorBody? Error { get; set; }
}

/// <summary><c>Snapshot</c> (push o dentro del ACK de Join): solo interesa el seq.</summary>
[MessagePackObject]
public sealed class SeqOnly
{
    [Key("seq")]
    public long Seq { get; set; }
}

[MessagePackObject]
public sealed class RoundOpened
{
    [Key("round")]
    public int Round { get; set; }

    [Key("deadline")]
    public string Deadline { get; set; } = "";

    [Key("expectedPlayers")]
    public int ExpectedPlayers { get; set; }
}

[MessagePackObject]
public sealed class RoundResolved
{
    [Key("round")]
    public int Round { get; set; }

    [Key("previousSeq")]
    public long PreviousSeq { get; set; }

    [Key("seq")]
    public long Seq { get; set; }

    [Key("delta")]
    public RoundDelta Delta { get; set; } = new();
}

[MessagePackObject]
public sealed class RoundDelta
{
    [Key("events")]
    public IReadOnlyList<IDictionary<string, object>> Events { get; set; } = [];
}

[MessagePackObject]
public sealed class DraftOffer
{
    [Key("offerId")]
    public string OfferId { get; set; } = "";

    [Key("options")]
    public IReadOnlyList<string> Options { get; set; } = [];
}

[MessagePackObject]
public sealed class PathOffer
{
    [Key("offerId")]
    public string OfferId { get; set; } = "";

    [Key("options")]
    public IReadOnlyList<string> Options { get; set; } = [];
}

[MessagePackObject]
public sealed class MatchFinished
{
    [Key("outcome")]
    public string Outcome { get; set; } = "";

    [Key("rounds")]
    public int Rounds { get; set; }
}

[MessagePackObject]
public sealed class ErrorPush
{
    [Key("error")]
    public ErrorBody? Error { get; set; }
}
