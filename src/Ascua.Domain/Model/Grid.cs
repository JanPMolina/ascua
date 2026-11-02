namespace Ascua.Domain.Model;

public enum Direction
{
    North,
    South,
    East,
    West,
}

public readonly record struct Position(int X, int Y)
{
    public Position Step(Direction direction) => direction switch
    {
        Direction.North => this with { Y = Y - 1 },
        Direction.South => this with { Y = Y + 1 },
        Direction.East => this with { X = X + 1 },
        Direction.West => this with { X = X - 1 },
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null),
    };

    public Position Step(Direction direction, int distance)
    {
        var position = this;
        for (var i = 0; i < distance; i++)
        {
            position = position.Step(direction);
        }

        return position;
    }

    public bool IsInside(int width, int height) => X >= 0 && Y >= 0 && X < width && Y < height;

    /// <summary>Distancia Manhattan (la que usan la visión, los alcances y la IA).</summary>
    public int DistanceTo(Position other) => Math.Abs(X - other.X) + Math.Abs(Y - other.Y);

    /// <summary>
    /// Dirección dominante hacia <paramref name="other"/>: el eje con mayor diferencia
    /// (en empate, el horizontal). <c>null</c> si es la misma casilla.
    /// </summary>
    public Direction? DirectionTo(Position other)
    {
        var dx = other.X - X;
        var dy = other.Y - Y;
        if (dx == 0 && dy == 0)
        {
            return null;
        }

        if (Math.Abs(dx) >= Math.Abs(dy))
        {
            return dx > 0 ? Direction.East : Direction.West;
        }

        return dy > 0 ? Direction.South : Direction.North;
    }

    /// <summary>¿Está <paramref name="other"/> en la misma fila o columna, en el sentido indicado?</summary>
    public bool IsInLine(Position other, Direction direction)
    {
        var sameAxis = direction is Direction.North or Direction.South ? other.X == X : other.Y == Y;
        return sameAxis && DirectionTo(other) == direction;
    }
}

public static class Directions
{
    public static IReadOnlyList<Direction> All { get; } = [Direction.North, Direction.East, Direction.South, Direction.West];

    /// <summary>Espejo de Hilván: norte y sur son iguales; este y oeste se invierten.</summary>
    public static Direction Mirrored(this Direction direction) =>
        direction is Direction.East or Direction.West ? direction.Opposite() : direction;

    public static Direction Opposite(this Direction direction) => direction switch
    {
        Direction.North => Direction.South,
        Direction.South => Direction.North,
        Direction.East => Direction.West,
        Direction.West => Direction.East,
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null),
    };
}
