using Ascua.Domain.Abstractions;
using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Domain.Views;

namespace Ascua.Domain.Tests.Support;

/// <summary>Azar falso: siempre devuelve el mismo valor (acotado al rango pedido).</summary>
internal sealed class FixedRandom(int value) : IRandom
{
    /// <summary>Ninguna tirada de probabilidad (&lt; 100%) tiene éxito.</summary>
    public static FixedRandom Never => new(99);

    /// <summary>Toda tirada de probabilidad tiene éxito.</summary>
    public static FixedRandom Always => new(0);

    public int Next(int maxExclusive) => Math.Min(value, maxExclusive - 1);
}

/// <summary>Azar falso que recorre una secuencia fija en bucle.</summary>
internal sealed class SequenceRandom(params int[] values) : IRandom
{
    private int _index;

    public int Next(int maxExclusive) => values[_index++ % values.Length] % maxExclusive;
}

/// <summary>Constructores de estados pequeños y legibles para las pruebas.</summary>
internal static class TestWorld
{
    public static readonly PlayerId A = new("alice");
    public static readonly PlayerId B = new("bruno");
    public static readonly PlayerId C = new("carla");
    public static readonly PlayerId D = new("dario");

    public static readonly Position Stairs = MatchFactory.StairsPosition;

    public static Hero Hero(PlayerId player, HeroClass heroClass, int x, int y, int seat = 0, Direction facing = Direction.North) =>
        Model.Hero.Create(player, seat, heroClass, new Position(x, y)) with { Facing = facing };

    public static Hero ExtinctHero(PlayerId player, HeroClass heroClass = HeroClass.Mampostero, int seat = 0, int saved = 0) =>
        Hero(player, heroClass, 2, 2, seat) with
        {
            Status = HeroStatus.Extinct,
            Hp = 0,
            Embers = 0,
            SavedEmbers = saved,
            Extinctions = 1,
            PathOfferPending = true,
        };

    public static Hero Ember(PlayerId player, int x, int y, int seat = 0, int saved = 0, int echoes = 0, HeroClass heroClass = HeroClass.Mampostero) =>
        Hero(player, heroClass, x, y, seat) with
        {
            Status = HeroStatus.EmberSpirit,
            Hp = 0,
            Embers = 0,
            SavedEmbers = saved,
            Echoes = echoes,
            Extinctions = 1,
        };

    public static Hero PresentRenegade(PlayerId player, HeroClass heroClass, int x, int y, int seat = 0) =>
        Hero(player, heroClass, x, y, seat) with { Status = HeroStatus.Renegade, Extinctions = 1 };

    /// <summary>Dos salas normales (la primera con escalera) y la del Celador al final: ninguna de las dos es la final.</summary>
    public static GameState TwoRooms(IEnumerable<Hero> heroes, IEnumerable<Enemy>? enemies = null, Room? first = null, Room? second = null) =>
        State(heroes, enemies) with
        {
            Rooms = [(first ?? EmptyRoom(0)).WithTerrain(Stairs, Terrain.Stairs), second ?? EmptyRoom(1), EmptyRoom(2)],
        };

    public static Enemy Enemy(int id, EnemyKind kind, int x, int y, Direction facing = Direction.South) =>
        Model.Enemy.Create(id, kind, new Position(x, y)) with { Facing = facing };

    public static Room EmptyRoom(int index = 0) => new() { Index = index, SpawnPoints = MatchFactory.SpawnPoints };

    public static GameState State(IEnumerable<Hero> heroes, IEnumerable<Enemy>? enemies = null, Room? room = null, int round = 1) => new()
    {
        Round = round,
        Phase = MatchPhase.InProgress,
        Heroes = [.. heroes],
        Enemies = [.. enemies ?? []],
        Rooms = [room ?? EmptyRoom()],
        NextEnemyId = 100,
    };

    public static Order Move(Direction d) => new(OrderType.Move, d);

    public static Order Attack(Direction d) => new(OrderType.Attack, d);

    public static Order Skill(Direction d) => new(OrderType.Skill, d);

    public static Order Interact(Direction d) => new(OrderType.Interact, d);

    public static (GameState State, IReadOnlyList<DomainEvent> Events) Resolve(
        GameState state,
        IRandom? rng = null,
        params (PlayerId Player, Order Order)[] orders) =>
        Resolver.Resolve(state, orders.ToDictionary(o => o.Player, o => o.Order), rng ?? FixedRandom.Never);

    public static (GameState State, IReadOnlyList<DomainEvent> Events) Resolve(
        GameState state,
        params (PlayerId Player, Order Order)[] orders) => Resolve(state, null, orders);

    public static Hero H(this GameState state, PlayerId player) => state.FindHero(player)!;

    public static AllyView Seen(this GameState state, PlayerId viewer, PlayerId other) =>
        PlayerViewProjector.For(state, viewer).Allies.Single(a => a.Player == other);

    public static IReadOnlyList<DomainEvent> SeenBy(this IReadOnlyList<DomainEvent> events, GameState state, PlayerId viewer) =>
        PlayerViewProjector.VisibleEvents(state, events, viewer);

    public static Position At(int x, int y) => new(x, y);

    public static Enemy? E(this GameState state, int id) => state.Enemies.Find(e => e.Id == id);
}
