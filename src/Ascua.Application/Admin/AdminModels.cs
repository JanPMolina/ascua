namespace Ascua.Application.Admin;

public enum AdminError
{
    UserNotFound,
    MatchNotFound,
    MatchNotActive,
    CannotTargetSelf,
    Busy,
}

/// <summary>Resultado de un caso de uso de administración: un valor o un <see cref="AdminError"/>.</summary>
public readonly record struct AdminResult<T>
{
    private AdminResult(T? value, AdminError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public AdminError? Error { get; }

    public static implicit operator AdminResult<T>(T value) => new(value, null);

    public static implicit operator AdminResult<T>(AdminError error) => new(default, error);
}

public sealed record AdminMatch(AdminMatchRow Match, IReadOnlySet<Guid> Connected);

public sealed record AdminMatchList(IReadOnlyList<AdminMatch> Items, long Total);

public sealed record ConnectedPlayer(Guid UserId, string DisplayName, int Connections);

public sealed record MatchConnections(Guid MatchId, string Status, IReadOnlyList<ConnectedPlayer> Players);

/// <summary>Conexiones vivas agregadas por partida y usuario (nunca ids de conexión ni tickets).</summary>
public sealed record ConnectionsOverview(DateTimeOffset GeneratedAt, int TotalConnections, int ConnectedUsers, IReadOnlyList<MatchConnections> Matches);

public sealed record SuspensionResult(Guid UserId, string Username, bool Suspended, DateTimeOffset UpdatedAt);

public sealed record TerminationResult(Guid MatchId, string Status, int Round, DateTimeOffset TerminatedAt);
