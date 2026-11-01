using MessagePack;

namespace Ascua.Api.Realtime;

// Mensajes del hub tal como los define contracts/asyncapi.yaml. En MessagePack se serializan como
// mapas con claves de texto en camelCase (atributos Key); con el protocolo JSON (solo local/dev)
// SignalR ya usa camelCase. Los enums viajan como texto con los nombres del contrato.
// Los objetos con campos opcionales que el contrato no admite como null (errores, órdenes, eventos
// visibles) se envían como diccionarios para poder omitir las claves.

// ───────────── Cliente → servidor ─────────────

/// <summary>Cuerpo de <c>JoinMatch</c>, <c>GetSnapshot</c> y <c>LeaveMatch</c>.</summary>
[MessagePackObject]
public sealed class MatchRequest
{
    [Key("matchId")]
    public string? MatchId { get; set; }
}

[MessagePackObject]
public sealed class SubmitOrderRequest
{
    [Key("matchId")]
    public string? MatchId { get; set; }

    [Key("round")]
    public int Round { get; set; }

    [Key("clientActionId")]
    public string? ClientActionId { get; set; }

    [Key("order")]
    public OrderPayload? Order { get; set; }
}

[MessagePackObject]
public sealed class OrderPayload
{
    [Key("type")]
    public string? Type { get; set; }

    [Key("direction")]
    public string? Direction { get; set; }
}

[MessagePackObject]
public sealed class DraftPickRequest
{
    [Key("matchId")]
    public string? MatchId { get; set; }

    [Key("offerId")]
    public string? OfferId { get; set; }

    [Key("blessingId")]
    public string? BlessingId { get; set; }
}

[MessagePackObject]
public sealed class ChoosePathRequest
{
    [Key("matchId")]
    public string? MatchId { get; set; }

    [Key("offerId")]
    public string? OfferId { get; set; }

    /// <summary><c>FRIENDSHIP</c> o <c>VENGEANCE</c>.</summary>
    [Key("path")]
    public string? Path { get; set; }
}

// ───────────── ACK ─────────────

[MessagePackObject]
public sealed class JoinMatchAck
{
    [Key("matchId")]
    public string MatchId { get; set; } = "";

    [Key("playerId")]
    public string PlayerId { get; set; } = "";

    [Key("seat")]
    public int Seat { get; set; }

    [Key("snapshot")]
    public SnapshotMessage Snapshot { get; set; } = new();
}

[MessagePackObject]
public sealed class SubmitOrderAck
{
    [Key("matchId")]
    public string MatchId { get; set; } = "";

    [Key("round")]
    public int Round { get; set; }

    [Key("clientActionId")]
    public string ClientActionId { get; set; } = "";

    /// <summary><c>ACCEPTED</c> o <c>DUPLICATE_ACTION</c>.</summary>
    [Key("result")]
    public string Result { get; set; } = "";

    [Key("receivedAt")]
    public string ReceivedAt { get; set; } = "";
}

[MessagePackObject]
public sealed class LeaveMatchAck
{
    [Key("matchId")]
    public string MatchId { get; set; } = "";

    [Key("leftAt")]
    public string LeftAt { get; set; } = "";

    [Key("abandoned")]
    public bool Abandoned { get; set; }
}

[MessagePackObject]
public sealed class DraftPickAck
{
    [Key("matchId")]
    public string MatchId { get; set; } = "";

    [Key("offerId")]
    public string OfferId { get; set; } = "";

    [Key("blessingId")]
    public string BlessingId { get; set; } = "";

    [Key("acceptedAt")]
    public string AcceptedAt { get; set; } = "";
}

[MessagePackObject]
public sealed class ChoosePathAck
{
    [Key("matchId")]
    public string MatchId { get; set; } = "";

    [Key("offerId")]
    public string OfferId { get; set; } = "";

    [Key("path")]
    public string Path { get; set; } = "";

    /// <summary><c>ACCEPTED</c> o <c>DUPLICATE_ACTION</c>.</summary>
    [Key("result")]
    public string Result { get; set; } = "";

    [Key("acceptedAt")]
    public string AcceptedAt { get; set; } = "";
}

// ───────────── Vista del jugador ─────────────

[MessagePackObject]
public sealed class PositionDto
{
    [Key("x")]
    public int X { get; set; }

