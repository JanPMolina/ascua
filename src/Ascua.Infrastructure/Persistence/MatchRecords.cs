namespace Ascua.Infrastructure.Persistence;

/// <summary>
/// Fila de <c>matches</c>. <see cref="State"/> es la instantánea durable de la verdad completa (jsonb,
/// incluida la información oculta) en el <see cref="LastSeq"/> indicado; nunca sale a un cliente.
/// <see cref="Result"/> se escribe una sola vez al terminar (un trigger impide cambiarlo después).
/// </summary>
public sealed class MatchRecord
{
    public Guid Id { get; set; }

    public Guid RoomId { get; set; }

    public string Status { get; set; } = null!;

    /// <summary>Control de concurrencia optimista: crece con cada confirmación.</summary>
    public long Version { get; set; }

    public long LastSeq { get; set; }

    public int Round { get; set; }

    public string State { get; set; } = null!;

    public DateTimeOffset? Deadline { get; set; }

    public int RoundDurationMs { get; set; }

    public string? Result { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>Asiento de <c>match_players</c>: autorización por partida y nombre visible.</summary>
public sealed class MatchPlayerRecord
{
    public Guid MatchId { get; set; }

    public Guid UserId { get; set; }

    public int Seat { get; set; }

    public string Hero { get; set; } = null!;

    public string DisplayName { get; set; } = null!;
}

/// <summary>Evento de <c>match_events</c> (append-only, <c>UNIQUE(match_id, seq)</c>). Solo se lee en el servidor.</summary>
public sealed class MatchEventRecord
{
    public Guid MatchId { get; set; }

    public long Seq { get; set; }

    public int Round { get; set; }

    public string Type { get; set; } = null!;

    public string Payload { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }
}
