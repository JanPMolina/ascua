using System.Collections.Immutable;
using Ascua.Domain.Abstractions;
using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

public sealed record PlayerSetup(PlayerId Player, HeroClass Class);

/// <summary>
/// Genera una partida nueva con el azar inyectado: 4 salas + la del Celador, pactos secretos
/// y el draft inicial. Con un <see cref="SeededRandom"/> la partida es reproducible. El lado de las salas depende de
/// los jugadores (07-version-4.md §2): 9 con 2 a 4 y 11 con 5.
/// </summary>
public static class MatchFactory
{
    public const int MinPlayers = 2;
    public const int MaxPlayers = 5;
    public const int RoomCount = 5;
    private const int MaxLayoutAttempts = 50;

    /// <summary>Entradas de una sala de 9 (2 a 4 jugadores).</summary>
    public static readonly ImmutableList<Position> SpawnPoints = SpawnPointsFor(Room.DefaultSize);

    /// <summary>Escalera de una sala de 9.</summary>
    public static readonly Position StairsPosition = StairsFor(Room.DefaultSize);

    /// <summary>Celador en una sala de 9.</summary>
    public static readonly Position WardenPosition = StairsFor(Room.DefaultSize);

    /// <summary>Entradas por asiento: fila <c>lado − 2</c>, columnas c−1, c+1, c−2, c+2 y c.</summary>
    public static ImmutableList<Position> SpawnPointsFor(int size)
    {
        var c = size / 2;
        var row = size - 2;
        return [new(c - 1, row), new(c + 1, row), new(c - 2, row), new(c + 2, row), new(c, row)];
    }

    /// <summary>Escalera (y Celador) en <c>(c, c − 1)</c>.</summary>
    public static Position StairsFor(int size) => new(size / 2, (size / 2) - 1);

    /// <summary>Celador al empezar su sala: donde estaría la escalera.</summary>
    public static Position WardenFor(int size) => StairsFor(size);

    public static (GameState State, IReadOnlyList<DomainEvent> Events) Create(
        IReadOnlyList<PlayerSetup> players,
        IRandom rng,
        IReadOnlyCollection<Curse>? curses = null)
    {
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(rng);
        if (players.Count is < MinPlayers or > MaxPlayers)
        {
            throw new ArgumentException($"Se necesitan entre {MinPlayers} y {MaxPlayers} jugadores.", nameof(players));
        }

        if (players.Select(p => p.Player).Distinct().Count() != players.Count)
        {
            throw new ArgumentException("Hay jugadores repetidos.", nameof(players));
        }

        ImmutableList<Curse> chosenCurses = [.. (curses ?? []).Distinct().Order()];
        var nextEnemyId = 1;
        var nextChestId = 1;
        var rooms = ImmutableList.CreateBuilder<Room>();
        var previous = RoomType.Normal;
        for (var index = 0; index < RoomCount; index++)
        {
            // La primera es normal y la última la del Celador; las demás, de un tipo al azar distinto del de la anterior
            // (07-version-4.md §6). Nadie lo sabe hasta entrar: la vista solo muestra la sala actual.
            var type = RoomType.Normal;
            if (index > 0 && index < RoomCount - 1)
            {
                var options = RoomTypes.Random.Where(t => t != previous).ToList();
                type = options[rng.Next(options.Count)];
            }

            rooms.Add(GenerateRoom(index, type, players.Count, chosenCurses, rng, ref nextEnemyId, ref nextChestId));
            previous = type;
        }

        var spawns = rooms[0].SpawnPoints;

        // Las 2 opciones de cada uno salen seguidas de un mazo barajado: nadie ve la misma misión dos veces.
        var deck = Shuffle(PactRules.Offered.ToList(), rng);
        var heroes = ImmutableList.CreateBuilder<Hero>();
        for (var seat = 0; seat < players.Count; seat++)
        {
            var setup = players[seat];
            var options = Shuffle(BlessingRules.All.ToList(), rng).Take(2);
            var pactOptions = ImmutableList.CreateBuilder<PactOffer>();
            for (var i = 0; i < PactRules.OfferedOptions; i++)
            {
                var kind = deck[((seat * PactRules.OfferedOptions) + i) % deck.Count];
                var others = players.Where(p => p.Player != setup.Player).ToList();
                pactOptions.Add(new PactOffer(kind, kind == PactKind.OutgoldTarget ? others[rng.Next(others.Count)].Player : null));
            }

            heroes.Add(Hero.Create(setup.Player, seat, setup.Class, spawns[seat]) with
            {
                Pact = pactOptions[0].Kind,
                PactTarget = pactOptions[0].Target,
                PactOptions = pactOptions.ToImmutable(),
                DraftOptions = [.. options],
                SecondBody = setup.Class == HeroClass.Hilvan ? SecondBodyAt(rooms[0], seat, players.Count) : null,
            });
        }

        var log = new EventLog(0, 1);
        log.Emit(new MatchCreated(players.Count));
        foreach (var hero in heroes)
        {
            log.Emit(new DraftOffered(hero.Player, hero.DraftOptions) { Audience = Audience.Only(hero.Player) });
        }

        var first = rooms[0];
        var state = new GameState
        {
            Round = 1,
            Phase = MatchPhase.Drafting,
            Heroes = heroes.ToImmutable(),
            Rooms = rooms.ToImmutable(),
            RoomIndex = 0,
            Enemies = first.InitialEnemies,
            NextEnemyId = nextEnemyId,
            NextChestId = nextChestId,
            Curses = chosenCurses,
            LastSeq = log.LastSeq,
        };
        return (state, log.Events);
    }