    [Key("y")]
    public int Y { get; set; }
}

[MessagePackObject]
public sealed class PactDto
{
    [Key("pactId")]
    public string PactId { get; set; } = "";

    [Key("description")]
    public string Description { get; set; } = "";
}

[MessagePackObject]
public sealed class SelfHeroDto
{
    [Key("playerId")]
    public string PlayerId { get; set; } = "";

    [Key("displayName")]
    public string DisplayName { get; set; } = "";

    [Key("hero")]
    public string Hero { get; set; } = "";

    [Key("state")]
    public string State { get; set; } = "";

    [Key("hp")]
    public int Hp { get; set; }

    [Key("maxHp")]
    public int MaxHp { get; set; }

    [Key("shield")]
    public int Shield { get; set; }

    [Key("position")]
    public PositionDto Position { get; set; } = new();

    [Key("facing")]
    public string Facing { get; set; } = "";

    [Key("embers")]
    public int Embers { get; set; }

    [Key("charges")]
    public int Charges { get; set; }

    [Key("abilityCooldown")]
    public int AbilityCooldown { get; set; }

    [Key("stealthRoundsLeft")]
    public int StealthRoundsLeft { get; set; }

    [Key("downRoundsLeft")]
    public int? DownRoundsLeft { get; set; }

    [Key("blessings")]
    public IReadOnlyList<string> Blessings { get; set; } = [];

    [Key("pact")]
    public PactDto? Pact { get; set; }

    [Key("decided")]
    public bool Decided { get; set; }

    /// <summary><c>{type, direction?}</c>: GUARD no lleva dirección.</summary>
    [Key("pendingOrder")]
    public IDictionary<string, object>? PendingOrder { get; set; }

    /// <summary>Caminos tras la extinción (desde 1.3.0). Solo lo ve su dueño.</summary>
    [Key("path")]
    public SelfPathDto? Path { get; set; }
}

/// <summary>Lo privado de los caminos (contrato 1.3.0): elección, ecos y ascuas guardadas.</summary>
[MessagePackObject]
public sealed class SelfPathDto
{
    /// <summary>Id de la oferta abierta (hay que llamar a <c>ChoosePath</c>); <c>null</c> si no hay que elegir.</summary>
    [Key("offerId")]
    public string? OfferId { get; set; }

    /// <summary>Camino elegido que aún no se reveló (<c>FRIENDSHIP</c> o <c>VENGEANCE</c>).</summary>
    [Key("choice")]
    public string? Choice { get; set; }

    [Key("savedEmbers")]
    public int SavedEmbers { get; set; }

    [Key("echoes")]
    public int Echoes { get; set; }

    /// <summary>Brasa: ascuas con las que renacería ahora (fórmula de renacer).</summary>
    [Key("rebirthEmbers")]
    public int? RebirthEmbers { get; set; }

    /// <summary>Brasa: enfriamiento de Estorbo (el de Avivar es <c>abilityCooldown</c>).</summary>
    [Key("hinderCooldown")]
    public int HinderCooldown { get; set; }

    /// <summary>Renegado que todavía no apareció: <c>NEXT_FLOOR</c> o <c>ROUND</c> (ver <c>spawnRound</c>).</summary>
    [Key("awaitingSpawn")]
    public string? AwaitingSpawn { get; set; }

    [Key("spawnRound")]
    public int? SpawnRound { get; set; }
}

[MessagePackObject]
public sealed class OtherHeroDto
{
    [Key("playerId")]
    public string PlayerId { get; set; } = "";

    [Key("displayName")]
    public string DisplayName { get; set; } = "";

    [Key("hero")]
    public string Hero { get; set; } = "";

    [Key("state")]
    public string State { get; set; } = "";

    [Key("hp")]
    public int Hp { get; set; }

    [Key("maxHp")]
    public int MaxHp { get; set; }

    [Key("position")]
    public PositionDto? Position { get; set; }

    [Key("facing")]
    public string? Facing { get; set; }

    [Key("embers")]
    public int Embers { get; set; }

    [Key("blessingCount")]
    public int BlessingCount { get; set; }

    [Key("connected")]
    public bool Connected { get; set; }

    [Key("decided")]
    public bool Decided { get; set; }
}

