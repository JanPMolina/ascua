using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Domain.Tests.Support;
using Ascua.Domain.Views;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests;

/// <summary>Caminos tras la extinción (planteamiento/04-caminos-tras-morir.md): elección, Brasa errante y Renegado.</summary>
public sealed class PathTests
{
    // ───────────── Fórmulas ─────────────

    [Theory]
    [InlineData(10, 0, 3)]
    [InlineData(10, 3, 5)]
    [InlineData(10, 7, 9)]
    [InlineData(10, 8, 9)]
    [InlineData(10, 30, 9)]
    [InlineData(100, 0, 25)]
    [InlineData(100, 3, 49)]
    [InlineData(100, 7, 81)]
    [InlineData(100, 8, 85)]
    [InlineData(100, 50, 85)]
    [InlineData(7, 0, 2)]
    [InlineData(0, 5, 0)]
    public void Rebirth_embers_are_25_percent_plus_8_per_echo_rounded_up_with_an_85_percent_cap(int saved, int echoes, int expected)
    {
        Assert.Equal(expected, PathRules.RebirthEmbers(saved, echoes));
    }

    [Theory]
    [InlineData(6, 3)]
    [InlineData(5, 3)]
    [InlineData(4, 2)]
    [InlineData(7, 4)]
    public void Rebirth_life_is_half_the_maximum_rounded_up(int maxHp, int expected) =>
        Assert.Equal(expected, PathRules.RebirthHp(maxHp));

    [Theory]
    [InlineData(6, false, 4)]
    [InlineData(6, true, 3)]
    [InlineData(4, false, 3)]
    [InlineData(4, true, 2)]
    [InlineData(5, true, 2)]
    public void A_renegade_returns_with_60_percent_of_its_life_or_40_in_the_warden_room(int maxHp, bool wardenRoom, int expected) =>
        Assert.Equal(expected, PathRules.RenegadeHp(maxHp, wardenRoom));

    [Theory]
    [InlineData(5, 3)]
    [InlineData(4, 2)]
    [InlineData(0, 0)]
    public void A_renegade_keeps_half_its_saved_embers_rounded_up(int saved, int expected) =>
        Assert.Equal(expected, PathRules.RenegadeEmbers(saved));

    // ───────────── Elección ─────────────

    [Fact]
    public void Choosing_a_path_is_private_until_the_round_resolves()
    {
        var state = TwoRooms([ExtinctHero(A, saved: 5), Hero(B, HeroClass.Mampostero, 0, 8, seat: 1)]);

        var result = MatchRules.ChoosePath(state, A, PathKind.Vengeance);

        Assert.True(result.IsSuccess);
        Assert.Equal(PathKind.Vengeance, result.State.H(A).PathChoice);
        Assert.Equal(HeroStatus.Extinct, result.State.H(A).Status);
        var selected = Assert.IsType<PathSelected>(Assert.Single(result.Events));
        Assert.Equal(Audience.Only(A), selected.Audience);
        Assert.Empty(PlayerViewProjector.VisibleEvents(result.State, result.Events, B));
        Assert.Equal(HeroStatus.Extinct, PlayerViewProjector.For(result.State, B).Allies.Single().Status);
        Assert.Equal(PathKind.Vengeance, PlayerViewProjector.For(result.State, A).Self.Path.Choice);
    }

    [Fact]
    public void Only_the_first_choice_counts_and_only_an_extinct_bearer_can_choose()
    {
        var state = TwoRooms([ExtinctHero(A), Hero(B, HeroClass.Mampostero, 0, 8, seat: 1)]);
        var chosen = MatchRules.ChoosePath(state, A, PathKind.Friendship).State;

        Assert.Equal("NO_PATH_OFFER", MatchRules.ChoosePath(chosen, A, PathKind.Vengeance).Error?.Code);
        Assert.Equal("NO_PATH_OFFER", MatchRules.ChoosePath(state, B, PathKind.Friendship).Error?.Code);
        Assert.Equal("NOT_A_PARTICIPANT", MatchRules.ChoosePath(state, C, PathKind.Friendship).Error?.Code);
        Assert.Equal("INVALID_PATH_CHOICE", MatchRules.ChoosePath(state, A, (PathKind)7).Error?.Code);
        Assert.Equal("MATCH_NOT_IN_PROGRESS", MatchRules.ChoosePath(state with { Phase = MatchPhase.Finished }, A, PathKind.Friendship).Error?.Code);
    }

