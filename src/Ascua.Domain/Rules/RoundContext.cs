using Ascua.Domain.Abstractions;
using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>
/// Copia de trabajo controlada de una ronda: listas mutables de registros inmutables.
/// Solo vive dentro de <see cref="Resolver"/>; al final se congela en un <see cref="GameState"/> nuevo.
/// </summary>
internal sealed class RoundContext
{
    private readonly EventLog _log;

    public RoundContext(GameState state, IRandom rng)
    {
        Start = state;
        Rng = rng;
        Heroes = [.. state.Heroes];
        Enemies = [.. state.Enemies];
        Rooms = [.. state.Rooms];
        RoomIndex = state.RoomIndex;
        NextEnemyId = state.NextEnemyId;
        _log = new EventLog(state.LastSeq, state.Round);
    }

    public GameState Start { get; }
    public IRandom Rng { get; }
    public int Round => Start.Round;
    public List<Hero> Heroes { get; }
    public List<Enemy> Enemies { get; }
    public List<Room> Rooms { get; }
    public int RoomIndex { get; set; }
    public int NextEnemyId { get; set; }
    public bool WardenSlain { get; set; }

    public HashSet<PlayerId> Guarding { get; } = [];
    public Dictionary<PlayerId, Direction> Ramparts { get; } = [];

    /// <summary>Héroes que atacaron o usaron Descarga esta ronda (Chispa no gana carga).</summary>
    public HashSet<PlayerId> Attacked { get; } = [];

    /// <summary>Héroes que entraron en sigilo esta ronda (su sigilo no se descuenta aún).</summary>
    public HashSet<PlayerId> FreshStealth { get; } = [];

    /// <summary>Enemigos congelados que pierden su acción (ejecutar, moverse y anunciar).</summary>
    public HashSet<int> SkippingEnemies { get; } = [];

    public Room Room
    {
        get => Rooms[RoomIndex];
        set => Rooms[RoomIndex] = value;
    }

    public long LastSeq => _log.LastSeq;
    public IReadOnlyList<DomainEvent> Events => _log.Events;

    public void Emit(DomainEvent domainEvent) => _log.Emit(domainEvent);

    /// <summary>Tirada de probabilidad. Solo consume azar si el resultado no es seguro.</summary>
    public bool Chance(int percent) => percent > 0 && (percent >= 100 || Rng.Next(100) < percent);

    public Hero Hero(PlayerId player) =>
        Heroes.Find(h => h.Player == player) ?? throw new InvalidOperationException($"Héroe desconocido: {player}.");

    public void Update(Hero hero) => Heroes[Heroes.FindIndex(h => h.Player == hero.Player)] = hero;

    public Enemy? Enemy(int id) => Enemies.Find(e => e.Id == id);

    public void Update(Enemy enemy)
    {
        var index = Enemies.FindIndex(e => e.Id == enemy.Id);
        if (index >= 0)
        {
            Enemies[index] = enemy;
        }
    }

    public Hero? HeroAt(Position position) => Heroes.Find(h => h.IsOnBoard && h.Position == position);

    public Enemy? EnemyAt(Position position) => Enemies.Find(e => e.Position == position);

    public bool IsOccupied(Position position) => HeroAt(position) is not null || EnemyAt(position) is not null;

    /// <summary>¿Se puede pisar el terreno? Muros, braseros, altares y cofres bloquean.</summary>
    public bool IsWalkable(Position position, bool flies, bool avoidCracks)
    {
        var terrain = Room.TerrainAt(position);
        if (terrain is Terrain.Wall or Terrain.Brazier or Terrain.Altar || Room.ChestAt(position) is not null)
        {
            return false;
        }

        return !(terrain == Terrain.Crack && avoidCracks && !flies);
    }

    public bool IsFreeFor(Position position, bool flies) =>
        IsWalkable(position, flies, avoidCracks: true) && !IsOccupied(position);

    /// <summary>Casillas libres ordenadas por cercanía (y luego fila y columna) para invocaciones.</summary>
    public List<Position> FreeTilesNear(Position origin) =>
        [.. Model.Room.AllPositions()
            .Where(p => IsFreeFor(p, flies: false))
            .OrderBy(p => p.DistanceTo(origin))
            .ThenBy(p => p.Y)
            .ThenBy(p => p.X)];

    public Enemy Spawn(EnemyKind kind, Position position, int? hp = null, bool isSplit = false)
    {
        var enemy = Model.Enemy.Create(NextEnemyId++, kind, position, hp) with { IsSplit = isSplit };
        Enemies.Add(enemy);
        Emit(new EnemySpawned(enemy.Id, kind, position) { Audience = Audience.At(position) });
        return enemy;
    }

    /// <summary>Audiencia de un hecho protagonizado por un héroe: en sigilo, solo él lo ve.</summary>
    public static Audience AudienceOf(Hero hero) =>
        hero.IsStealthed ? Audience.Only(hero.Player) : Audience.At(hero.Position);

    public Audience AudienceOf(EntityRef entity)
    {
        if (entity.Hero is { } player)
        {
            return AudienceOf(Hero(player));
        }

        return Enemy(entity.EnemyId ?? -1) is { } enemy ? Audience.At(enemy.Position) : Audience.Everyone;
    }
}
