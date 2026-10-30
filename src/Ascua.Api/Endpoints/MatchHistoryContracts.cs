using System.ComponentModel;
using Ascua.Api.Realtime;

namespace Ascua.Api.Endpoints;

// DTOs de historial, detalle y reproducción. Las vistas reutilizan los DTOs del contrato de tiempo
// real (PlayerViewDto, StandingDto…), así que una reproducción se ve igual que la partida en vivo.

public sealed record MatchHistoryItem(
    Guid MatchId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    [property: Description("DRAFTING, IN_PROGRESS, FINISHED, ABANDONED, EXPIRED o FAULTED.")] string Status,
    [property: Description("Resultado global: WARDEN_DEFEATED, DRAW, EMBER_EXTINGUISHED, ABANDONED, EXPIRED, FAULTED; null si sigue en curso.")] string? Outcome,
    [property: Description("Resultado para quien consulta: VICTORY, DRAW, DEFEAT (otro ganó), COLLECTIVE_DEFEAT, ABANDONED; null sin resultado del juego.")] string? Result,
    [property: Description("Héroe con el que jugó: YUNQUE, CHISPA, CUERVO o RAIZ.")] string Hero,
    [property: Description("Puesto final (1 = más ascuas, empates comparten puesto); null sin resultado.")] int? Placement,
    [property: Description("Puntuación final (ascuas + pacto cumplido); null sin resultado.")] int? Embers,
    [property: Description("Rondas jugadas.")] int Rounds,
    int PlayerCount);

public sealed record MatchHistoryResponse(
    IReadOnlyList<MatchHistoryItem> Items,
    int Page,
    int PageSize,
    [property: Description("Total de partidas del usuario.")] long Total);

public sealed record MatchDetailResponse(
    Guid MatchId,
    string Status,
    [property: Description("Igual que en el historial; null si sigue en curso.")] string? Outcome,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    int Rounds,
    [property: Description("playerId de los ganadores (vacío en empate sin Celador, derrota colectiva o sin resultado).")] IReadOnlyList<string> Winners,
    [property: Description("Jugadores ordenados por puesto. El pacto ajeno solo aparece si la partida terminó con resultado del juego.")] IReadOnlyList<StandingDto> Players,
    [property: Description("Tu propio pacto (siempre visible para ti).")] RevealedPactDto MyPact,
    string ReplayUrl);

public sealed record ReplayRound(
    [property: Description("0 = draft inicial; 1..60 = rondas.")] int Round,
    [property: Description("Último seq aplicado al final de la ronda.")] long Seq,
    [property: Description("Vista de quien pide al final de la ronda (solo en view=PLAYER).")] PlayerViewDto? View,
    [property: Description("Verdad completa al final de la ronda (solo en view=FULL, administradores).")] FullStateDto? Full,
    [property: Description("Eventos de la ronda, en orden. En view=PLAYER, solo los que esa persona vio en vivo.")] IReadOnlyList<IDictionary<string, object>> Events);

public sealed record MatchReplayResponse(
    Guid MatchId,
    [property: Description("PLAYER (vista de quien pide) o FULL (administrador, auditado).")] string ViewMode,
    [property: Description("playerId de quien mira; null en FULL.")] string? ViewerId,
    string Status,
    int FromRound,
    int ToRound,
    IReadOnlyList<ReplayRound> Rounds);

public sealed record FullStateDto(
    int Round,
    string Phase,
    int Floor,
    IReadOnlyList<FullHeroDto> Heroes,
    IReadOnlyList<FullEnemyDto> Enemies,
    IReadOnlyList<FullChestDto> Chests);

public sealed record FullHeroDto(
    string PlayerId,
    string Hero,
    string State,
    int Hp,
    int Shield,
    PositionDto Position,
    string Facing,
    int Embers,
    IReadOnlyList<string> Blessings,
    IReadOnlyList<string> DraftOptions,
    string Pact,
    int StealthRoundsLeft);

public sealed record FullEnemyDto(int EnemyId, string Kind, int Hp, int MaxHp, PositionDto Position, string Facing, IReadOnlyList<PositionDto> Telegraph);

public sealed record FullChestDto(int ChestId, PositionDto Position, bool Opened, string? OpenedBy, string Loot, bool SilverKey);
