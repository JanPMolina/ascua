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

    public bool IsInside(int width, int height) => X >= 0 && Y >= 0 && X < width && Y < height;
}