    [Fact]
    public void When_the_deadline_expires_friendship_is_chosen()
    {
        var state = TwoRooms([ExtinctHero(A), Hero(B, HeroClass.Mampostero, 0, 8, seat: 1)]);

        var result = MatchRules.AutoChoosePaths(state);

        Assert.Equal(PathKind.Friendship, result.State.H(A).PathChoice);
        Assert.Contains(result.Events, e => e is PathSelected { Path: PathKind.Friendship, Auto: true });
    }

    [Fact]
    public void An_unchosen_offer_becomes_friendship_when_the_round_resolves()
    {
        var state = TwoRooms([ExtinctHero(A, saved: 6), Hero(B, HeroClass.Mampostero, 0, 8, seat: 1)]);

        var (next, events) = Resolve(state, FixedRandom.Never);

        Assert.Equal(HeroStatus.EmberSpirit, next.H(A).Status);
        Assert.False(next.H(A).PathOfferPending);
        Assert.Equal(6, next.H(A).SavedEmbers);
        Assert.False(next.H(A).BeaconActive);
        var chosen = Assert.Single(events.OfType<PathChosen>());
        Assert.Equal(AudienceKind.Everyone, chosen.Audience.Kind);
        Assert.Contains(events, e => e is PathSelected { Auto: true });
    }

    [Fact]
    public void Choosing_vengeance_reveals_a_renegade_that_waits_for_the_next_room_with_half_its_embers()
    {
        var state = TwoRooms([ExtinctHero(A, saved: 5) with { PathChoice = PathKind.Vengeance }, Hero(B, HeroClass.Mampostero, 0, 8, seat: 1)]);

        var (next, events) = Resolve(state, FixedRandom.Never);

        var renegade = next.H(A);
        Assert.Equal(HeroStatus.Renegade, renegade.Status);
        Assert.True(renegade.AwaitingSpawn);
        Assert.Equal(0, renegade.SpawnRound);
        Assert.Equal(3, renegade.Embers);
        Assert.Equal(0, renegade.SavedEmbers);
        Assert.False(renegade.IsOnBoard);
        Assert.Contains(events, e => e is PathChosen { Path: PathKind.Vengeance });
        Assert.Null(PlayerViewProjector.For(next, B).Allies.Single().Position);
    }

    // ───────────── Brasa errante ─────────────

    [Fact]
    public void Drift_moves_up_to_two_tiles_through_walls_and_enemies_without_leaving_the_room()
    {
        var room = EmptyRoom(0).WithTerrain(new Position(4, 3), Terrain.Wall);
        var state = TwoRooms([Ember(A, 4, 4), Hero(B, HeroClass.Mampostero, 0, 8, seat: 1)], [Enemy(1, EnemyKind.Totem, 4, 2)], room);

        var (next, events) = Resolve(state, (A, Move(Direction.North)));
        Assert.Equal(new Position(4, 2), next.H(A).Position);
        Assert.Contains(events, e => e is HeroMoved { Audience.Kind: AudienceKind.Group });

        var (edge, _) = Resolve(next with { Heroes = [next.H(A) with { Position = new Position(4, 1) }, next.H(B)] }, (A, Move(Direction.North)));
        Assert.Equal(new Position(4, 0), edge.H(A).Position);
    }

    [Fact]
    public void Kindle_gives_one_shield_to_the_first_ally_in_line_up_to_two_from_embers()
    {
        var state = TwoRooms([Ember(A, 4, 4), Hero(B, HeroClass.Mampostero, 4, 2, seat: 1)]);

        var (next, events) = Resolve(state, (A, Skill(Direction.North)));

        Assert.Equal(1, next.H(B).Shield);
        Assert.Equal(new[] { A }, next.H(B).EmberShieldBy);
        Assert.Contains(events, e => e is EmberAbilityUsed { Ability: EmberAbility.Kindle, Target.Hero: { } t } && t == B);

        var full = next with { Heroes = [next.H(A), next.H(B) with { Shield = 2, EmberShieldBy = [A, A] }] };
        var (capped, _) = Resolve(full, (A, Skill(Direction.North)));
        Assert.Equal(2, capped.H(B).Shield);
    }

