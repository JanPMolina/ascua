using System.Reflection;
using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Tests.Support;
using Ascua.Domain.Views;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests;

/// <summary>Pruebas de no filtración: la vista de A nunca contiene secretos de B.</summary>
public sealed class PlayerViewTests
{
    private static readonly Position ChestNearB = new(7, 2);

    /// <summary>A en la esquina inferior izquierda, B en la superior derecha (distancia 12).</summary>
    private static GameState SecretsOfB()
    {
        var alice = Hero(A, HeroClass.Mampostero, 1, 7, seat: 0) with { Pact = PactKind.NeverFall };
        var bruno = Hero(B, HeroClass.Errata, 7, 1, seat: 1) with
        {
            Pact = PactKind.BetrayUnseen,
            Blessings = [Blessing.Lamp, Blessing.Greed],
            DraftOptions = [Blessing.Frost, Blessing.OwlEye],
        };
        var room = EmptyRoom() with
        {
            Chests = [new Chest(1, ChestNearB, ChestLoot.HealingPotion, HasSilverKey: true) { Opened = true, OpenedBy = B }],
        };
        return State([alice, bruno], [Enemy(1, EnemyKind.Crossbowman, 8, 0)], room);
    }

    [Fact]
    public void The_view_of_A_never_contains_the_secrets_of_B()
    {
        var state = SecretsOfB();

        var json = Json.Of(PlayerViewProjector.For(state, A));

        foreach (var secret in new[] { "BetrayUnseen", "Lamp", "Greed", "Frost", "OwlEye", "HealingPotion", "Crossbowman" })
        {
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        }

        Assert.Contains("NeverFall", json, StringComparison.Ordinal);
        Assert.Equal(2, PlayerViewProjector.For(state, A).Allies.Single().BlessingCount);
        Assert.Contains("BetrayUnseen", Json.Of(PlayerViewProjector.For(state, B)), StringComparison.Ordinal);
    }

    [Fact]
    public void Fog_of_war_only_sends_tiles_within_vision()
    {
        var view = PlayerViewProjector.For(SecretsOfB(), A);
        var origin = new Position(1, 7);

        Assert.All(view.Tiles, t => Assert.True(t.Position.DistanceTo(origin) <= HeroCatalog.VisionRadius));
        Assert.Equal(EmptyRoom().AllPositions().Count(p => p.DistanceTo(origin) <= HeroCatalog.VisionRadius), view.Tiles.Count);
        Assert.Empty(view.Enemies);
        Assert.Empty(view.Chests);
    }

    [Fact]
    public void The_lamp_shows_the_whole_room()
    {
        var view = PlayerViewProjector.For(SecretsOfB(), B);

        Assert.Equal(HeroCatalog.VisionRadius, view.Self.VisionRadius);
        Assert.Equal(Room.DefaultSize * Room.DefaultSize, view.Tiles.Count);
    }

    [Fact]
    public void Allies_are_visible_anywhere_unless_inside_a_host()
    {
        var state = SecretsOfB();
        Assert.Equal(new Position(7, 1), PlayerViewProjector.For(state, A).Allies.Single().Position);

        var hidden = state with { Heroes = [state.H(A), state.H(B) with { HostEnemyId = 1 }] };
        var ally = PlayerViewProjector.For(hidden, A).Allies.Single();

        Assert.Null(ally.Position);
        Assert.Null(ally.Facing);
        Assert.Equal(new Position(7, 1), PlayerViewProjector.For(hidden, B).Self.Position);
    }

    [Fact]
    public void Chest_contents_are_only_shown_to_whoever_opened_it()
    {
        var state = SecretsOfB();
        var nearby = state with { Heroes = [state.H(A) with { Position = new Position(6, 3) }, state.H(B)] };

        var forA = PlayerViewProjector.For(nearby, A).Chests.Single();
        var forB = PlayerViewProjector.For(nearby, B).Chests.Single();

        Assert.True(forA.Opened);
        Assert.Null(forA.Loot);
        Assert.Equal(ChestLoot.HealingPotion, forB.Loot);
    }

