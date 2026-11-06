using Ascua.Domain.Abstractions;
using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Domain.Tests.Support;
using Ascua.Domain.Views;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests.V3;

/// <summary>06-version-3.md §5, §6, §8, §9 y §10.</summary>
public class GoldMissionsBlessingsTests
{
    private static Enemy Warden(int hp, int x = 4, int y = 3) =>
        Model.Enemy.Create(1, EnemyKind.Warden, At(x, y), 20) with { Hp = hp, Phase = 2 };

    // ───── Celador

    [Fact]
    public void The_warden_shares_six_by_damage_with_largest_remainders_and_one_for_the_final_blow()
    {
        var a = Hero(A, HeroClass.Mampostero, 4, 4, seat: 0) with { Progress = new PactProgress { WardenDamage = 3 } };
        var b = Hero(B, HeroClass.Errata, 0, 8, seat: 1) with { Progress = new PactProgress { WardenDamage = 1 } };
        var c = Hero(C, HeroClass.Campanero, 1, 8, seat: 2);

        var (next, _) = Resolve(State([a, b, c], [Warden(1)]), (A, Attack(Direction.North)));

        // Daño 4 y 1 de 5 en total: 6×4/5 = 4,8 y 6×1/5 = 1,2. Enteras 4 y 1; el que sobra, al resto mayor (A). Más 1 al golpe final.
        Assert.Equal(6, next.H(A).Embers);
        Assert.Equal(1, next.H(B).Embers);
        Assert.Equal(0, next.H(C).Embers);
        Assert.True(next.H(A).Progress.KilledWarden);
    }

    [Fact]
    public void A_tie_in_the_remainders_goes_to_the_lower_seat()
    {
        var shares = new List<(PlayerId, int, int)> { (B, 1, 1), (A, 0, 1), (C, 2, 2) };

        var split = Outcomes.SplitByLargestRemainder(shares, 6);

        Assert.Equal(new[] { (A, 2), (B, 1), (C, 3) }, split);
    }

    [Fact]
    public void Excess_damage_does_not_count_and_the_retired_do_not_collect()
    {
        var a = Hero(A, HeroClass.Mampostero, 4, 4, seat: 0) with { Blessings = [Blessing.StoneFist], Hp = 3 };
        var b = Hero(B, HeroClass.Errata, 0, 8, seat: 1) with { Status = HeroStatus.Retired, Progress = new PactProgress { WardenDamage = 5 } };

        var (next, _) = Resolve(State([a, b], [Warden(1)]), (A, Attack(Direction.North)));

        Assert.Equal(1, next.H(A).Progress.WardenDamage);
        Assert.Equal(7, next.H(A).Embers);
        Assert.Equal(0, next.H(B).Embers);
    }

    // ───── Asistencia

    [Fact]
    public void Two_attacks_on_an_enemy_that_dies_give_an_assist_to_the_second()
    {
        var a = Hero(A, HeroClass.Polizon, 4, 4, seat: 0);
        var b = Hero(B, HeroClass.Mampostero, 3, 3, seat: 1);
        var enemy = Model.Enemy.Create(1, EnemyKind.Scavenger, At(4, 3), 1);

        var (next, events) = Resolve(State([a, b], [enemy]), (A, Attack(Direction.North)), (B, Attack(Direction.East)));

        Assert.Equal(1, next.H(A).Embers);
        Assert.Equal(1, next.H(B).Embers);
        Assert.Equal(1, next.H(A).Progress.Kills);
        Assert.Equal(0, next.H(B).Progress.Kills);
        Assert.Contains(events, e => e is EmbersChanged { Reason: EmberReason.Assist } c && c.Player == B);
        Assert.Equal(A, events.OfType<EnemyKilled>().Single().Killer);
    }

    [Fact]
    public void Whoever_hit_before_the_final_blow_also_gets_it()
    {
        var a = Hero(A, HeroClass.Polizon, 4, 4, seat: 0);
        var b = Hero(B, HeroClass.Mampostero, 3, 3, seat: 1);
        var enemy = Model.Enemy.Create(1, EnemyKind.Scavenger, At(4, 3), 2);

        var (next, _) = Resolve(State([a, b], [enemy]), (A, Attack(Direction.North)), (B, Attack(Direction.East)));

        // A pega primero sin matar y B da el golpe final.
        Assert.Equal(1, next.H(A).Embers);
        Assert.Equal(1, next.H(B).Embers);
        Assert.Equal(1, next.H(B).Progress.Kills);
    }

