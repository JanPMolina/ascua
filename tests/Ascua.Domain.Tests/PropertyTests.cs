using Ascua.Domain.Abstractions;
using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Domain.Tests.Support;
using Ascua.Domain.Views;

namespace Ascua.Domain.Tests;

/// <summary>
/// Propiedades sobre partidas completas jugadas por bots al azar: se generan con muchas
/// semillas y se comprueban invariantes en cada ronda.
/// </summary>
public sealed class PropertyTests
{
    public static TheoryData<ulong> Seeds()
    {
        var data = new TheoryData<ulong>();
        for (ulong seed = 1; seed <= 40; seed++)
        {
            data.Add(seed);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Invariants_hold_in_every_round_of_a_random_match(ulong seed)
    {
        foreach (var (before, after, events) in Play(seed))
        {
            AssertInvariants(before, after, events);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void The_same_input_with_the_same_random_source_gives_the_same_result(ulong seed)
    {
        var first = Play(seed).ToList();
        var second = Play(seed).ToList();

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(Json.Of(first[^1].After), Json.Of(second[^1].After));
        Assert.Equal(Json.Of(first.SelectMany(r => r.Events)), Json.Of(second.SelectMany(r => r.Events)));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void No_view_ever_leaks_another_bearers_pact_or_blessings(ulong seed)
    {
        foreach (var (_, after, events) in Play(seed))
        {
            if (after.Phase == MatchPhase.Finished)
            {
                continue;
            }

            foreach (var viewer in after.Heroes)
            {
                var view = PlayerViewProjector.For(after, viewer.Player);
                Assert.All(view.Allies, ally => Assert.Equal(after.FindHero(ally.Player)!.Blessings.Count, ally.BlessingCount));

                var visible = PlayerViewProjector.VisibleEvents(after, events, viewer.Player);
                Assert.DoesNotContain(visible, e => e is DraftOffered d && d.Player != viewer.Player);
                Assert.DoesNotContain(visible, e => e is BlessingChosen b && b.Player != viewer.Player);
                Assert.DoesNotContain(visible, e => e is ChestLooted c && c.Player != viewer.Player);
                Assert.DoesNotContain(visible, e => e is EmbersChanged c && c.Player != viewer.Player);
                Assert.All(view.Tiles, t => Assert.True(t.Position.DistanceTo(viewer.Position) <= viewer.VisionRadius));
                Assert.All(view.Enemies, e => Assert.True(e.Position.DistanceTo(viewer.Position) <= viewer.VisionRadius));
            }
        }
    }

    /// <summary>Juega una partida completa: draft automático y órdenes al azar de bots.</summary>
    private static IEnumerable<(GameState Before, GameState After, IReadOnlyList<DomainEvent> Events)> Play(ulong seed)
    {
        var bots = new SeededRandom(seed * 7919);
        var classes = Enum.GetValues<HeroClass>();
        var players = Enumerable.Range(0, 2 + bots.Next(3))
            .Select(i => new PlayerSetup(new PlayerId($"bot-{i}"), classes[bots.Next(classes.Length)]))
            .ToList();
        var rng = new SeededRandom(seed);
        var (state, _) = MatchFactory.Create(players, rng);
        state = MatchRules.AutoChooseBlessings(state).State;

        var types = Enum.GetValues<OrderType>();
        while (state.Phase == MatchPhase.InProgress)
        {
            var orders = state.Heroes
                .Where(h => h.IsActive)
                .ToDictionary(
                    h => h.Player,
                    h =>
                    {
                        var type = types[bots.Next(types.Length)];
                        return type == OrderType.Guard ? Order.DefaultGuard : new Order(type, Directions.All[bots.Next(4)]);
                    });

            foreach (var hero in state.Heroes.Where(h => !h.DraftOptions.IsEmpty).ToList())
            {
                state = MatchRules.ChooseBlessing(state, hero.Player, 0).State;
            }

            var (next, events) = Resolver.Resolve(state, orders, rng);
            yield return (state, next, events);
            state = next;
        }
    }

    private static void AssertInvariants(GameState before, GameState after, IReadOnlyList<DomainEvent> events)
    {
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => before.LastSeq + i), events.Select(e => e.Seq));
        Assert.All(events, e => Assert.Equal(before.Round, e.Round));
        Assert.Equal(after.Phase == MatchPhase.Finished ? before.Round : before.Round + 1, after.Round);
        Assert.InRange(after.Round, 1, after.MaxRounds);

        foreach (var hero in after.Heroes)
        {
            Assert.InRange(hero.Hp, 0, Math.Max(hero.MaxHp, before.FindHero(hero.Player)!.Hp));
            Assert.True(hero.Embers >= 0);
            Assert.True(hero.Shield >= 0);
            Assert.InRange(hero.Charges, 0, HeroCatalog.MaxCharges);
            Assert.True(hero.SkillCooldown >= 0);
            Assert.Equal(hero.Status == HeroStatus.Active, hero.Hp > 0);
            Assert.True(Room.IsInside(hero.Position));
        }

        foreach (var enemy in after.Enemies)
        {
            Assert.InRange(enemy.Hp, 1, enemy.MaxHp);
            Assert.True(Room.IsInside(enemy.Position));
        }

        var occupied = after.Heroes.Where(h => h.IsOnBoard).Select(h => h.Position)
            .Concat(after.Enemies.Select(e => e.Position))
            .ToList();
        Assert.Equal(occupied.Count, occupied.Distinct().Count());
    }
}