[MessagePackObject]
public sealed class EnemyDto
{
    [Key("enemyId")]
    public int EnemyId { get; set; }

    [Key("kind")]
    public string Kind { get; set; } = "";

    [Key("hp")]
    public int Hp { get; set; }

    [Key("maxHp")]
    public int MaxHp { get; set; }

    [Key("position")]
    public PositionDto Position { get; set; } = new();

    [Key("facing")]
    public string? Facing { get; set; }

    [Key("phase")]
    public int? Phase { get; set; }

    [Key("telegraph")]
    public IReadOnlyList<PositionDto> Telegraph { get; set; } = [];
}

[MessagePackObject]
public sealed class TileDto
{
    [Key("x")]
    public int X { get; set; }

    [Key("y")]
    public int Y { get; set; }

    [Key("kind")]
    public string Kind { get; set; } = "";
}

[MessagePackObject]
public sealed class PlayerViewDto
{
    [Key("status")]
    public string Status { get; set; } = "";

    [Key("round")]
    public int Round { get; set; }

    [Key("floor")]
    public int Floor { get; set; }

    [Key("roundDeadline")]
    public string? RoundDeadline { get; set; }

    [Key("me")]
    public SelfHeroDto Me { get; set; } = new();

    [Key("allies")]
    public IReadOnlyList<OtherHeroDto> Allies { get; set; } = [];

    [Key("enemies")]
    public IReadOnlyList<EnemyDto> Enemies { get; set; } = [];

    [Key("tiles")]
    public IReadOnlyList<TileDto> Tiles { get; set; } = [];
}

[MessagePackObject]
public sealed class PlayerViewDeltaDto
{
    [Key("status")]
    public string Status { get; set; } = "";

    [Key("floorChanged")]
    public int? FloorChanged { get; set; }

    [Key("me")]
    public SelfHeroDto Me { get; set; } = new();

    [Key("alliesChanged")]
    public IReadOnlyList<OtherHeroDto> AlliesChanged { get; set; } = [];

    [Key("enemiesUpserted")]
    public IReadOnlyList<EnemyDto> EnemiesUpserted { get; set; } = [];

    [Key("enemiesRemoved")]
    public IReadOnlyList<int> EnemiesRemoved { get; set; } = [];

    [Key("tilesChanged")]
    public IReadOnlyList<TileDto> TilesChanged { get; set; } = [];

    /// <summary>Eventos visibles <c>{type, actorId?, targetId?, position?, amount?}</c>.</summary>
    [Key("events")]
    public IReadOnlyList<IDictionary<string, object>> Events { get; set; } = [];
}

// ───────────── Servidor → cliente ─────────────

[MessagePackObject]
public sealed class SnapshotMessage
{
    [Key("matchId")]
    public string MatchId { get; set; } = "";

    [Key("seq")]
    public long Seq { get; set; }

    [Key("serverTime")]
    public string ServerTime { get; set; } = "";

    [Key("view")]
    public PlayerViewDto View { get; set; } = new();
}

[MessagePackObject]
public sealed class RoundOpenedMessage
{
    [Key("matchId")]
    public string MatchId { get; set; } = "";

    [Key("round")]
    public int Round { get; set; }

    [Key("deadline")]
    public string Deadline { get; set; } = "";

    [Key("durationMs")]
    public int DurationMs { get; set; }

    [Key("expectedPlayers")]
    public int ExpectedPlayers { get; set; }
}

[MessagePackObject]
public sealed class PlayerDecidedMessage
{
    [Key("matchId")]
    public string MatchId { get; set; } = "";

    [Key("round")]
    public int Round { get; set; }

    [Key("playerId")]
    public string PlayerId { get; set; } = "";

    [Key("decidedCount")]
    public int DecidedCount { get; set; }

    [Key("expectedPlayers")]
    public int ExpectedPlayers { get; set; }
}

[MessagePackObject]
public sealed class RoundResolvedMessage
{
    [Key("matchId")]
    public string MatchId { get; set; } = "";

    [Key("round")]
    public int Round { get; set; }

    [Key("previousSeq")]
    public long PreviousSeq { get; set; }

    [Key("seq")]
    public long Seq { get; set; }

    [Key("delta")]
    public PlayerViewDeltaDto Delta { get; set; } = new();
}