    [Fact]
    public void An_attack_that_would_not_have_reached_gets_nothing_and_a_long_line_keeps_going()
    {
        var a = Hero(A, HeroClass.Polizon, 4, 4, seat: 0);
        var far = Hero(B, HeroClass.Mampostero, 0, 0, seat: 1);
        var first = Model.Enemy.Create(1, EnemyKind.Scavenger, At(4, 3), 1);

        var (missed, _) = Resolve(State([a, far], [first]), (A, Attack(Direction.North)), (B, Attack(Direction.East)));
        Assert.Equal(0, missed.H(B).Embers);

        var escucha = Hero(B, HeroClass.Escucha, 2, 3, seat: 1);
        var behind = Enemy(2, EnemyKind.Totem, 6, 3);
        var (line, _) = Resolve(State([a, escucha], [first, behind]), (A, Attack(Direction.North)), (B, Attack(Direction.East)));
        Assert.Equal(1, line.H(B).Embers);
        Assert.True(line.E(2)!.Hp < 3);
    }

    // ───── Derrota colectiva

    [Fact]
    public void A_collective_defeat_has_no_winners()
    {
        var a = Hero(A, HeroClass.Mampostero, 4, 4) with { Hp = 1 };
        var enemy = Enemy(1, EnemyKind.Scavenger, 4, 3) with { Telegraph = [At(4, 4)] };

        var (next, _) = Resolve(State([a], [enemy]), (A, Interact(Direction.North)));

        Assert.Equal(OutcomeKind.CollectiveDefeat, next.Outcome!.Kind);
        Assert.Empty(next.Outcome.Winners);
    }

    // ───── Misiones

    private static GameState Drafting(IRandom rng, int players = 4)
    {
        var setups = new[] { A, B, C, D }.Take(players).Select((p, i) => new PlayerSetup(p, (HeroClass)i)).ToList();
        return MatchFactory.Create(setups, rng).State;
    }

    [Fact]
    public void Each_player_is_offered_two_different_missions_and_the_match_waits_for_them()
    {
        var state = Drafting(new SeededRandom(7));

        foreach (var hero in state.Heroes)
        {
            Assert.Equal(2, hero.PactOptions.Select(o => o.Kind).Distinct().Count());
            Assert.All(hero.PactOptions, o => Assert.Contains(o.Kind, PactRules.Offered));
        }

        foreach (var hero in state.Heroes)
        {
            state = MatchRules.ChooseBlessing(state, hero.Player, 0).State;
        }

        Assert.Equal(MatchPhase.Drafting, state.Phase);
        foreach (var hero in state.Heroes)
        {
            state = MatchRules.ChoosePact(state, hero.Player, 1).State;
        }

        Assert.Equal(MatchPhase.InProgress, state.Phase);
    }

    [Fact]
    public void The_chosen_mission_and_its_target_stay_with_the_owner_and_the_deadline_picks_the_first()
    {
        var state = Drafting(new SeededRandom(11));
        var expected = state.H(A).PactOptions[0];

        var auto = MatchRules.AutoChooseBlessings(state).State;

        Assert.Equal(MatchPhase.InProgress, auto.Phase);
        Assert.Equal(expected.Kind, auto.H(A).Pact);
        Assert.Equal(expected.Target, auto.H(A).PactTarget);
    }

    [Fact]
    public void A_mission_error_is_typed()
    {
        var state = Drafting(new SeededRandom(11));

        Assert.Equal(ErrorCode.NoPactOffer, MatchRules.ChoosePact(state, A, 5).Error!.Kind);
        var done = MatchRules.ChoosePact(state, A, 0).State;
        Assert.Equal(ErrorCode.NoPactOffer, MatchRules.ChoosePact(done, A, 0).Error!.Kind);
    }