    [Fact]
    public void Each_point_of_kindled_shield_that_absorbs_damage_is_an_echo()
    {
        var mampostero = Hero(B, HeroClass.Mampostero, 4, 5, seat: 1) with { Shield = 1, EmberShieldBy = [A] };
        var striker = Enemy(1, EnemyKind.Scavenger, 4, 4) with { Telegraph = [new Position(4, 5)] };
        var state = TwoRooms([Ember(A, 0, 0), mampostero], [striker]);

        var (next, events) = Resolve(state, (B, Interact(Direction.East)));

        Assert.Equal(6, next.H(B).Hp);
        Assert.Equal(0, next.H(B).Shield);
        Assert.Empty(next.H(B).EmberShieldBy);
        Assert.Equal(1, next.H(A).Echoes);
        var echo = Assert.Single(events.OfType<EchoGained>());
        Assert.Equal(EchoReason.ShieldAbsorbed, echo.Reason);
        Assert.Equal(Audience.Only(A), echo.Audience);
    }

    [Fact]
    public void Rekindle_lifts_an_adjacent_fallen_ally_with_one_life_for_three_echoes()
    {
        // Sin la Calidez (que lo curaría al terminar la ronda junto a la Brasa) para ver la vida de Reavivar.
        var fallen = Hero(B, HeroClass.Usurera, 4, 3, seat: 1) with { Hp = 0, Status = HeroStatus.Down, DownRounds = 1, WarmthCooldown = 3 };
        var state = TwoRooms([Ember(A, 4, 4), fallen, Hero(C, HeroClass.Campanero, 8, 8, seat: 2)]);

        var (next, events) = Resolve(state, (A, Interact(Direction.North)));

        Assert.Equal(HeroStatus.Active, next.H(B).Status);
        Assert.Equal(1, next.H(B).Hp);
        Assert.Equal(3, next.H(A).Echoes);
        Assert.Equal(0, next.H(A).Embers);
        Assert.Contains(events, e => e is HeroRevived r && r.Reviver == A && r.Target == B);
    }

    [Fact]
    public void Hinder_delays_an_announced_attack_by_one_round_and_earns_an_echo_if_it_would_hit_a_hero()
    {
        var striker = Enemy(1, EnemyKind.Scavenger, 4, 4) with { Telegraph = [new Position(4, 3)] };
        var state = TwoRooms([Ember(A, 4, 6), Hero(B, HeroClass.Mampostero, 4, 3, seat: 1)], [striker]);

        var (delayed, events) = Resolve(state, (A, Attack(Direction.North)), (B, Interact(Direction.East)));

        Assert.Equal(6, delayed.H(B).Hp);
        Assert.Equal(new[] { new Position(4, 3) }, delayed.E(1)!.Telegraph);
        Assert.Equal(1, delayed.H(A).Echoes);
        Assert.Equal(1, delayed.H(A).HinderCooldown);
        Assert.Contains(events, e => e is EmberAbilityUsed { Ability: EmberAbility.Hinder, Target.EnemyId: 1 });
        Assert.Equal("SKILL_ON_COOLDOWN", OrderValidator.Validate(delayed, A, delayed.Round, Attack(Direction.North))?.Code);

        var (hit, _) = Resolve(delayed, (B, Interact(Direction.East)));
        Assert.Equal(5, hit.H(B).Hp);
    }

    [Fact]
    public void Hinder_does_not_work_on_the_warden_in_phase_two()
    {
        var warden = Model.Enemy.Create(1, EnemyKind.Warden, new Position(4, 4), maxHp: 24) with { Hp = 10, Phase = 2 };
        var state = State([Ember(A, 4, 6), Hero(B, HeroClass.Mampostero, 0, 8, seat: 1)], [warden]);

        var (next, _) = Resolve(state, (A, Attack(Direction.North)));

        Assert.False(next.E(1)!.Delayed);
        Assert.Equal(0, next.H(A).HinderCooldown);
        Assert.Equal(0, next.H(A).Echoes);
    }

    [Fact]
    public void Beacon_shares_a_radius_two_light_with_the_group_and_an_ally_kill_inside_it_is_an_echo()
    {
        var moth = Enemy(1, EnemyKind.Moth, 4, 5);
        var renegade = PresentRenegade(C, HeroClass.Campanero, 8, 0, seat: 2);
        var state = TwoRooms([Ember(A, 4, 4), Hero(B, HeroClass.Mampostero, 4, 6, seat: 1), renegade], [moth]);

        var (next, events) = Resolve(state, (B, Attack(Direction.North)));

        Assert.True(next.H(A).BeaconActive);
        Assert.Equal(1, next.H(A).Echoes);
        Assert.Contains(events, e => e is EchoGained { Reason: EchoReason.BeaconKill });

        var far = next with { Heroes = [next.H(A), next.H(B) with { Position = new Position(0, 8) }, next.H(C)] };
        Assert.Contains(PlayerViewProjector.For(far, B).Tiles, t => t.Position == new Position(6, 4));
        Assert.DoesNotContain(PlayerViewProjector.For(far, C).Tiles, t => t.Position == new Position(4, 4));
    }