    /// <summary>Hilván empieza con sus cuerpos en la misma fila, a 2 casillas, sin pisar la entrada de otro asiento.</summary>
    private static Position SecondBodyAt(Room room, int seat, int playerCount)
    {
        var spawns = room.SpawnPoints;
        var east = spawns[seat].Step(Direction.East, HeroRules.BodySpacing);
        return room.IsInside(east) && !spawns.Take(playerCount).Contains(east)
            ? east
            : spawns[seat].Step(Direction.West, HeroRules.BodySpacing);
    }

    /// <summary>Genera una sala del tipo dado.</summary>
    internal static Room GenerateRoom(
        int index, RoomType type, int playerCount, IReadOnlyCollection<Curse> curses, IRandom rng, ref int nextEnemyId, ref int nextChestId)
    {
        var isBossRoom = index == RoomCount - 1;
        var size = Room.SizeFor(playerCount);
        for (var attempt = 0; attempt < MaxLayoutAttempts; attempt++)
        {
            var room = TryLayout(index, type, isBossRoom, size, rng, simple: false);
            if (RoomValidator.IsValid(room))
            {
                return Populate(room, index, playerCount, curses, rng, ref nextEnemyId, ref nextChestId);
            }
        }

        return Populate(TryLayout(index, type, isBossRoom, size, rng, simple: true), index, playerCount, curses, rng, ref nextEnemyId, ref nextChestId);
    }

    /// <summary>Cuántos cofres trae una sala según su tipo.</summary>
    private static int ChestsIn(RoomType type) => type switch
    {
        RoomType.Treasure => 4,
        RoomType.Arena => 0,
        RoomType.Sanctuary => 1,
        RoomType.Dark => 3,
        _ => 2,
    };

    private static Room TryLayout(int index, RoomType type, bool isBossRoom, int size, IRandom rng, bool simple)
    {
        var spawns = SpawnPointsFor(size);
        var stairs = StairsFor(size);
        var large = size > Room.DefaultSize;
        var room = new Room { Index = index, Type = type, Size = size, SpawnPoints = spawns };
        var reserved = new HashSet<Position>(spawns.SelectMany(p => new[] { p, p.Step(Direction.North) }))
        {
            stairs,
            stairs.Step(Direction.South),
            WardenFor(size),
        };

        if (!isBossRoom)
        {
            room = room.WithTerrain(stairs, Terrain.Stairs);
        }

        var lastRow = size - 3;

        if (simple)
        {
            // Sin obstáculos, pero el santuario sigue trayendo su altar y los cofres su cantidad.
            var open = room.AllPositions().Where(p => p.Y >= 1 && p.Y <= lastRow && !reserved.Contains(p)).ToList();
            if (type == RoomType.Sanctuary)
            {
                room = room.WithTerrain(open[0], Terrain.Altar);
                open.RemoveAt(0);
            }

            return isBossRoom
                ? room
                : room with { Chests = [.. open.Take(ChestsIn(type)).Select(p => new Chest(-1, p, ChestLoot.Nothing, HasSilverKey: false))] };
        }

        var free = room.AllPositions().Where(p => p.Y >= 1 && p.Y <= lastRow && !reserved.Contains(p)).ToList();
        Position Take()
        {
            var i = rng.Next(free.Count);
            var position = free[i];
            free.RemoveAt(i);
            return position;
        }

        var walls = (isBossRoom ? 4 : 6) + (large ? 3 : 0);
        for (var i = 0; i < walls; i++)
        {
            room = room.WithTerrain(Take(), Terrain.Wall);
        }

        var spikes = isBossRoom ? 2 : type == RoomType.Sanctuary ? 6 : 3;
        if (large && !isBossRoom)
        {
            spikes += type == RoomType.Sanctuary ? 2 : 1;
        }

        for (var i = 0; i < spikes; i++)
        {
            room = room.WithTerrain(Take(), Terrain.Spikes);
        }

        for (var i = 0; i < (isBossRoom ? 2 : 1); i++)
        {
            room = room.WithTerrain(Take(), Terrain.Brazier);
        }

        if (index is 1 or 2)
        {
            room = room.WithTerrain(Take(), Terrain.Crack);
        }

        if (index is 1 or 3 || type == RoomType.Sanctuary)
        {
            room = room.WithTerrain(Take(), Terrain.Altar);
        }

        if (!isBossRoom)
        {
            var chests = ImmutableList.CreateBuilder<Chest>();
            for (var i = 0; i < ChestsIn(type); i++)
            {
                chests.Add(new Chest(-1, Take(), ChestLoot.Nothing, HasSilverKey: false));
            }

            room = room with { Chests = chests.ToImmutable() };
        }

        return room;
    }

