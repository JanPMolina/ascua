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
                Assert.All(view.Tiles, t => Assert.True(viewer.IsBlind || PlayerViewProjector.IsVisible(after, viewer, t.Position)));
                Assert.All(view.Enemies, e => Assert.True(PlayerViewProjector.CanSeeEnemy(after, viewer, after.Enemies.Single(x => x.Id == e.Id))));
                Assert.All(view.Chests, c => Assert.True(PlayerViewProjector.IsVisible(after, viewer, c.Position)));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void No_view_ever_leaks_another_bearers_path_choice_echoes_or_saved_embers(ulong seed)
    {
        foreach (var (_, after, events) in Play(seed))
        {
            foreach (var viewer in after.Heroes)
            {
                var visible = PlayerViewProjector.VisibleEvents(after, events, viewer.Player);
                Assert.DoesNotContain(visible, e => e is PathOffered o && o.Player != viewer.Player);
                Assert.DoesNotContain(visible, e => e is PathSelected s && s.Player != viewer.Player);
                Assert.DoesNotContain(visible, e => e is EchoGained g && g.Player != viewer.Player);
                Assert.All(visible.OfType<RenegadeSpawned>(), r => Assert.True(
                    r.Position is null || r.Player == viewer.Player || after.FindHero(r.Player)!.IsSameFaction(viewer)
                    || PlayerViewProjector.IsVisible(after, viewer, r.Position.Value)));

                var view = PlayerViewProjector.For(after, viewer.Player);
                Assert.Equal(viewer.SavedEmbers, view.Self.Path.SavedEmbers);
                Assert.Equal(viewer.Echoes, view.Self.Path.Echoes);
                var json = Json.Of(view.Allies);
                Assert.DoesNotContain("SavedEmbers", json, StringComparison.Ordinal);
                Assert.DoesNotContain("Echoes", json, StringComparison.Ordinal);
                Assert.DoesNotContain("PathChoice", json, StringComparison.Ordinal);

                // Del otro bando (Renegado frente al grupo) solo se ve la posición dentro de la visión.
                foreach (var ally in view.Allies.Where(a => a.Position is not null))
                {
                    var other = after.FindHero(ally.Player)!;
                    Assert.True(other.IsSameFaction(viewer) || PlayerViewProjector.IsVisible(after, viewer, ally.Position!.Value));
                }
            }
        }
    }

    [Fact]
    public void Random_matches_exercise_both_paths()
    {
        var events = Enumerable.Range(1, 40).SelectMany(seed => Play((ulong)seed).SelectMany(r => r.Events)).ToList();

        Assert.Contains(events, e => e is PathChosen { Path: PathKind.Friendship });
        Assert.Contains(events, e => e is PathChosen { Path: PathKind.Vengeance });
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Monsters_never_strike_renegades_or_embers(ulong seed)
    {
        foreach (var (before, _, events) in Play(seed))
        {
            var spared = before.Heroes.Where(h => h.IsRenegade || h.IsEmber).Select(h => h.Player).ToHashSet();
            Assert.DoesNotContain(events, e => e is EntityDamaged { Source.EnemyId: not null, Target.Hero: { } hero } && spared.Contains(hero));
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void No_view_ever_leaks_hidden_stowaways_debts_or_betrayals(ulong seed)
    {
        foreach (var (_, after, events) in Play(seed))
        {
            foreach (var viewer in after.Heroes)
            {
                var view = PlayerViewProjector.For(after, viewer.Player);
                foreach (var ally in view.Allies)
                {
                    var other = after.H(ally.Player);

                    // Polizón dentro de un huésped no aparece para nadie, aliados incluidos.
                    if (other.IsInHost)
                    {
                        Assert.Null(ally.Position);
                        Assert.Null(ally.Facing);
                    }

                    // La deuda solo la ven el deudor y la Usurera.
                    Assert.Equal(other.DebtTo == viewer.Player && other.Debt > 0 ? other.Debt : null, ally.DebtToMe);

                    // La Escucha solo ubica a otro portador a su alcance o si hizo ruido.
                    if (viewer.IsBlind && ally.Position is { } seen)
                    {
                        Assert.True(after.NoisyHeroes.Contains(ally.Player) || PlayerViewProjector.IsVisible(after, viewer, seen));
                    }
                }

                var visible = PlayerViewProjector.VisibleEvents(after, events, viewer.Player);
                Assert.DoesNotContain(visible, e => e is HostBoarded h && h.Player != viewer.Player);
                Assert.DoesNotContain(visible, e => e is HostDrained h && h.Player != viewer.Player);
                Assert.DoesNotContain(visible, e => e is BetrayalRecorded b && b.Traitor != viewer.Player);
                Assert.DoesNotContain(visible, e => e is DebtChanged d && d.Debtor != viewer.Player && d.Creditor != viewer.Player);
                Assert.DoesNotContain(visible, e => e is EnemyKilled { HiddenKiller: true } k && k.Killer is not null && k.Killer != viewer.Player);
                Assert.DoesNotContain(visible, e => e is SkillUsed { Hero: HeroClass.Errata } s && s.Player != viewer.Player);

                // Antes del final nadie ve las traiciones; después, todos.
                if (after.Phase != MatchPhase.Finished)
                {
                    Assert.Null(view.Outcome);
                    Assert.DoesNotContain(visible, e => e is BetrayalRevealed);
                }
                else
                {
                    Assert.Equal(after.Outcome!.Betrayals, view.Outcome!.Betrayals);
                }

                // Sin el estado completo, la vista no tiene de dónde sacar el huésped de otro.
                Assert.Equal(viewer.HostEnemyId, view.Self.HostEnemyId);
                if (!viewer.IsInHost)
                {
                    Assert.Null(view.Self.HostEnemyId);
                }
            }
        }
    }

    [Fact]
    public void Random_matches_exercise_all_nine_heroes()
    {
        var plays = Enumerable.Range(1, 40).SelectMany(seed => Play((ulong)seed)).ToList();

        Assert.Equal(Enum.GetValues<HeroClass>().Length, plays.SelectMany(p => p.Before.Heroes).Select(h => h.Class).Distinct().Count());
        var events = plays.SelectMany(p => p.Events).ToList();
        Assert.Contains(events, e => e is SkillUsed);
        Assert.Contains(events, e => e is EchoFired);
        Assert.Contains(events, e => e is HostBoarded);
        Assert.Contains(events, e => e is RoomShifted);
        Assert.Contains(events, e => e is Shouted);
        Assert.Contains(events, e => e is BellRung);
        Assert.Contains(events, e => e is EmbersDropped);
    }

    /// <summary>Juega una partida completa: draft automático y órdenes al azar de bots.</summary>
    private static IEnumerable<(GameState Before, GameState After, IReadOnlyList<DomainEvent> Events)> Play(ulong seed)
    {
        var bots = new SeededRandom(seed * 7919);
        // Como en las salas, sin repetir héroe; recorre los nueve según la semilla.
        var classes = Enum.GetValues<HeroClass>().OrderBy(_ => bots.Next(1000)).ToList();
        var players = Enumerable.Range(0, 2 + bots.Next(3))
            .Select(i => new PlayerSetup(new PlayerId($"bot-{i}"), classes[i]))
            .ToList();
        var rng = new SeededRandom(seed);
        var (state, _) = MatchFactory.Create(players, rng);
        state = MatchRules.AutoChooseBlessings(state).State;

        var types = Enum.GetValues<OrderType>();
        while (state.Phase == MatchPhase.InProgress)
        {
            var orders = state.Heroes
                .Where(h => h.CanAct)
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

            // Los bots eligen camino al azar; a veces no eligen y la resolución toma Amistad.
            foreach (var hero in state.Heroes.Where(h => h.IsChoosingPath).ToList())
            {
                var pick = bots.Next(3);
                if (pick < 2)
                {
                    state = MatchRules.ChoosePath(state, hero.Player, pick == 0 ? PathKind.Friendship : PathKind.Vengeance).State;
                }
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
            Assert.True(hero.SkillCooldown >= 0);
            Assert.Equal(hero.IsActive || hero.IsPresentRenegade, hero.Hp > 0);
            Assert.True(hero.SavedEmbers >= 0 && hero.Echoes >= 0);
            Assert.InRange(hero.EmberShieldBy.Count, 0, Math.Min(PathRules.MaxEmberShield, hero.Shield));
            Assert.Equal(hero.Status == HeroStatus.Extinct, hero.PathOfferPending);
            Assert.True(Room.IsInside(hero.Position));
            Assert.True(hero.SecondBody is null || (hero.Class == HeroClass.Hilvan && Room.IsInside(hero.SecondBody.Value)));
            Assert.True(hero.Debt >= 0);
            Assert.Equal(hero.Debt > 0, hero.DebtTo is not null);
            Assert.True(!hero.IsMiser || hero.PeakEmbers >= hero.Embers);
            if (hero.HostEnemyId is { } host)
            {
                Assert.Equal(HeroClass.Polizon, hero.Class);
                Assert.True(hero.IsVulnerable);
                Assert.Equal(after.Enemies.Single(e => e.Id == host).Position, hero.Position);
            }
        }

        // La sala nunca queda rota: cada casilla tiene a lo sumo una entidad y un Polizón en cada huésped.
        var hosts = after.Heroes.Where(h => h.IsInHost).Select(h => h.HostEnemyId!.Value).ToList();
        Assert.Equal(hosts.Count, hosts.Distinct().Count());

        foreach (var enemy in after.Enemies)
        {
            Assert.InRange(enemy.Hp, 1, enemy.MaxHp);
            Assert.True(Room.IsInside(enemy.Position));
        }

        var occupied = after.Heroes.Where(h => h.HasBody).SelectMany(h => h.Bodies)
            .Concat(after.Enemies.Select(e => e.Position))
            .ToList();
        Assert.Equal(occupied.Count, occupied.Distinct().Count());
    }
}
