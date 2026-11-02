using System.Collections.Immutable;

namespace Ascua.Domain.Model;

public enum Terrain
{
    Floor,
    Wall,

    /// <summary>Pinchos: se activan cada 2 rondas (rondas pares) y dañan a quien no vuela.</summary>
    Spikes,

    /// <summary>Grieta: Extinción inmediata para quien no vuela.</summary>
    Crack,

    /// <summary>Brasero: si un ataque lo vuelca, prende fuego en cruz.</summary>
    Brazier,

    /// <summary>Altar: con una llave de plata ofrece un draft de bendiciones.</summary>
    Altar,

    /// <summary>Escalera: con la sala despejada, lleva a la siguiente.</summary>
    Stairs,
}

/// <summary>Casilla que no es suelo.</summary>
public sealed record Feature(Position Position, Terrain Terrain);

public enum ChestLoot
{
    Nothing,

    /// <summary>Poción: cura 2 al abrir.</summary>
    HealingPotion,

    /// <summary>+1 de escudo.</summary>
    Shield,
}

public sealed record Chest(int Id, Position Position, ChestLoot Loot, bool HasSilverKey)
{
    public bool Opened { get; init; }
    public PlayerId? OpenedBy { get; init; }
}

public sealed record Fire(Position Position, int Rounds, int Damage);

/// <summary>Ascuas tiradas en una casilla (del Avaro o de un enemigo sobornado). Las recoge el primero que la pise.</summary>
public sealed record GroundEmbers(Position Position, int Amount);

public sealed record Room
{
    public const int Size = 9;

    public required int Index { get; init; }
    public ImmutableList<Feature> Features { get; init; } = [];
    public ImmutableList<Chest> Chests { get; init; } = [];
    public ImmutableList<Fire> Fires { get; init; } = [];

    /// <summary>Ascuas en el suelo de esta sala; se pierden al cambiar de sala.</summary>
    public ImmutableList<GroundEmbers> Ground { get; init; } = [];

    /// <summary>Enemigos con los que arranca la sala cuando los héroes entran.</summary>
    public ImmutableList<Enemy> InitialEnemies { get; init; } = [];

    /// <summary>Casillas de entrada de los héroes, por asiento.</summary>
    public ImmutableList<Position> SpawnPoints { get; init; } = [];

    public static bool IsInside(Position position) => position.IsInside(Size, Size);

    public static IEnumerable<Position> AllPositions()
    {
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                yield return new Position(x, y);
            }
        }
    }

    /// <summary>Terreno de la casilla; fuera de la sala cuenta como muro.</summary>
    public Terrain TerrainAt(Position position)
    {
        if (!IsInside(position))
        {
            return Terrain.Wall;
        }

        foreach (var feature in Features)
        {
            if (feature.Position == position)
            {
                return feature.Terrain;
            }
        }

        return Terrain.Floor;
    }

    public Room WithTerrain(Position position, Terrain terrain)
    {
        var without = Features.RemoveAll(f => f.Position == position);
        return this with { Features = terrain == Terrain.Floor ? without : without.Add(new Feature(position, terrain)) };
    }

    public Chest? ChestAt(Position position) => Chests.Find(c => c.Position == position);

    public Fire? FireAt(Position position) => Fires.Find(f => f.Position == position);

    public IEnumerable<Position> PositionsOf(Terrain terrain) =>
        Features.Where(f => f.Terrain == terrain).Select(f => f.Position);
}
