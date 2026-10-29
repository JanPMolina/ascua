using Ascua.Domain.Abstractions;
using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;

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

    public static Hero Hero(PlayerId player, HeroClass heroClass, int x, int y, int seat = 0, Direction facing = Direction.North) =>
        Model.Hero.Create(player, seat, heroClass, new Position(x, y)) with { Facing = facing };

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

    public static Enemy? E(this GameState state, int id) => state.Enemies.Find(e => e.Id == id);
}