    /// <summary>Los monstruos nacen a esta distancia o menos de la escalera (sala de 9): a la vista del grupo y sin rincones que recorrer.</summary>
    public const int MonsterSpread = 4;

    /// <summary>Distancia máxima a la escalera para nacer: 4 con 9 de lado, 5 con 11.</summary>
    public static int MonsterSpreadFor(int size) => size / 2;

    private static IEnumerable<EnemyKind> MonstersFor(Room room, int index, int playerCount, IRandom rng)
    {
        var list = Difficulty.Monsters(index, playerCount).ToList();
        switch (room.Type)
        {
            case RoomType.Treasure:
                list.Add(EnemyKind.Scavenger);
                break;
            case RoomType.Arena:
                list.AddRange(Enumerable.Repeat(EnemyKind.Scavenger, 1 + rng.Next(2)));
                break;
            case RoomType.Sanctuary:
                list = [list[0]];
                break;
            default:
                break;
        }

        return list;
    }

    private static Room Populate(Room room, int index, int playerCount, IReadOnlyCollection<Curse> curses, IRandom rng, ref int nextEnemyId, ref int nextChestId)
    {
        var chests = ImmutableList.CreateBuilder<Chest>();
        foreach (var chest in room.Chests)
        {
            var loot = (ChestLoot)rng.Next(3);
            chests.Add(chest with { Id = nextChestId++, Loot = loot, HasSilverKey = rng.Next(2) == 0 });
        }

        room = room with { Chests = chests.ToImmutable() };

        var occupied = new HashSet<Position>(room.Chests.Select(c => c.Position));
        var enemies = ImmutableList.CreateBuilder<Enemy>();
        foreach (var kind in MonstersFor(room, index, playerCount, rng))
        {
            if (kind == EnemyKind.Warden)
            {
                enemies.Add(Enemy.Create(nextEnemyId++, kind, WardenFor(room.Size), EnemyCatalog.WardenHp(playerCount, curses)));
                continue;
            }

            var stairs = StairsFor(room.Size);
            var spread = MonsterSpreadFor(room.Size);
            var candidates = room.AllPositions()
                .Where(p => p.Y <= room.Size - 4 && p.DistanceTo(stairs) <= spread
                    && room.TerrainAt(p) is Terrain.Floor or Terrain.Spikes
                    && !occupied.Contains(p))
                .ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            var position = candidates[rng.Next(candidates.Count)];
            occupied.Add(position);
            enemies.Add(Enemy.Create(nextEnemyId++, kind, position, EnemyCatalog.HpOf(kind, Difficulty.MonsterHp(kind, playerCount), curses)));
        }

        return room with { InitialEnemies = enemies.ToImmutable() };
    }

    private static List<T> Shuffle<T>(List<T> items, IRandom rng)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }

        return items;
    }
}

/// <summary>Una sala es válida si desde la entrada se llega a toda casilla transitable y a cada objeto.</summary>
public static class RoomValidator
{
    public static bool IsValid(Room room)
    {
        ArgumentNullException.ThrowIfNull(room);
        if (room.SpawnPoints.IsEmpty)
        {
            return false;
        }

        var reachable = new HashSet<Position>();
        var queue = new Queue<Position>();
        queue.Enqueue(room.SpawnPoints[0]);
        reachable.Add(room.SpawnPoints[0]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var direction in Directions.All)
            {
                var next = current.Step(direction);
                if (IsSafeFloor(room, next) && reachable.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        var allFloorReachable = room.AllPositions().Where(p => IsSafeFloor(room, p)).All(reachable.Contains);
        var objects = room.Chests.Select(c => c.Position)
            .Concat(room.PositionsOf(Terrain.Altar))
            .Concat(room.PositionsOf(Terrain.Brazier));
        var objectsReachable = objects.All(o => Directions.All.Any(d => reachable.Contains(o.Step(d))));
        return allFloorReachable && objectsReachable;
    }

    internal static bool IsSafeFloor(Room room, Position position) =>
        room.IsInside(position)
        && room.TerrainAt(position) is Terrain.Floor or Terrain.Spikes or Terrain.Stairs
        && room.ChestAt(position) is null;
}
