namespace Ascua.Domain.Model;

public enum OrderType
{
    Guard,
    Move,
    Attack,
    Skill,
    Interact,
}

/// <summary>Intención de un jugador para una ronda. El cliente nunca envía resultados.</summary>
public sealed record Order(OrderType Type, Direction? Direction)
{
    /// <summary>Acción por defecto cuando el jugador no decide a tiempo.</summary>
    public static Order DefaultGuard { get; } = new(OrderType.Guard, null);

    public bool IsWellFormed() => Type == OrderType.Guard ? Direction is null : Direction is not null;
}
