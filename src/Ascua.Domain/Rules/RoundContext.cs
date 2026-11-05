using Ascua.Domain.Abstractions;
using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>Enemigo muerto esta ronda en la fase de acciones: quien lo mató y quién ya cobró su asistencia.</summary>
internal sealed record FallenEnemy(int EnemyId, Position Tile, PlayerId? Killer)
{
    public HashSet<PlayerId> Assisted { get; } = [];
}

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
        NextChestId = state.NextChestId;
        Betrayals = [.. state.Betrayals];
        BellRound = state.BellRound;
        BellRinger = state.BellRinger;
        BellClosed = state.BellClosed;
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
    public int NextChestId { get; set; }

    /// <summary>Se activó la escalera esta ronda: al terminar se abre este interludio.</summary>
    public InterludeState? OpeningInterlude { get; set; }

    /// <summary>Quién intentó dar un paso esta ronda (aunque rebotara).</summary>
    public HashSet<PlayerId> Stepped { get; } = [];

    /// <summary>Quién le dio un ataque a cada enemigo esta ronda.</summary>
    public Dictionary<int, HashSet<PlayerId>> Attackers { get; } = [];

    /// <summary>Enemigos que murieron en la fase de acciones: un ataque posterior que llegue allí es asistencia.</summary>
    public List<FallenEnemy> Corpses { get; } = [];

    public RoundPhase Phase
    {
        set => _log.Phase = value;
    }
    public bool WardenSlain { get; set; }

    public HashSet<PlayerId> Guarding { get; } = [];

    /// <summary>Enemigos que pierden su acción de la ronda (ejecutar, moverse y anunciar).</summary>
    public HashSet<int> SkippingEnemies { get; } = [];

    /// <summary>Faros encendidos esta ronda: Brasa → centro de la zona iluminada.</summary>
    public Dictionary<PlayerId, Position> Beacons { get; } = [];

    public List<Betrayal> Betrayals { get; }

    public HashSet<PlayerId> NoisyHeroes { get; } = [];

    public HashSet<int> NoisyEnemies { get; } = [];

    public HashSet<Position> NoiseTiles { get; } = [];

    public int BellRound { get; set; }

    public PlayerId? BellRinger { get; set; }

    public bool BellClosed { get; set; }

    /// <summary>¿Es la sala del Celador (la última)?</summary>
    public bool IsWardenRoom => RoomIndex == Rooms.Count - 1;

    public Room Room
    {
        get => Rooms[RoomIndex];
        set => Rooms[RoomIndex] = value;
    }

    public long LastSeq => _log.LastSeq;
    public IReadOnlyList<DomainEvent> Events => _log.Events;

    public void Emit(DomainEvent domainEvent) => _log.Emit(domainEvent);

    /// <summary>Suma ecos a una Brasa (evento privado).</summary>
    public void GainEchoes(PlayerId ember, int amount, EchoReason reason)
    {
        var hero = Hero(ember);
        if (amount <= 0 || !hero.IsEmber)
        {
            return;
        }

        Update(hero with { Echoes = hero.Echoes + amount });
        Emit(new EchoGained(ember, amount, reason, hero.Echoes + amount) { Audience = Audience.Only(ember) });
    }

    /// <summary>Hizo ruido este héroe (lo oye La Escucha). Las Brasas no hacen ruido, ni Polizón dentro de un huésped.</summary>
    public void MakeNoise(Hero hero)
    {
        if (!hero.HasBody)
        {
            return;
        }

        NoisyHeroes.Add(hero.Player);
        foreach (var body in hero.Bodies)
        {
            NoiseTiles.Add(body);
        }
    }

    public void MakeNoise(Enemy enemy)
    {
        NoisyEnemies.Add(enemy.Id);
        NoiseTiles.Add(enemy.Position);
    }

    public void ClearNoise()
    {
        NoisyHeroes.Clear();
        NoisyEnemies.Clear();
        NoiseTiles.Clear();
    }

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

    /// <summary>Héroe con un cuerpo en la casilla (Polizón dentro de un huésped no cuenta; la Brasa tampoco).</summary>
    public Hero? HeroAt(Position position) => Heroes.Find(h => h.HasBody && h.Occupies(position));

    public Enemy? EnemyAt(Position position) => Enemies.Find(e => e.Position == position);

    /// <summary>Polizón que va dentro de este enemigo.</summary>
    public Hero? PassengerOf(int enemyId) => Heroes.Find(h => h.HostEnemyId == enemyId);

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

    /// <summary>La casilla pedida si está libre; si no, la libre más cercana. <c>null</c> si no queda ninguna.</summary>
    public Position? FreeTileOrNearest(Position tile) =>
        IsFreeFor(tile, flies: false) ? tile : FreeTilesNear(tile).Select(p => (Position?)p).FirstOrDefault();

    public Enemy Spawn(EnemyKind kind, Position position, int? hp = null, bool isSplit = false)
    {
        var enemy = Model.Enemy.Create(NextEnemyId++, kind, position, EnemyCatalog.HpOf(kind, hp ?? EnemyCatalog.For(kind).MaxHp, Start.Curses)) with { IsSplit = isSplit };
        Enemies.Add(enemy);
        MakeNoise(enemy);
        Emit(new EnemySpawned(enemy.Id, kind, position) { Audience = Audience.At(position) });
        return enemy;
    }

    /// <summary>Audiencia de un hecho protagonizado por un héroe: dentro de un huésped, solo él lo ve.</summary>
    public static Audience AudienceOf(Hero hero) =>
        hero.IsInHost ? Audience.Only(hero.Player) : Audience.At(hero.Position);

    public Audience AudienceOf(EntityRef entity)
    {
        if (entity.Hero is { } player)
        {
            return AudienceOf(Hero(player));
        }

        return Enemy(entity.EnemyId ?? -1) is { } enemy ? Audience.At(enemy.Position) : Audience.Everyone;
    }

    /// <summary>Lo primero que cumpla <paramref name="find"/> en línea, a lo sumo a <paramref name="range"/> casillas.</summary>
    public static T? FirstInLine<T>(Position origin, Direction direction, int range, Func<Position, T?> find)
        where T : class
    {
        var position = origin;
        for (var i = 0; i < range; i++)
        {
            position = position.Step(direction);
            if (find(position) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