    [Fact]
    public void Warmth_heals_an_adjacent_wounded_ally_once_every_three_rounds()
    {
        var state = TwoRooms([Ember(A, 4, 4), Hero(B, HeroClass.Mampostero, 4, 5, seat: 1) with { Hp = 3 }]);

        var (first, events) = Resolve(state, FixedRandom.Never);
        Assert.Equal(4, first.H(B).Hp);
        Assert.Equal(1, first.H(A).Echoes);
        Assert.Contains(events, e => e is EmberAbilityUsed { Ability: EmberAbility.Warmth });

        var state2 = first;
        for (var i = 0; i < 2; i++)
        {
            state2 = Resolve(state2, FixedRandom.Never).State;
            Assert.Equal(4, state2.H(B).Hp);
        }

        var (fourth, _) = Resolve(state2, FixedRandom.Never);
        Assert.Equal(5, fourth.H(B).Hp);
        Assert.Equal(2, fourth.H(A).Echoes);
    }

    [Fact]
    public void Monsters_ignore_the_ember_and_nothing_hurts_it()
    {
        var striker = Enemy(1, EnemyKind.Scavenger, 4, 3) with { Telegraph = [new Position(4, 4)] };
        var state = TwoRooms([Ember(A, 4, 4), Hero(B, HeroClass.Mampostero, 0, 8, seat: 1)], [striker]);

        var (next, events) = Resolve(state, (A, Move(Direction.South)));

        Assert.Equal(HeroStatus.EmberSpirit, next.H(A).Status);
        Assert.DoesNotContain(events, e => e is EntityDamaged { Target.Hero: { } t } && t == A);
    }

    [Fact]
    public void The_ember_is_reborn_at_the_entrance_of_the_next_room_with_half_life_and_its_share_of_embers()
    {
        var ember = Ember(A, 0, 0, saved: 10, echoes: 7) with
        {
            Blessings = [Blessing.Lamp],
            Pact = PactKind.NeverFall,
            SkillCooldown = 1,
            HinderCooldown = 2,
        };
        var state = TwoRooms([ember, Hero(B, HeroClass.Usurera, 4, 1, seat: 1)]);

        var (next, events) = Resolve(state, (B, Move(Direction.North)));

        Assert.Equal(1, next.RoomIndex);
        var reborn = next.H(A);
        Assert.Equal(HeroStatus.Active, reborn.Status);
        Assert.Equal(3, reborn.Hp);
        Assert.Equal(9, reborn.Embers);
        Assert.Equal(0, reborn.SavedEmbers);
        Assert.Equal(0, reborn.Echoes);
        Assert.Equal(new[] { Blessing.Lamp }, reborn.Blessings);
        Assert.Equal(PactKind.NeverFall, reborn.Pact);
        Assert.Equal(0, reborn.SkillCooldown);
        Assert.Equal(0, reborn.HinderCooldown);
        Assert.Equal(MatchFactory.SpawnPoints[0], reborn.Position);
        Assert.Contains(events, e => e is EmberReborn { Hp: 3 } r && r.Player == A);
        Assert.Contains(events, e => e is EmbersChanged { Reason: EmberReason.Rebirth, Delta: 9 } c && c.Audience == Audience.Only(A));
    }

    [Fact]
    public void In_the_warden_room_the_ember_stays_an_ember_and_can_win_with_the_rebirth_formula()
    {
        var warden = Model.Enemy.Create(1, EnemyKind.Warden, new Position(4, 2), maxHp: 24) with { Hp = 1, Phase = 2 };
        var mampostero = Hero(A, HeroClass.Mampostero, 4, 3, seat: 0) with { Pact = PactKind.OpenThreeChests };
        var ember = Ember(B, 8, 8, seat: 1, saved: 10, echoes: 7) with { Pact = PactKind.ReviveTwoAllies };

        var (next, _) = Resolve(State([mampostero, ember], [warden]), (A, Attack(Direction.North)));

        Assert.Equal(OutcomeKind.Victory, next.Outcome!.Kind);
        Assert.Equal(B, Assert.Single(next.Outcome.Winners));
        Assert.Equal(9, next.Outcome.Scores.Single(s => s.Player == B).Embers);
        Assert.Equal(HeroStatus.EmberSpirit, next.H(B).Status);
    }

