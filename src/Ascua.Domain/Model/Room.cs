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

/// <summary>Salas con reglas propias; el tipo sale al azar al entrar (07-version-4.md §6).</summary>
public enum RoomType
{
    /// <summary>Sin regla propia: la primera sala y la del Celador.</summary>
    Normal,

    /// <summary>4 cofres y 1 monstruo más.</summary>
    Treasure,

    /// <summary>Sin cofres, 1 o 2 monstruos más y cada golpe final da +2.</summary>
    Arena,

    /// <summary>Pocos monstruos, un altar que no pide llave y más trampas.</summary>
    Sanctuary,

    /// <summary>Visión 1 menos para todos y 1 cofre más.</summary>
    Dark,

    /// <summary>Desde el norte, cada 6 rondas se inunda una fila más y hace 1 de daño a quien no vuela.</summary>
    Flooded,
}

public static class RoomTypes
{
    /// <summary>Tipos que pueden salir al azar en las salas intermedias.</summary>
    public static IReadOnlyList<RoomType> Random { get; } = [RoomType.Treasure, RoomType.Arena, RoomType.Sanctuary, RoomType.Dark, RoomType.Flooded];

    public const int FloodEvery = 6;
    public const int FloodDamage = 1;
    public const int ArenaKillReward = 2;
}

public sealed record Room
{
    /// <summary>Lado de la sala con 2 a 4 jugadores.</summary>
    public const int DefaultSize = 9;

    /// <summary>Lado de la sala con 5 jugadores.</summary>
    public const int LargeSize = 11;

    public required int Index { get; init; }

    /// <summary>Lado de la sala (cuadrada): 9 con 2 a 4 jugadores, 11 con 5 (07-version-4.md §2).</summary>
    public int Size { get; init; } = DefaultSize;

    /// <summary>Columna (y fila de referencia) del centro.</summary>
    public int Center => Size / 2;

    public RoomType Type { get; init; } = RoomType.Normal;

    /// <summary>Ronda en la que el grupo entró (de ahí sale el avance del agua).</summary>
    public int EnteredRound { get; init; } = 1;
    public ImmutableList<Feature> Features { get; init; } = [];
    public ImmutableList<Chest> Chests { get; init; } = [];
    public ImmutableList<Fire> Fires { get; init; } = [];

    /// <summary>Ascuas en el suelo de esta sala; se pierden al cambiar de sala.</summary>
    public ImmutableList<GroundEmbers> Ground { get; init; } = [];

    /// <summary>Enemigos con los que arranca la sala cuando los héroes entran.</summary>
    public ImmutableList<Enemy> InitialEnemies { get; init; } = [];

    /// <summary>Casillas de entrada de los héroes, por asiento.</summary>
    public ImmutableList<Position> SpawnPoints { get; init; } = [];

    /// <summary>Lado de la sala para una partida de tantos jugadores.</summary>
    public static int SizeFor(int playerCount) => playerCount >= 5 ? LargeSize : DefaultSize;

    public bool IsInside(Position position) => position.IsInside(Size, Size);

    public IEnumerable<Position> AllPositions()
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

    /// <summary>Filas inundadas desde el norte en esa ronda; 0 salvo en la sala inundada.</summary>
    public int FloodedRows(int round) =>
        Type == RoomType.Flooded ? Math.Clamp((round - EnteredRound) / RoomTypes.FloodEvery, 0, Size) : 0;

    public Chest? ChestAt(Position position) => Chests.Find(c => c.Position == position);

    public Fire? FireAt(Position position) => Fires.Find(f => f.Position == position);

    public IEnumerable<Position> PositionsOf(Terrain terrain) =>
        Features.Where(f => f.Terrain == terrain).Select(f => f.Position);
}