    [Fact]
    public void Missions_and_targets_are_only_revealed_to_their_owner()
    {
        for (ulong seed = 0; seed < 30; seed++)
        {
            var state = Drafting(new SeededRandom(seed));
            var target = state.Heroes.SelectMany(h => h.PactOptions.Select(o => (h.Player, o.Target))).FirstOrDefault(x => x.Target is not null);
            if (target.Target is null)
            {
                continue;
            }

            var owner = target.Player;
            var other = state.Heroes.First(h => h.Player != owner).Player;
            var offer = state.H(owner).PactOptions.First(o => o.Target is not null);
            var mine = PlayerViewProjector.For(state, owner);
            var theirs = PlayerViewProjector.For(state, other);
            Assert.Contains(offer, mine.Self.PactOptions);
            Assert.DoesNotContain("Pact", Json.Of(theirs.Allies));
            Assert.DoesNotContain(offer, theirs.Self.PactOptions);

            // Elegida, sigue siendo solo suya: ni la misión ni el objetivo salen en lo que ven los demás.
            var chosen = MatchRules.ChoosePact(state, owner, state.H(owner).PactOptions.IndexOf(offer));
            Assert.Empty(chosen.Events.SeenBy(chosen.State, other));
            Assert.Equal(offer.Target, PlayerViewProjector.For(chosen.State, owner).Self.PactTarget);
            Assert.DoesNotContain("Pact", Json.Of(PlayerViewProjector.For(chosen.State, other).Allies));
            return;
        }

        Assert.Fail("Ninguna semilla ofreció la misión con objetivo.");
    }

    public static TheoryData<PactKind, PactProgress, bool> ProgressCases => new()
    {
        { PactKind.OpenThreeChests, new PactProgress { ChestsOpened = 3 }, true },
        { PactKind.OpenThreeChests, new PactProgress { ChestsOpened = 2 }, false },
        { PactKind.ReviveTwoAllies, new PactProgress { Revives = 2 }, true },
        { PactKind.BetrayTwiceUnconvicted, new PactProgress { Betrayals = 2 }, false },
        { PactKind.PushTwoIntoHazard, new PactProgress { HazardPushes = 2 }, true },
        { PactKind.CorrectConviction, new PactProgress(), false },
        { PactKind.MostWardenDamage, new PactProgress(), false },
        { PactKind.SlayWarden, new PactProgress { KilledWarden = true }, false },
        { PactKind.NeverFall, new PactProgress(), false },
    };

    [Theory]
    [MemberData(nameof(ProgressCases))]
    public void Missions_read_the_progress(PactKind pact, PactProgress progress, bool expected)
    {
        var hero = Hero(A, HeroClass.Mampostero, 1, 1) with { Pact = pact, Progress = progress };

        Assert.Equal(expected, PactRules.IsFulfilled(hero, [hero]));
    }

    [Fact]
    public void Most_warden_damage_is_shared_on_a_tie()
    {
        var a = Hero(A, HeroClass.Mampostero, 1, 1) with { Pact = PactKind.MostWardenDamage, Progress = new PactProgress { WardenDamage = 3 } };
        var b = Hero(B, HeroClass.Errata, 2, 1) with { Pact = PactKind.MostWardenDamage, Progress = new PactProgress { WardenDamage = 3 } };
        var c = Hero(C, HeroClass.Escucha, 3, 1) with { Pact = PactKind.MostWardenDamage, Progress = new PactProgress { WardenDamage = 1 } };

        Assert.True(PactRules.IsFulfilled(a, [a, b, c]));
        Assert.True(PactRules.IsFulfilled(b, [a, b, c]));
        Assert.False(PactRules.IsFulfilled(c, [a, b, c]));
    }

    [Fact]
    public void Outgold_compares_final_gold_with_the_secret_target()
    {
        var a = Hero(A, HeroClass.Mampostero, 1, 1) with { Pact = PactKind.OutgoldTarget, PactTarget = B, Embers = 3 };
        var poor = Hero(B, HeroClass.Errata, 2, 1) with { Embers = 2 };
        var rich = poor with { Embers = 3 };

        Assert.True(PactRules.IsFulfilled(a, [a, poor]));
        Assert.False(PactRules.IsFulfilled(a, [a, rich]));
    }

    // ───── Mejoras