    // ───────────── Renegado ─────────────

    [Fact]
    public void A_renegade_appears_in_the_next_room_far_from_the_heroes_with_one_monster_less_and_a_shield_for_each_hero()
    {
        var second = EmptyRoom(1) with { InitialEnemies = [Enemy(7, EnemyKind.Scavenger, 4, 4), Enemy(8, EnemyKind.Moth, 2, 2)] };
        var renegade = Hero(A, HeroClass.Mampostero, 0, 0) with { Status = HeroStatus.Renegade, AwaitingSpawn = true, Embers = 3 };
        var state = TwoRooms([renegade, Hero(B, HeroClass.Usurera, 4, 1, seat: 1), Hero(C, HeroClass.Campanero, 8, 8, seat: 2)], second: second);

        var (next, events) = Resolve(state, (B, Move(Direction.North)));

        Assert.Equal(1, next.RoomIndex);
        Assert.Equal(8, Assert.Single(next.Enemies).Id);
        var spawned = next.H(A);
        Assert.True(spawned.IsPresentRenegade);
        Assert.Equal(4, spawned.Hp);
        Assert.Equal(new Position(0, 0), spawned.Position);
        Assert.All([next.H(B), next.H(C)], h => Assert.True(h.Position.DistanceTo(spawned.Position) >= PathRules.RenegadeMinDistance));
        Assert.Equal(1, next.H(B).Shield);
        Assert.Equal(1, next.H(C).Shield);
        Assert.Contains(events, e => e is RenegadeSpawned r && r.Player == A);
        var forB = PlayerViewProjector.VisibleEvents(next, events, B).OfType<RenegadeSpawned>().Single();
        Assert.Null(forB.Position);
    }

    [Fact]
    public void A_room_never_loses_its_last_monster_to_renegades()
    {
        var second = EmptyRoom(1) with { InitialEnemies = [Enemy(7, EnemyKind.Scavenger, 4, 4)] };
        var renegade = Hero(A, HeroClass.Mampostero, 0, 0) with { Status = HeroStatus.Renegade, AwaitingSpawn = true };
        var state = TwoRooms([renegade, Hero(B, HeroClass.Usurera, 4, 1, seat: 1)], second: second);

        var (next, _) = Resolve(state, (B, Move(Direction.North)));

        Assert.Single(next.Enemies);
    }

    [Fact]
    public void In_the_warden_room_a_renegade_returns_two_rounds_later_next_to_the_warden_weakening_it()
    {
        var warden = Model.Enemy.Create(1, EnemyKind.Warden, new Position(4, 2), maxHp: 24);
        var chooser = ExtinctHero(A, HeroClass.Usurera, saved: 4) with { PathChoice = PathKind.Vengeance };
        var state = State([chooser, Hero(B, HeroClass.Mampostero, 8, 8, seat: 1), Hero(C, HeroClass.Campanero, 0, 8, seat: 2)], [warden], round: 5);

        var (revealed, _) = Resolve(state, (B, Interact(Direction.East)), (C, Interact(Direction.East)));
        Assert.Equal(7, revealed.H(A).SpawnRound);
        Assert.True(revealed.H(A).AwaitingSpawn);
        Assert.Equal(2, revealed.H(A).Embers);

        var (waiting, _) = Resolve(revealed, (B, Interact(Direction.East)), (C, Interact(Direction.East)));
        Assert.True(waiting.H(A).AwaitingSpawn);

        var (spawned, _) = Resolve(waiting, (B, Interact(Direction.East)), (C, Interact(Direction.East)));
        var renegade = spawned.H(A);
        Assert.True(renegade.IsPresentRenegade);
        Assert.Equal(2, renegade.Hp);
        Assert.Equal(PathRules.RenegadeFinalSkillLock, renegade.SkillCooldown);
        Assert.Equal(1, renegade.Position.DistanceTo(spawned.E(1)!.Position));
        Assert.Equal(18, spawned.E(1)!.MaxHp);
        Assert.Equal(18, spawned.E(1)!.Hp);
        Assert.Equal(1, spawned.H(B).Shield);
        Assert.Equal("SKILL_ON_COOLDOWN", OrderValidator.Validate(spawned, A, spawned.Round, Skill(Direction.North))?.Code);
    }

