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

[MessagePackObject]
public sealed class SendSignalRequest
{
    [Key("matchId")]
    public string? MatchId { get; set; }

    /// <summary><c>HERE</c>, <c>DANGER</c>, <c>HELP</c> o <c>MINE</c>.</summary>
    [Key("kind")]
    public string? Kind { get; set; }

    [Key("position")]
    public PositionDto? Position { get; set; }
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

[MessagePackObject]
public sealed class SendSignalAck
{
    [Key("matchId")]
    public string MatchId { get; set; } = "";

    [Key("sentAt")]
    public string SentAt { get; set; } = "";
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

    [Key("abilityCooldown")]
    public int AbilityCooldown { get; set; }

    /// <summary>Hilván: su segundo cuerpo.</summary>
    [Key("secondBody")]
    public PositionDto? SecondBody { get; set; }

    /// <summary>Polizón: el enemigo en el que va. Solo lo ve él.</summary>
    [Key("hostEnemyId")]
    public int? HostEnemyId { get; set; }

    /// <summary>Ascuas que debe a la Usurera.</summary>
    [Key("debt")]
    public int Debt { get; set; }

    /// <summary>A quién le debe; <c>null</c> sin deuda.</summary>
    [Key("debtTo")]
    public string? DebtTo { get; set; }

    /// <summary>Anteayer: adónde saltaría Volver en la ronda abierta; <c>null</c> sin 2 rondas de historia.</summary>
    [Key("rewindTile")]
    public PositionDto? RewindTile { get; set; }

    /// <summary>Anteayer: vida que tenía allí.</summary>
    [Key("rewindHp")]
    public int? RewindHp { get; set; }

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

    /// <summary>Hilván: su segundo cuerpo, con la misma regla de visibilidad que <c>position</c>.</summary>
    [Key("secondBody")]
    public PositionDto? SecondBody { get; set; }

    /// <summary>Lo que te debe este jugador (solo si eres la Usurera a la que le debe).</summary>
    [Key("debtToYou")]
    public int? DebtToYou { get; set; }
}

/// <summary>Próximo Eco de Anteayer que se ve en el tablero.</summary>
[MessagePackObject]
public sealed class EchoDto
{
    [Key("ownerId")]
    public string OwnerId { get; set; } = "";

    [Key("position")]
    public PositionDto Position { get; set; } = new();

    [Key("direction")]
    public string Direction { get; set; } = "";

    [Key("fireRound")]
    public int FireRound { get; set; }
}

/// <summary>Ascuas en el suelo: el primero que pise la casilla se las queda.</summary>
[MessagePackObject]
public sealed class GroundEmbersDto
{
    [Key("position")]
    public PositionDto Position { get; set; } = new();

    [Key("amount")]
    public int Amount { get; set; }
}

/// <summary>Repique sonando en la ronda abierta.</summary>
[MessagePackObject]
public sealed class BellDto
{
    [Key("ringerId")]
    public string RingerId { get; set; } = "";

    /// <summary>Hasta cuándo pueden decidir los demás.</summary>
    [Key("deadline")]
    public string? Deadline { get; set; }

    /// <summary>Ya venció el plazo reducido.</summary>
    [Key("closed")]
    public bool Closed { get; set; }
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

    /// <summary>Casillas anunciadas por enemigos que no ves (Errata y Ojo de búho): solo las casillas, sin autor.</summary>
    [Key("hiddenTelegraphs")]
    public IReadOnlyList<PositionDto> HiddenTelegraphs { get; set; } = [];

    [Key("echoes")]
    public IReadOnlyList<EchoDto> Echoes { get; set; } = [];

    [Key("groundEmbers")]
    public IReadOnlyList<GroundEmbersDto> GroundEmbers { get; set; } = [];

    /// <summary>La Escucha: casillas donde oyó ruido en la última resolución.</summary>
    [Key("noiseTiles")]
    public IReadOnlyList<PositionDto> NoiseTiles { get; set; } = [];

    [Key("bell")]
    public BellDto? Bell { get; set; }
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

    /// <summary>Eventos visibles <c>{type, actorId?, targetId?, position?, amount?, detail?, tiles?}</c>.</summary>
    [Key("events")]
    public IReadOnlyList<IDictionary<string, object>> Events { get; set; } = [];

    /// <summary>Lista completa: reemplaza la anterior.</summary>
    [Key("hiddenTelegraphs")]
    public IReadOnlyList<PositionDto> HiddenTelegraphs { get; set; } = [];

    /// <summary>Lista completa: reemplaza la anterior.</summary>
    [Key("echoes")]
    public IReadOnlyList<EchoDto> Echoes { get; set; } = [];

    /// <summary>Lista completa: reemplaza la anterior.</summary>
    [Key("groundEmbers")]
    public IReadOnlyList<GroundEmbersDto> GroundEmbers { get; set; } = [];

    /// <summary>Lista completa: reemplaza la anterior.</summary>
    [Key("noiseTiles")]
    public IReadOnlyList<PositionDto> NoiseTiles { get; set; } = [];

    [Key("bell")]
    public BellDto? Bell { get; set; }

    /// <summary>Plazo de este jugador en la ronda que abre (con el Repique, puede ser menor que el de los demás).</summary>
    [Key("roundDeadline")]
    public string? RoundDeadline { get; set; }
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

    /// <summary>Con Repique: hasta cuándo pueden decidir todos menos <see cref="BellRingerId"/>.</summary>
    [Key("bellDeadline")]
    public string? BellDeadline { get; set; }

    [Key("bellRingerId")]
    public string? BellRingerId { get; set; }
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

/// <summary>Una orden que oye el Campanero. Como Renegado, <see cref="Direction"/> no llega.</summary>
[MessagePackObject]
public sealed class HeardOrderDto
{
    [Key("playerId")]
    public string PlayerId { get; set; } = "";

    [Key("type")]
    public string Type { get; set; } = "";

    [Key("direction")]
    public string? Direction { get; set; }
}

/// <summary>Último en hablar: privado para el Campanero cuando todos los demás ya decidieron.</summary>
[MessagePackObject]
public sealed class LastWordMessage
{
    [Key("matchId")]
    public string MatchId { get; set; } = "";

    [Key("round")]
    public int Round { get; set; }

    [Key("orders")]
    public IReadOnlyList<HeardOrderDto> Orders { get; set; } = [];

    /// <summary>Como Renegado solo se oyen los tipos.</summary>
    [Key("typesOnly")]
    public bool TypesOnly { get; set; }
}

/// <summary>Señal rápida de alguien de tu bando. La casilla la eligió esa persona: puede mentir.</summary>
[MessagePackObject]
public sealed class SignalReceivedMessage
{
    [Key("matchId")]
    public string MatchId { get; set; } = "";

    [Key("fromPlayerId")]
    public string FromPlayerId { get; set; } = "";

    [Key("kind")]
    public string Kind { get; set; } = "";

    [Key("position")]
    public PositionDto? Position { get; set; }

    [Key("sentAt")]
    public string SentAt { get; set; } = "";
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

/// <summary>Traición anónima revelada al final: «Errata le costó 3 de vida a Ana».</summary>
[MessagePackObject]
public sealed class BetrayalDto
{
    [Key("traitorId")]
    public string TraitorId { get; set; } = "";

    [Key("victimId")]
    public string VictimId { get; set; } = "";

    [Key("damage")]
    public int Damage { get; set; }
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

    /// <summary>Traiciones anónimas de la partida (vacío si no hubo o si no terminó con un resultado).</summary>
    [Key("betrayals")]
    public IReadOnlyList<BetrayalDto> Betrayals { get; set; } = [];
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