    [Fact]
    public void Plunderer_sees_silver_keys_in_closed_chests_and_nobody_else_does()
    {
        var room = EmptyRoom() with { Chests = [new Chest(1, new Position(4, 3), ChestLoot.Nothing, HasSilverKey: true)] };
        var plunderer = Hero(A, HeroClass.Errata, 4, 4, seat: 0) with { Blessings = [Blessing.Greed, Blessing.Lockpick] };
        var other = Hero(B, HeroClass.Mampostero, 5, 4, seat: 1);
        var state = State([plunderer, other], room: room);

        Assert.True(PlayerViewProjector.For(state, A).Chests.Single().HasSilverKey);
        Assert.Null(PlayerViewProjector.For(state, B).Chests.Single().HasSilverKey);
    }

    [Fact]
    public void Telegraphs_only_include_visible_tiles_unless_owl_eye()
    {
        var hero = Hero(A, HeroClass.Mampostero, 1, 7);
        var crossbow = Enemy(1, EnemyKind.Crossbowman, 8, 7) with
        {
            Telegraph = [.. Enumerable.Range(0, 8).Select(x => new Position(x, 7))],
        };
        var state = State([hero], [crossbow]);

        var plain = Assert.Single(PlayerViewProjector.For(state, A).Telegraphs);
        Assert.All(plain.Tiles, t => Assert.True(t.X <= 4));
        Assert.Null(plain.EnemyId);

        var owl = state with { Heroes = [hero with { Blessings = [Blessing.OwlEye] }] };
        Assert.Equal(8, Assert.Single(PlayerViewProjector.For(owl, A).Telegraphs).Tiles.Count);
    }

    [Fact]
    public void Private_and_distant_events_are_filtered_out()
    {
        var state = SecretsOfB();
        IReadOnlyList<DomainEvent> events =
        [
            new DraftOffered(B, [Blessing.Frost, Blessing.OwlEye]) { Audience = Audience.Only(B) },
            new ChestLooted(B, 1, ChestLoot.HealingPotion, true) { Audience = Audience.Only(B) },
            new EmbersChanged(B, 1, EmberReason.ChestOpened, 1) { Audience = Audience.Only(B) },
            new ChestOpened(B, 1) { Audience = Audience.At(ChestNearB) },
            new HeroDowned(B),
            new HeroGuarded(A) { Audience = Audience.At(new Position(1, 7)) },
        ];

        var forA = PlayerViewProjector.VisibleEvents(state, events, A);
        var forB = PlayerViewProjector.VisibleEvents(state, events, B);

        Assert.Equal(new[] { typeof(HeroDowned), typeof(HeroGuarded) }, forA.Select(e => e.GetType()));
        Assert.Equal(6, forB.Count);
    }

    [Fact]
    public void Views_have_no_member_that_could_carry_the_seed()
    {
        var types = typeof(PlayerView).Assembly.GetTypes().Where(t => t.Namespace == typeof(PlayerView).Namespace);
        var members = types.SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)).Select(p => p.Name);

        Assert.DoesNotContain(members, m => m.Contains("Seed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(GameState).GetProperties(), p => p.Name.Contains("Seed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pacts_are_revealed_to_everyone_when_the_match_ends()
    {
        var warden = Model.Enemy.Create(1, EnemyKind.Warden, new Position(4, 2), maxHp: 24) with { Hp = 1, Phase = 2 };
        var alice = Hero(A, HeroClass.Mampostero, 4, 3, seat: 0) with { Pact = PactKind.NeverFall };
        var bruno = Hero(B, HeroClass.Errata, 8, 8, seat: 1) with { Pact = PactKind.BetrayUnseen };

        var (finished, _) = Resolve(State([alice, bruno], [warden]), (A, Attack(Direction.North)));

        Assert.Contains("BetrayUnseen", Json.Of(PlayerViewProjector.For(finished, A)), StringComparison.Ordinal);
    }

    [Fact]
    public void Non_participants_get_no_view()
    {
        Assert.Throws<ArgumentException>(() => PlayerViewProjector.For(SecretsOfB(), new PlayerId("mallory")));
    }
}