    [Fact]
    public void The_catalog_describes_all_twelve_with_a_cost_and_no_percentages()
    {
        Assert.Equal(Enum.GetValues<Blessing>().Length, BlessingCatalog.All.Count);
        Assert.Equal(12, BlessingCatalog.All.Select(b => b.Blessing).Distinct().Count());
        Assert.All(BlessingCatalog.All, b =>
        {
            Assert.False(string.IsNullOrWhiteSpace(b.Cost));
            Assert.DoesNotContain("%", b.Effect + b.Cost);
        });
        Assert.Equal(3, BlessingCatalog.Synergies.Count);
    }

    [Fact]
    public void Living_ember_always_burns_and_chests_burn_the_owner()
    {
        var hero = Hero(A, HeroClass.Mampostero, 4, 4) with { Blessings = [Blessing.LivingEmber] };
        var room = EmptyRoom() with { Chests = [new Chest(1, At(4, 3), ChestLoot.Nothing, false)] };

        var (next, _) = Resolve(State([hero], room: room), (A, Interact(Direction.North)));

        Assert.Equal(5, next.H(A).Hp);
    }

    [Fact]
    public void Fire_step_hurts_its_owner_one_more_and_inferno_makes_him_immune()
    {
        var room = EmptyRoom() with { Fires = [new Fire(At(4, 4), 2, 1)] };
        var burned = Hero(A, HeroClass.Mampostero, 4, 4) with { Blessings = [Blessing.FireStep] };
        var (afterBurn, _) = Resolve(State([burned], room: room), (A, Interact(Direction.North)));
        Assert.Equal(4, afterBurn.H(A).Hp);

        var inferno = burned with { Blessings = [Blessing.FireStep, Blessing.LivingEmber] };
        var (afterInferno, _) = Resolve(State([inferno], room: room), (A, Interact(Direction.North)));
        Assert.Equal(6, afterInferno.H(A).Hp);
    }

    [Fact]
    public void Stone_fist_hits_harder_only_when_planted()
    {
        var hero = Hero(A, HeroClass.Mampostero, 4, 4) with { Blessings = [Blessing.StoneFist] };
        var totem = Enemy(1, EnemyKind.Totem, 4, 3);

        var (planted, _) = Resolve(State([hero], [totem]), (A, Attack(Direction.North)));
        Assert.Equal(1, planted.E(1)!.Hp);

        var besideTotem = hero with { Position = At(5, 4) };
        var (stepped, _) = Resolve(State([besideTotem], [totem]), (A, Attack(Direction.North) with { Step = Direction.West }));
        Assert.Equal(2, stepped.E(1)!.Hp);

        // Un paso que rebotó también cuenta como paso.
        var walled = EmptyRoom().WithTerrain(At(3, 4), Terrain.Wall);
        var (bounced, _) = Resolve(State([hero], [totem], walled), (A, Attack(Direction.North) with { Step = Direction.West }));
        Assert.Equal(2, bounced.E(1)!.Hp);
    }

    [Fact]
    public void Double_heart_cannot_be_healed()
    {
        var hero = Hero(A, HeroClass.Mampostero, 4, 4) with { Blessings = [Blessing.DoubleHeart], Hp = 3 };
        var room = EmptyRoom() with { Chests = [new Chest(1, At(4, 3), ChestLoot.HealingPotion, false)] };

        var (next, _) = Resolve(State([hero], room: room), (A, Interact(Direction.North)));

        Assert.Equal(3, next.H(A).Hp);
        Assert.Equal(8, next.H(A).MaxHp);
    }

    [Fact]
    public void Hungry_blade_heals_on_a_final_blow_but_cannot_guard()
    {
        var hero = Hero(A, HeroClass.Mampostero, 4, 4) with { Blessings = [Blessing.HungryBlade], Hp = 3 };
        var enemy = Model.Enemy.Create(1, EnemyKind.Scavenger, At(4, 3), 1);
        var (next, _) = Resolve(State([hero], [enemy]), (A, Attack(Direction.North)));
        Assert.Equal(4, next.H(A).Hp);

        var hitter = Enemy(2, EnemyKind.Scavenger, 4, 2) with { Telegraph = [At(4, 4)] };
        var (guarded, _) = Resolve(State([hero], [hitter]), (A, Order.DefaultGuard));
        Assert.Equal(2, guarded.H(A).Hp);
    }