    [Fact]
    public void The_warden_never_drops_below_twelve_maximum_life_and_summons_a_single_totem_with_renegades()
    {
        var weak = Model.Enemy.Create(1, EnemyKind.Warden, new Position(4, 2), maxHp: 12);
        var chooser = Hero(A, HeroClass.Usurera, 0, 0) with { Status = HeroStatus.Renegade, AwaitingSpawn = true, SpawnRound = 1 };
        var (next, _) = Resolve(State([chooser, Hero(B, HeroClass.Mampostero, 8, 8, seat: 1)], [weak]), (B, Interact(Direction.East)));
        Assert.Equal(12, next.E(1)!.MaxHp);

        var warden = Model.Enemy.Create(1, EnemyKind.Warden, new Position(4, 2), maxHp: 24) with { Hp = 13 };
        var mampostero = Hero(B, HeroClass.Mampostero, 4, 1, seat: 1, facing: Direction.South);
        var renegade = PresentRenegade(A, HeroClass.Usurera, 8, 8);
        var (enraged, _) = Resolve(State([renegade, mampostero], [warden]), (B, Attack(Direction.South)));
        Assert.Equal(2, enraged.E(1)!.Phase);
        Assert.Single(enraged.Enemies, e => e.Kind == EnemyKind.Totem);
    }

    [Fact]
    public void The_first_hit_of_a_renegade_on_each_hero_is_absorbed_and_later_hits_deal_at_most_one_and_steal_an_ember()
    {
        var renegade = PresentRenegade(A, HeroClass.Errata, 4, 4) with { Blessings = [Blessing.StoneFist] };
        var mampostero = Hero(B, HeroClass.Mampostero, 4, 3, seat: 1, facing: Direction.North) with { Embers = 3 };
        var state = TwoRooms([renegade, mampostero, Hero(C, HeroClass.Campanero, 8, 8, seat: 2)]);

        var (warned, events) = Resolve(state, (A, Attack(Direction.North)), (B, Interact(Direction.East)));
        Assert.Equal(6, warned.H(B).Hp);
        Assert.Contains(events, e => e is HitPrevented { Reason: PreventionReason.WarningShield });
        Assert.Equal(new[] { B }, warned.H(A).WarnedHeroes);

        var (hit, _) = Resolve(warned, (A, Attack(Direction.North)), (B, Interact(Direction.East)));
        Assert.Equal(5, hit.H(B).Hp);
        Assert.Equal(2, hit.H(B).Embers);
        Assert.Equal(1, hit.H(A).Embers);
    }

    [Fact]
    public void Downing_a_hero_gives_the_renegade_two_more_embers()
    {
        var renegade = PresentRenegade(A, HeroClass.Mampostero, 4, 4) with { WarnedHeroes = [B] };
        var usurera = Hero(B, HeroClass.Usurera, 4, 3, seat: 1) with { Hp = 1, Embers = 2 };
        var state = TwoRooms([renegade, usurera, Hero(C, HeroClass.Campanero, 8, 8, seat: 2)]);

        var (next, _) = Resolve(state, (A, Attack(Direction.North)), (B, Interact(Direction.East)));

        Assert.Equal(HeroStatus.Down, next.H(B).Status);
        Assert.Equal(3, next.H(A).Embers);
        Assert.Equal(1, next.H(B).Embers);
    }

    [Fact]
    public void A_renegade_cannot_harm_monsters_monsters_do_not_attack_it_and_it_cannot_open_chests()
    {
        var chest = new Chest(1, new Position(5, 6), ChestLoot.Nothing, HasSilverKey: false);
        var room = EmptyRoom(0) with { Chests = [chest] };
        var renegade = PresentRenegade(A, HeroClass.Usurera, 4, 6);
        var scavenger = Enemy(1, EnemyKind.Scavenger, 4, 4) with { Telegraph = [new Position(4, 6)] };
        var state = TwoRooms([renegade, Hero(B, HeroClass.Mampostero, 0, 0, seat: 1)], [scavenger], room);

        var (shot, _) = Resolve(state, (A, Attack(Direction.North)));
        Assert.Equal(2, shot.E(1)!.Hp);
        Assert.Equal(renegade.Hp, shot.H(A).Hp);

        var (opened, _) = Resolve(state, (A, Interact(Direction.East)));
        Assert.False(opened.Rooms[0].Chests.Single().Opened);
    }

