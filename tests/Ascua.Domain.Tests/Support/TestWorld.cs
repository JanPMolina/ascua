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

    /// <summary>
    /// Un héroe con vida y ascuas fijas: estas pruebas son de reglas, no de balance, así que un ajuste en
    /// <see cref="HeroCatalog"/> no debe romperlas. Los valores reales los cubre <c>MatchFactoryTests</c>.
    /// </summary>
    public static Hero Hero(PlayerId player, HeroClass heroClass, int x, int y, int seat = 0, Direction facing = Direction.North)
    {
        var hero = Model.Hero.Create(player, seat, heroClass, new Position(x, y)) with { Facing = facing };
        return heroClass switch
        {
            HeroClass.Hilvan => hero with { Hp = 5 },
            HeroClass.Polizon => hero with { Hp = 3 },
            HeroClass.Avaro => hero with { Embers = 3, PeakEmbers = 3 },
            _ => hero,
        };
    }

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

    /// <summary>Un enemigo con vida fija, por lo mismo que <see cref="Hero"/>: resiste un golpe aunque el catálogo baje.</summary>
    public static Enemy Enemy(int id, EnemyKind kind, int x, int y, Direction facing = Direction.South)
    {
        int? hp = kind switch
        {
            EnemyKind.Scavenger or EnemyKind.Slime or EnemyKind.Crossbowman => 2,
            EnemyKind.Totem => 3,
            _ => null,
        };
        return Model.Enemy.Create(id, kind, new Position(x, y), hp) with { Facing = facing };
    }

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

    public static Order Sabotage(Direction d) => new(OrderType.Sabotage, d);

    public static Order Push(Direction d) => new(OrderType.Push, d);

    /// <summary>
    /// Una ronda y, si abre el interludio, lo cierra sin puertas ni votos: la mayoría de las pruebas no van de eso y usan
    /// las salas que pusieron a mano. Las del interludio usan <see cref="ResolveRaw"/>.
    /// </summary>
    public static (GameState State, IReadOnlyList<DomainEvent> Events) Resolve(
        GameState state,
        IRandom? rng = null,
        params (PlayerId Player, Order Order)[] orders)
    {
        var (next, events) = ResolveRaw(state, rng, orders);
        if (next.Phase != MatchPhase.Interlude)
        {
            return (next, events);
        }

        var closed = MatchRules.CloseInterlude(next with { Interlude = next.Interlude! with { Doors = [] } }, new HashSet<PlayerId>(), rng ?? FixedRandom.Never);
        return (closed.State, [.. events, .. closed.Events]);
    }

    /// <summary>Una ronda tal cual, sin cerrar el interludio.</summary>
    public static (GameState State, IReadOnlyList<DomainEvent> Events) ResolveRaw(
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