    [Fact]
    public void Broken_mirror_never_holds_a_shield()
    {
        var hero = Hero(A, HeroClass.Mampostero, 4, 4) with { Shield = 2 };

        var granted = BlessingRules.Grant(hero, Blessing.BrokenMirror);
        Assert.Equal(0, granted.Shield);

        var room = EmptyRoom() with { Chests = [new Chest(1, At(4, 3), ChestLoot.Shield, false)] };
        var (next, _) = Resolve(State([granted], room: room), (A, Interact(Direction.North)));
        Assert.Equal(0, next.H(A).Shield);
    }

    [Fact]
    public void The_lamp_attracts_every_monster()
    {
        var lamp = Hero(A, HeroClass.Mampostero, 8, 8, seat: 0) with { Blessings = [Blessing.Lamp] };
        var near = Hero(B, HeroClass.Errata, 4, 5, seat: 1);
        var enemy = Enemy(1, EnemyKind.Scavenger, 4, 4);

        var (next, _) = Resolve(State([lamp, near], [enemy]), (A, Order.DefaultGuard), (B, Order.DefaultGuard));

        // Tenía a B al lado, pero se va hacia la Lámpara sin anunciarle nada.
        Assert.DoesNotContain(At(4, 5), next.E(1)!.Telegraph);
        Assert.True(next.E(1)!.Position.DistanceTo(At(8, 8)) < At(4, 4).DistanceTo(At(8, 8)));
    }

    [Fact]
    public void Lockpick_cannot_lift_an_ally()
    {
        var hero = Hero(A, HeroClass.Mampostero, 4, 4, seat: 0) with { Blessings = [Blessing.Lockpick] };
        var fallen = Hero(B, HeroClass.Errata, 4, 3, seat: 1) with { Status = HeroStatus.Down, Hp = 0 };

        var (next, _) = Resolve(State([hero, fallen]), (A, Interact(Direction.North)));

        Assert.Equal(HeroStatus.Down, next.H(B).Status);
    }

    [Fact]
    public void Last_breath_once_and_then_nobody_can_lift_him()
    {
        var hero = Hero(A, HeroClass.Mampostero, 4, 4, seat: 0);
        var spent = Hero(B, HeroClass.Errata, 4, 3, seat: 1) with
        {
            Blessings = [Blessing.LastBreath],
            LastBreathUsed = true,
            Status = HeroStatus.Down,
            Hp = 0,
        };

        var (next, _) = Resolve(State([hero, spent]), (A, Interact(Direction.North)));

        Assert.Equal(HeroStatus.Down, next.H(B).Status);
        Assert.False(next.H(B).CanBeRevived);
    }

    [Fact]
    public void Frost_freezes_without_damage_except_the_warden()
    {
        var hero = Hero(A, HeroClass.Mampostero, 4, 4) with { Blessings = [Blessing.Frost] };
        var enemy = Enemy(1, EnemyKind.Scavenger, 4, 3);

        var (next, events) = Resolve(State([hero], [enemy]), (A, Attack(Direction.North)));
        Assert.Equal(2, next.E(1)!.Hp);
        Assert.Contains(events, e => e is ActionLost);

        var (warden, _) = Resolve(State([hero], [Warden(5)]), (A, Attack(Direction.North)));
        Assert.Equal(4, warden.E(1)!.Hp);
    }

    [Fact]
    public void Owl_eye_sees_all_telegraphs_and_gives_up_one_of_vision()
    {
        var hero = Hero(A, HeroClass.Mampostero, 0, 0) with { Blessings = [Blessing.OwlEye] };
        var enemy = Enemy(1, EnemyKind.Scavenger, 8, 8) with { Telegraph = [At(8, 7)] };

        var view = PlayerViewProjector.For(State([hero], [enemy]), A);

        Assert.Equal(2, view.Self.VisionRadius);
        Assert.Contains(view.Telegraphs, t => t.Tiles.Contains(At(8, 7)));
    }

    [Fact]
    public void Plunderer_sees_every_chest_of_the_room_and_whether_it_has_a_key()
    {
        var hero = Hero(A, HeroClass.Mampostero, 0, 0) with { Blessings = [Blessing.Greed, Blessing.Lockpick] };
        var room = EmptyRoom() with { Chests = [new Chest(1, At(8, 8), ChestLoot.Nothing, true)] };

        var view = PlayerViewProjector.For(State([hero], room: room), A);

        Assert.True(Assert.Single(view.Chests).HasSilverKey);
    }

