namespace Ascua.Domain.Model;

public enum OrderType
{
    Guard,
    Move,
    Attack,
    Skill,
    Interact,

    /// <summary>Sabotaje: quitado en la versión 4. Se conserva el valor para leer partidas viejas; siempre se rechaza.</summary>
    Sabotage,

    /// <summary>Empujón: mueve 1 casilla a lo que esté adyacente en esa dirección (06-version-3.md §7).</summary>
    Push,
}

/// <summary>
/// Intención de un jugador para una ronda: un paso opcional y una acción opcional con dirección. El cliente nunca envía
/// resultados. <c>MOVE</c> es solo paso (su dirección es el paso) y <c>GUARD</c> no admite paso: quien defiende se planta.
/// </summary>
public sealed record Order(OrderType Type, Direction? Direction)
{
    /// <summary>Acción por defecto cuando el jugador no decide a tiempo.</summary>
    public static Order DefaultGuard { get; } = new(OrderType.Guard, null);

    /// <summary>Paso previo a la acción. Se resuelve en la fase de movimientos.</summary>
    public Direction? Step { get; init; }

    /// <summary>Hacia dónde camina en la fase de movimientos, si camina.</summary>
    public Direction? Walk => Type == OrderType.Move ? Direction : Step;

    public bool IsWellFormed() => Type switch
    {
        OrderType.Guard => Direction is null && Step is null,
        OrderType.Move => Direction is not null && Step is null,
        _ => Direction is not null,
    };
}