    [Fact]
    public void A_renegade_skill_cooldown_is_doubled()
    {
        var renegade = PresentRenegade(A, HeroClass.Campanero, 4, 4);
        var state = TwoRooms([renegade, Hero(B, HeroClass.Campanero, 0, 0, seat: 1)]);

        var (next, _) = Resolve(state, (A, Skill(Direction.North)), (B, Skill(Direction.North)));

        Assert.Equal((HeroCatalog.For(HeroClass.Campanero).SkillCooldown * 2) - 1, next.H(A).SkillCooldown);
        Assert.Equal(HeroCatalog.For(HeroClass.Campanero).SkillCooldown - 1, next.H(B).SkillCooldown);
    }

    [Fact]
    public void Defeating_a_renegade_gives_two_embers_and_it_chooses_again_and_can_turn_into_an_ember()
    {
        var renegade = PresentRenegade(A, HeroClass.Usurera, 4, 3) with { Hp = 1, Embers = 4 };
        var mampostero = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1);
        var state = TwoRooms([renegade, mampostero]);

        var (defeated, events) = Resolve(state, (B, Attack(Direction.North)), (A, Interact(Direction.East)));

        var fallen = defeated.H(A);
        Assert.Equal(HeroStatus.Extinct, fallen.Status);
        Assert.Equal(2, fallen.Extinctions);
        Assert.True(fallen.IsChoosingPath);
        Assert.Equal(4, fallen.SavedEmbers);
        Assert.Equal(2, defeated.H(B).Embers);
        Assert.Contains(events, e => e is RenegadeDefeated d && d.Renegade == A && d.By == B);
        Assert.Contains(events, e => e is HeroExtinguished { Reason: ExtinctionReason.Defeated });
        Assert.Contains(events, e => e is PathOffered { Extinctions: 2 });

