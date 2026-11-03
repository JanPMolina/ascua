using System.Collections.Immutable;
using Ascua.Domain.Abstractions;
using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

public sealed record PlayerSetup(PlayerId Player, HeroClass Class);

/// <summary>
/// Genera una partida nueva con el azar inyectado: 4 salas + la del Celador, pactos secretos
/// y el draft inicial. Con un <see cref="SeededRandom"/> la partida es reproducible.
/// </summary>
public static class MatchFactory
{
    public const int MinPlayers = 2;
    public const int MaxPlayers = 4;
    public const int RoomCount = 5;
    private const int MaxLayoutAttempts = 50;

    public static readonly ImmutableList<Position> SpawnPoints = [new(3, 7), new(5, 7), new(2, 7), new(6, 7)];
    public static readonly Position StairsPosition = new(4, 3);
    public static readonly Position WardenPosition = new(4, 3);

    private static readonly EnemyKind[][] RoomEnemies =
    [
        [EnemyKind.Scavenger, EnemyKind.Scavenger, EnemyKind.Moth],
        [EnemyKind.Slime, EnemyKind.Hopper, EnemyKind.Scavenger],
        [EnemyKind.Crossbowman, EnemyKind.Slime, EnemyKind.Moth],
        [EnemyKind.Totem, EnemyKind.Crossbowman],
        [EnemyKind.Warden],
    ];

    public static (GameState State, IReadOnlyList<DomainEvent> Events) Create(
        IReadOnlyList<PlayerSetup> players,
        IRandom rng,
        int maxRounds = GameState.DefaultMaxRounds)
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

        var nextEnemyId = 1;
        var nextChestId = 1;
        var rooms = ImmutableList.CreateBuilder<Room>();
        for (var index = 0; index < RoomCount; index++)
        {
            rooms.Add(GenerateRoom(index, players.Count, rng, ref nextEnemyId, ref nextChestId));
        }

        var pacts = Shuffle(Enum.GetValues<PactKind>().ToList(), rng);
        var heroes = ImmutableList.CreateBuilder<Hero>();
        for (var seat = 0; seat < players.Count; seat++)
        {
            var setup = players[seat];
            var options = Shuffle(BlessingRules.All.ToList(), rng).Take(2);
            heroes.Add(Hero.Create(setup.Player, seat, setup.Class, SpawnPoints[seat]) with
            {
                Pact = pacts[seat % pacts.Count],
                DraftOptions = [.. options],
                SecondBody = setup.Class == HeroClass.Hilvan ? SecondBodyAt(seat, players.Count) : null,
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
            MaxRounds = maxRounds,
            Phase = MatchPhase.Drafting,
            Heroes = heroes.ToImmutable(),
            Rooms = rooms.ToImmutable(),
            RoomIndex = 0,
            Enemies = first.InitialEnemies,
            NextEnemyId = nextEnemyId,
            LastSeq = log.LastSeq,
        };
        return (state, log.Events);
    }

    /// <summary>Hilván empieza con sus cuerpos en la misma fila, a 2 casillas, sin pisar la entrada de otro asiento.</summary>
    private static Position SecondBodyAt(int seat, int playerCount)
    {
        var east = SpawnPoints[seat].Step(Direction.East, HeroRules.BodySpacing);
        return Room.IsInside(east) && !SpawnPoints.Take(playerCount).Contains(east)
            ? east
            : SpawnPoints[seat].Step(Direction.West, HeroRules.BodySpacing);
    }

    private static Room GenerateRoom(int index, int playerCount, IRandom rng, ref int nextEnemyId, ref int nextChestId)
    {
        var isBossRoom = index == RoomCount - 1;
        for (var attempt = 0; attempt < MaxLayoutAttempts; attempt++)
        {
            var room = TryLayout(index, isBossRoom, rng, simple: false);
            if (RoomValidator.IsValid(room))
            {
                return Populate(room, index, playerCount, rng, ref nextEnemyId, ref nextChestId);
            }
        }

        return Populate(TryLayout(index, isBossRoom, rng, simple: true), index, playerCount, rng, ref nextEnemyId, ref nextChestId);
    }

    private static Room TryLayout(int index, bool isBossRoom, IRandom rng, bool simple)
    {
        var room = new Room { Index = index, SpawnPoints = SpawnPoints };
        var reserved = new HashSet<Position>(SpawnPoints.SelectMany(p => new[] { p, p.Step(Direction.North) }))
        {
            StairsPosition,
            StairsPosition.Step(Direction.South),
            WardenPosition,
        };

        if (!isBossRoom)
        {
            room = room.WithTerrain(StairsPosition, Terrain.Stairs);
        }

        if (simple)
        {
            return room;
        }

        var free = Room.AllPositions().Where(p => p.Y is >= 1 and <= 6 && !reserved.Contains(p)).ToList();
        Position Take()
        {
            var i = rng.Next(free.Count);
            var position = free[i];
            free.RemoveAt(i);
            return position;
        }

        var walls = isBossRoom ? 4 : 6;
        for (var i = 0; i < walls; i++)
        {
            room = room.WithTerrain(Take(), Terrain.Wall);
        }

        for (var i = 0; i < (isBossRoom ? 2 : 3); i++)
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

        if (index is 1 or 3)
        {
            room = room.WithTerrain(Take(), Terrain.Altar);
        }

        if (!isBossRoom)
        {
            var chests = ImmutableList.CreateBuilder<Chest>();
            for (var i = 0; i < 2; i++)
            {
                chests.Add(new Chest(-1, Take(), ChestLoot.Nothing, HasSilverKey: false));
            }

            room = room with { Chests = chests.ToImmutable() };
        }

        return room;
    }

    /// <summary>Los monstruos nacen a esta distancia o menos de la escalera: a la vista del grupo y sin rincones que recorrer.</summary>
    public const int MonsterSpread = 4;

    private static Room Populate(Room room, int index, int playerCount, IRandom rng, ref int nextEnemyId, ref int nextChestId)
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
        // Con 2 jugadores la sala trae un monstruo menos: los mismos enemigos tardarían el doble en caer.
        var kinds = RoomEnemies[index];
        foreach (var kind in playerCount == MinPlayers ? kinds.Take(2) : kinds)
        {
            if (kind == EnemyKind.Warden)
            {
                enemies.Add(Enemy.Create(nextEnemyId++, kind, WardenPosition, EnemyCatalog.WardenHpPerPlayer * playerCount));
                continue;
            }

            var candidates = Room.AllPositions()
                .Where(p => p.Y <= 5 && p.DistanceTo(StairsPosition) <= MonsterSpread
                    && room.TerrainAt(p) is Terrain.Floor or Terrain.Spikes
                    && !occupied.Contains(p))
                .ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            var position = candidates[rng.Next(candidates.Count)];
            occupied.Add(position);
            enemies.Add(Enemy.Create(nextEnemyId++, kind, position));
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

        var allFloorReachable = Room.AllPositions().Where(p => IsSafeFloor(room, p)).All(reachable.Contains);
        var objects = room.Chests.Select(c => c.Position)
            .Concat(room.PositionsOf(Terrain.Altar))
            .Concat(room.PositionsOf(Terrain.Brazier));
        var objectsReachable = objects.All(o => Directions.All.Any(d => reachable.Contains(o.Step(d))));
        return allFloorReachable && objectsReachable;
    }

    internal static bool IsSafeFloor(Room room, Position position) =>
        Room.IsInside(position)
        && room.TerrainAt(position) is Terrain.Floor or Terrain.Spikes or Terrain.Stairs
        && room.ChestAt(position) is null;
}