[MessagePackObject]
public sealed class DraftOfferMessage
{
    [Key("matchId")]
    public string MatchId { get; set; } = "";

    [Key("offerId")]
    public string OfferId { get; set; } = "";

    /// <summary><c>INITIAL_DRAFT</c> o <c>ALTAR</c>.</summary>
    [Key("source")]
    public string Source { get; set; } = "";

    [Key("options")]
    public IReadOnlyList<string> Options { get; set; } = [];

    [Key("deadline")]
    public string Deadline { get; set; } = "";
}

/// <summary>Oferta privada de camino tras extinguirse (contrato 1.3.0).</summary>
[MessagePackObject]
public sealed class PathOfferMessage
{
    [Key("matchId")]
    public string MatchId { get; set; } = "";

    [Key("offerId")]
    public string OfferId { get; set; } = "";

    [Key("options")]
    public IReadOnlyList<string> Options { get; set; } = [];

    [Key("defaultPath")]
    public string DefaultPath { get; set; } = "";

    [Key("deadline")]
    public string Deadline { get; set; } = "";

    [Key("savedEmbers")]
    public int SavedEmbers { get; set; }

    /// <summary>Ascuas con las que vuelve como Renegado: ⌈guardadas / 2⌉.</summary>
    [Key("vengeanceEmbers")]
    public int VengeanceEmbers { get; set; }

    /// <summary>Ascuas con las que renacería la Brasa con 0 ecos (25%) y con el tope (85%).</summary>
    [Key("friendshipEmbersMin")]
    public int FriendshipEmbersMin { get; set; }

    [Key("friendshipEmbersMax")]
    public int FriendshipEmbersMax { get; set; }

    /// <summary>En la sala del Celador la Brasa no renace y el Renegado aparece 2 rondas después.</summary>
    [Key("wardenRoom")]
    public bool WardenRoom { get; set; }
}

[MessagePackObject]
public sealed class PlayerConnectionChangedMessage
{
    [Key("matchId")]
    public string MatchId { get; set; } = "";

    [Key("playerId")]
    public string PlayerId { get; set; } = "";

    /// <summary><c>CONNECTED</c>, <c>DISCONNECTED</c> o <c>ABANDONED</c>.</summary>
    [Key("state")]
    public string State { get; set; } = "";

    [Key("graceDeadline")]
    public string? GraceDeadline { get; set; }
}

[MessagePackObject]
public sealed class RevealedPactDto
{
    [Key("pactId")]
    public string PactId { get; set; } = "";

    [Key("description")]
    public string Description { get; set; } = "";

    [Key("fulfilled")]
    public bool Fulfilled { get; set; }
}

[MessagePackObject]
public sealed class StandingDto
{
    [Key("playerId")]
    public string PlayerId { get; set; } = "";

    [Key("displayName")]
    public string DisplayName { get; set; } = "";

    [Key("hero")]
    public string Hero { get; set; } = "";

    [Key("finalState")]
    public string FinalState { get; set; } = "";

    [Key("embers")]
    public int Embers { get; set; }

    [Key("placement")]
    public int Placement { get; set; }

    [Key("pact")]
    public RevealedPactDto? Pact { get; set; }
}

[MessagePackObject]
public sealed class MatchFinishedMessage
{
    [Key("matchId")]
    public string MatchId { get; set; } = "";

    [Key("seq")]
    public long Seq { get; set; }

    [Key("outcome")]
    public string Outcome { get; set; } = "";

    [Key("rounds")]
    public int Rounds { get; set; }

    [Key("winners")]
    public IReadOnlyList<string> Winners { get; set; } = [];

    [Key("standings")]
    public IReadOnlyList<StandingDto> Standings { get; set; } = [];

    [Key("finishedAt")]
    public string FinishedAt { get; set; } = "";

    [Key("replayUrl")]
    public string ReplayUrl { get; set; } = "";
}

[MessagePackObject]
public sealed class ServerDrainingMessage
{
    /// <summary><c>DEPLOYMENT</c>, <c>SCALE_IN</c> o <c>SHUTDOWN</c>.</summary>
    [Key("reason")]
    public string Reason { get; set; } = "";

    [Key("reconnectAfterMs")]
    public int ReconnectAfterMs { get; set; }

    [Key("closesAt")]
    public string ClosesAt { get; set; } = "";
}