        var chosen = MatchRules.ChoosePath(defeated, A, PathKind.Friendship).State;
        var (ember, _) = Resolve(chosen, FixedRandom.Never);
        Assert.Equal(HeroStatus.EmberSpirit, ember.H(A).Status);
        Assert.Equal(4, ember.H(A).SavedEmbers);
    }

    [Fact]
    public void A_hero_hitting_a_renegade_deals_full_damage_without_stealing()
    {
        var renegade = PresentRenegade(A, HeroClass.Mampostero, 4, 3) with { Embers = 2 };
        var mampostero = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1) with { Blessings = [Blessing.StoneFist] };

        var (next, _) = Resolve(TwoRooms([renegade, mampostero]), (B, Attack(Direction.North)), (A, Interact(Direction.East)));

        Assert.Equal(renegade.Hp - 2, next.H(A).Hp);
        Assert.Equal(2, next.H(A).Embers);
        Assert.Equal(0, next.H(B).Embers);
    }

    // ───────────── Fin de partida ─────────────

    [Fact]
    public void With_every_hero_down_and_a_renegade_standing_the_threshold_claims()
    {
        var errata = Hero(B, HeroClass.Errata, 4, 5, seat: 1) with { Hp = 1 };
        var striker = Enemy(1, EnemyKind.Scavenger, 4, 4) with { Telegraph = [new Position(4, 5)] };
        var renegade = PresentRenegade(A, HeroClass.Campanero, 0, 0) with { Embers = 4 };

        var (next, events) = Resolve(TwoRooms([renegade, errata], [striker]), (B, Interact(Direction.East)));

        Assert.Equal(OutcomeKind.ThresholdClaimed, next.Outcome!.Kind);
        Assert.Equal(A, Assert.Single(next.Outcome.Winners));
        Assert.Contains(events, e => e is MatchEnded);
    }

    [Fact]
    public void Tied_renegades_share_the_threshold()
    {
        var errata = Hero(C, HeroClass.Errata, 4, 5, seat: 2) with { Hp = 1 };
        var striker = Enemy(1, EnemyKind.Scavenger, 4, 4) with { Telegraph = [new Position(4, 5)] };
        var first = PresentRenegade(A, HeroClass.Campanero, 0, 0) with { Embers = 3 };
        var second = PresentRenegade(B, HeroClass.Campanero, 8, 0, seat: 1) with { Embers = 3 };

        var (next, _) = Resolve(TwoRooms([first, second, errata], [striker]), (C, Interact(Direction.East)));

        Assert.Equal(OutcomeKind.ThresholdClaimed, next.Outcome!.Kind);
        Assert.Equal(2, next.Outcome.Winners.Count);
    }

    [Fact]
    public void A_renegade_standing_when_the_warden_falls_can_win()
    {
        var warden = Model.Enemy.Create(1, EnemyKind.Warden, new Position(4, 2), maxHp: 24) with { Hp = 1, Phase = 2 };
        var mampostero = Hero(A, HeroClass.Mampostero, 4, 3) with { Pact = PactKind.OpenThreeChests };
        var renegade = PresentRenegade(B, HeroClass.Campanero, 8, 8, seat: 1) with { Embers = 10, Pact = PactKind.ReviveTwoAllies };

        var (next, _) = Resolve(State([mampostero, renegade], [warden]), (A, Attack(Direction.North)));

        Assert.Equal(OutcomeKind.Victory, next.Outcome!.Kind);
        Assert.Equal(B, Assert.Single(next.Outcome.Winners));
    }

    // ───────────── Órdenes y vistas ─────────────

    [Fact]
    public void Embers_and_present_renegades_give_orders_but_extinct_and_waiting_ones_do_not()
    {
        var waiting = Hero(C, HeroClass.Campanero, 0, 0, seat: 2) with { Status = HeroStatus.Renegade, AwaitingSpawn = true };
        var state = TwoRooms([Ember(A, 4, 4), PresentRenegade(B, HeroClass.Mampostero, 6, 6, seat: 1), waiting, ExtinctHero(new PlayerId("dana"), seat: 3)]);

        Assert.Null(OrderValidator.Validate(state, A, 1, Move(Direction.North)));
        Assert.Null(OrderValidator.Validate(state, B, 1, Attack(Direction.North)));
        Assert.Equal("PLAYER_OUT", OrderValidator.Validate(state, C, 1, Order.DefaultGuard)?.Code);
        Assert.Equal("PLAYER_OUT", OrderValidator.Validate(state, new PlayerId("dana"), 1, Order.DefaultGuard)?.Code);
    }

    [Fact]
    public void The_ember_is_visible_to_the_group_but_not_to_a_distant_renegade()
    {
        var state = TwoRooms([Ember(A, 8, 0, saved: 10, echoes: 3), Hero(B, HeroClass.Mampostero, 0, 8, seat: 1), PresentRenegade(C, HeroClass.Campanero, 0, 0, seat: 2)]);

        var forB = PlayerViewProjector.For(state, B);
        var forC = PlayerViewProjector.For(state, C);

        Assert.Equal(new Position(8, 0), forB.Allies.Single(a => a.Player == A).Position);
        Assert.Equal(0, forB.Allies.Single(a => a.Player == A).Embers);
        Assert.Null(forC.Allies.Single(a => a.Player == A).Position);
        var self = PlayerViewProjector.For(state, A).Self;
        Assert.Equal(10, self.Path.SavedEmbers);
        Assert.Equal(3, self.Path.Echoes);
        Assert.Equal(PathRules.EmberVisionRadius, self.VisionRadius);
    }

    [Fact]
    public void A_renegade_is_seen_as_an_enemy_only_inside_the_vision_and_sees_heroes_the_same_way()
    {
        var state = TwoRooms([PresentRenegade(A, HeroClass.Errata, 8, 8), Hero(B, HeroClass.Mampostero, 0, 0, seat: 1), Hero(C, HeroClass.Campanero, 7, 7, seat: 2)]);

        Assert.Null(PlayerViewProjector.For(state, B).Allies.Single(a => a.Player == A).Position);
        Assert.Equal(new Position(8, 8), PlayerViewProjector.For(state, C).Allies.Single(a => a.Player == A).Position);
        Assert.Equal(HeroStatus.Renegade, PlayerViewProjector.For(state, B).Allies.Single(a => a.Player == A).Status);
        var forA = PlayerViewProjector.For(state, A);
        Assert.Null(forA.Allies.Single(a => a.Player == B).Position);
        Assert.Equal(new Position(7, 7), forA.Allies.Single(a => a.Player == C).Position);
    }

    [Fact]
    public void An_ember_or_extinct_bearer_can_still_retire()
    {
        var state = TwoRooms([Ember(A, 4, 4, saved: 8), Hero(B, HeroClass.Mampostero, 0, 8, seat: 1)]);

        var result = MatchRules.Retire(state, A);

        Assert.True(result.IsSuccess);
        Assert.Equal(HeroStatus.Retired, result.State.H(A).Status);
        Assert.Equal(0, result.State.H(A).SavedEmbers);
    }
}