    // ───── Maldiciones

    [Fact]
    public void Curses_change_monsters_warden_and_vision_and_are_public()
    {
        var setups = new[] { new PlayerSetup(A, HeroClass.Errata), new PlayerSetup(B, HeroClass.Avaro) };
        var plain = MatchFactory.Create(setups, new SeededRandom(3)).State;
        var cursed = MatchFactory.Create(setups, new SeededRandom(3), curses: [Curse.Fierce, Curse.WardenPlus, Curse.Dim]).State;

        Assert.Equal(plain.Rooms[0].InitialEnemies.Select(e => e.Hp + 1), cursed.Rooms[0].InitialEnemies.Select(e => e.Hp));
        var wardenPlain = plain.Rooms[^1].InitialEnemies.Single(e => e.Kind == EnemyKind.Warden).Hp;
        var wardenCursed = cursed.Rooms[^1].InitialEnemies.Single(e => e.Kind == EnemyKind.Warden).Hp;
        Assert.Equal(((wardenPlain * 3) + 1) / 2, wardenCursed);
        Assert.Equal(plain.VisionOf(plain.H(A)) - 1, cursed.VisionOf(cursed.H(A)));
        Assert.Equal(3, PlayerViewProjector.For(cursed, B).Curses.Count);
    }

    [Fact]
    public void Dim_and_a_dark_room_add_up_with_a_minimum_of_one()
    {
        var state = State([Hero(A, HeroClass.Mampostero, 1, 1)], room: EmptyRoom() with { Type = RoomType.Dark }) with { Curses = [Curse.Dim] };

        Assert.Equal(1, state.VisionOf(state.H(A)));
    }

    [Fact]
    public void Fierce_also_reaches_summoned_monsters()
    {
        var state = State([Hero(A, HeroClass.Mampostero, 0, 0)], [Model.Enemy.Create(1, EnemyKind.Totem, At(4, 4), 2) with { SummonTimer = EnemyCatalog.TotemSummonEvery - 1 }]) with
        {
            Curses = [Curse.Fierce],
        };

        var (next, _) = Resolve(state, (A, Order.DefaultGuard));

        Assert.Equal(2, next.Enemies.Single(e => e.Kind == EnemyKind.Scavenger).Hp);
    }

    // ───── Títulos

    [Fact]
    public void Awards_are_only_for_someone_who_earned_them_and_ties_share_them()
    {
        var a = Hero(A, HeroClass.Mampostero, 1, 1) with { Progress = new PactProgress { Betrayals = 2, ChestsOpened = 1 } };
        var b = Hero(B, HeroClass.Errata, 2, 1, seat: 1) with { Progress = new PactProgress { Betrayals = 2, WardenDamage = 3, GoldAtDeath = 4 } };

        var awards = Awards.Compute([a, b]);

        Assert.Equal(2, awards.Count(x => x.Kind == AwardKind.MostBetrayals));
        Assert.Contains(awards, x => x is { Kind: AwardKind.MostWardenDamage, Value: 3 } && x.Player == B);
        Assert.Contains(awards, x => x is { Kind: AwardKind.RichestDeath, Value: 4 } && x.Player == B);
        Assert.DoesNotContain(awards, x => x.Kind == AwardKind.MostTrialGoldLost);
        Assert.DoesNotContain(awards, x => x.Kind == AwardKind.MostRevives);
        Assert.DoesNotContain(awards, x => x.Kind == AwardKind.MostPushes);
    }

    [Fact]
    public void The_outcome_carries_the_awards()
    {
        var a = Hero(A, HeroClass.Mampostero, 4, 4);

        var (next, _) = Resolve(State([a], [Warden(1)]), (A, Attack(Direction.North)));

        Assert.Contains(next.Outcome!.Awards, x => x.Kind == AwardKind.MostKills && x.Player == A);
        Assert.Contains(next.Outcome.Awards, x => x.Kind == AwardKind.MostWardenDamage && x.Player == A);
    }
}
