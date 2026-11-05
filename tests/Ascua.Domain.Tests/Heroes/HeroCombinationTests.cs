using Ascua.Domain.Abstractions;
using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Domain.Tests.Support;
using Ascua.Domain.Views;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests.Heroes;

/// <summary>Combinaciones sensibles entre héroes y caminos (05-heroes.md §13).</summary>
public sealed class HeroCombinationTests
{
    [Fact]
    public void Mason_with_hilvan_a_shift_moves_only_the_body_on_the_line_and_misaligns_them()
    {
        var mason = Hero(A, HeroClass.Mampostero, 0, 4);
        var hilvan = Hero(B, HeroClass.Hilvan, 2, 4, seat: 1) with { SecondBody = At(2, 6) };

        var (next, _) = Resolve(State([mason, hilvan]), (A, Skill(Direction.East)), (B, Interact(Direction.North)));

        Assert.Equal(At(3, 4), next.H(B).Position);
        Assert.Equal(At(2, 6), next.H(B).SecondBody);
    }

    [Fact]
    public void Mason_with_hilvan_a_body_on_the_edge_takes_the_damage_on_the_shared_life()
    {
        var mason = Hero(A, HeroClass.Mampostero, 0, 4);
        var hilvan = Hero(B, HeroClass.Hilvan, 8, 4, seat: 1) with { SecondBody = At(8, 6) };

        var (next, _) = Resolve(State([mason, hilvan]), (A, Skill(Direction.East)), (B, Interact(Direction.North)));

        Assert.Equal(At(0, 4), next.H(B).Position);
        Assert.Equal(4, next.H(B).Hp);
    }

    [Fact]
    public void Mason_with_hilvan_a_shift_can_stretch_the_bodies_for_a_tug_on_the_same_round()
    {
        var mason = Hero(A, HeroClass.Mampostero, 0, 4);
        var hilvan = Hero(B, HeroClass.Hilvan, 2, 4, seat: 1) with { SecondBody = At(5, 5) };
        var totem = Enemy(1, EnemyKind.Totem, 1, 5);

        var (next, _) = Resolve(State([mason, hilvan], [totem]), (A, Skill(Direction.South)), (B, Skill(Direction.North)));

        // La columna 0 no toca a Hilván ni al tótem: Tensar no se cumple porque siguen desalineados.
        Assert.Equal(3, next.E(1)!.Hp);
        Assert.Equal(0, next.H(B).SkillCooldown);
    }

    [Fact]
    public void Errata_with_the_warden_a_diverted_blow_on_the_wardens_own_tile_ignores_its_front_and_drops_it_to_phase_two()
    {
        var warden = Model.Enemy.Create(1, EnemyKind.Warden, At(4, 2), maxHp: 24) with { Hp = 13, Facing = Direction.South, Telegraph = [At(4, 3)] };
        var errata = Hero(A, HeroClass.Errata, 4, 5);

        var (next, _) = Resolve(State([errata], [warden]), (A, Skill(Direction.North)));

        Assert.Equal(12, next.E(1)!.Hp);
        Assert.Equal(2, next.E(1)!.Phase);
    }

    [Fact]
    public void Errata_can_slay_the_warden_with_its_own_blow_and_earns_the_warden_reward()
    {
        var warden = Model.Enemy.Create(1, EnemyKind.Warden, At(4, 2), maxHp: 24) with { Hp = 1, Phase = 2, Telegraph = [At(4, 3)] };
        var errata = Hero(A, HeroClass.Errata, 4, 5) with { Pact = PactKind.SlayWarden };

        var (next, _) = Resolve(State([errata], [warden]), (A, Skill(Direction.North)));

        Assert.Equal(MatchPhase.Finished, next.Phase);
        Assert.True(next.H(A).Progress.KilledWarden);
        Assert.Equal(7, next.H(A).Embers);
    }

    [Fact]
    public void Errata_with_a_stowaway_a_diverted_blow_on_the_host_can_kill_it_and_he_takes_one()
    {
        var host = Enemy(1, EnemyKind.Moth, 4, 4);
        var polizon = Hero(B, HeroClass.Polizon, 4, 4, seat: 1) with { HostEnemyId = 1, BoardedRound = 1 };
        var striker = Enemy(2, EnemyKind.Scavenger, 0, 0) with { Telegraph = [At(4, 3)] };
        var errata = Hero(A, HeroClass.Errata, 4, 5);

        var (next, _) = Resolve(State([errata, polizon], [host, striker]), (A, Skill(Direction.South)), (B, Order.DefaultGuard));

        Assert.Null(next.E(1));
        Assert.False(next.H(B).IsInHost);
        Assert.Equal(2, next.H(B).Hp);
        Assert.Equal(1, next.H(A).Embers);
    }

    [Fact]
    public void Echo_with_a_fallen_ally_the_blow_does_not_hurt()
    {
        var anteayer = Hero(A, HeroClass.Anteayer, 4, 5) with { EchoQueue = [new PendingEcho(1, At(4, 5), Direction.North)] };
        var fallen = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1) with { Hp = 0, Status = HeroStatus.Down };
        var totem = Enemy(1, EnemyKind.Totem, 4, 3);

        var (next, _) = Resolve(State([anteayer, fallen], [totem]), (A, Order.DefaultGuard));

        Assert.Equal(3, next.E(1)!.Hp);
        Assert.Equal(0, next.H(B).Hp);
    }

    [Fact]
    public void Echo_with_a_downed_owner_can_hit_a_renegade_and_the_reward_is_hers()
    {
        var fallen = Hero(A, HeroClass.Anteayer, 8, 8, seat: 0) with
        {
            Hp = 0,
            Status = HeroStatus.Down,
            EchoQueue = [new PendingEcho(1, At(4, 5), Direction.North)],
        };
        var renegade = PresentRenegade(B, HeroClass.Mampostero, 4, 4, seat: 1) with { Hp = 1 };
        var ally = Hero(C, HeroClass.Mampostero, 0, 8, seat: 2);

        var (next, _) = Resolve(State([fallen, renegade, ally]), (B, Interact(Direction.East)), (C, Order.DefaultGuard));

        Assert.Equal(HeroStatus.Extinct, next.H(B).Status);
        Assert.Equal(PathRules.RenegadeDefeatReward, next.H(A).Embers);
    }

    [Fact]
    public void Stowaway_with_the_lender_a_debt_from_outside_keeps_growing_and_being_paid_from_inside()
    {
        var usurera = Hero(A, HeroClass.Usurera, 0, 0);
        var polizon = Hero(B, HeroClass.Polizon, 4, 4, seat: 1) with { HostEnemyId = 1, BoardedRound = 1, Debt = 2, DebtTo = A, DebtSinceRound = 1 };
        var host = Enemy(1, EnemyKind.Scavenger, 4, 4);

        var (interest, _) = Resolve(State([usurera, polizon], [host], round: 1 + HeroRules.InterestEvery), (A, Order.DefaultGuard), (B, Order.DefaultGuard));

        Assert.Equal(3, interest.H(B).Debt);

        var moth = Enemy(2, EnemyKind.Moth, 4, 3);
        var (paid, _) = Resolve(State([usurera, polizon], [host, moth], round: 2), (A, Order.DefaultGuard), (B, Attack(Direction.North)));
        Assert.Equal(1, paid.H(B).Debt);
        Assert.Equal(0, paid.H(B).Embers);
        Assert.Equal(1, paid.H(A).Embers);
    }

    [Fact]
    public void Stowaway_with_the_lender_she_can_lift_him_once_he_falls_outside()
    {
        var usurera = Hero(A, HeroClass.Usurera, 4, 6);
        var fallen = Hero(B, HeroClass.Polizon, 4, 5, seat: 1) with { Hp = 0, Status = HeroStatus.Down };

        var (next, _) = Resolve(State([usurera, fallen]), (A, Skill(Direction.North)));

        Assert.Equal(HeroStatus.Active, next.H(B).Status);
        Assert.Equal(3, next.H(B).Hp);
    }

    [Fact]
    public void Miser_with_the_paths_the_ember_keeps_half_of_his_peak_and_the_formula_applies()
    {
        var fallen = Hero(A, HeroClass.Avaro, 4, 4) with { Hp = 0, Status = HeroStatus.Down, DownRounds = 2, Embers = 0, PeakEmbers = 20 };
        var other = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1);

        var (extinct, _) = Resolve(TwoRooms([fallen, other]), (B, Order.DefaultGuard));
        Assert.Equal(10, extinct.H(A).SavedEmbers);

        var (ember, _) = Resolve(MatchRules.ChoosePath(extinct, A, PathKind.Friendship).State, (B, Order.DefaultGuard));
        Assert.True(ember.H(A).IsEmber);

        // Renace con ⌈10 × 25%⌉ = 3.
        var (reborn, _) = Resolve(ember, (A, Order.DefaultGuard), (B, Move(Direction.North)));
        Assert.True(reborn.H(A).IsActive);
        Assert.Equal(3, reborn.H(A).Embers);
    }

    [Fact]
    public void Miser_with_the_paths_a_renegade_can_win_and_his_embers_are_his_score()
    {
        var warden = Model.Enemy.Create(1, EnemyKind.Warden, At(4, 2), maxHp: 24) with { Hp = 1, Phase = 2 };
        var renegade = PresentRenegade(A, HeroClass.Avaro, 8, 8) with { Embers = 9, Pact = PactKind.OpenThreeChests };
        var hero = Hero(B, HeroClass.Mampostero, 4, 3, seat: 1) with { Pact = PactKind.OpenThreeChests };

        var (next, _) = Resolve(State([renegade, hero], [warden]), (A, Order.DefaultGuard), (B, Attack(Direction.North)));

        Assert.Equal(A, Assert.Single(next.Outcome!.Winners));
    }

    [Fact]
    public void Ember_with_a_hilvan_shield_a_kindle_finds_either_body()
    {
        var ember = Ember(A, 4, 4);
        var hilvan = Hero(B, HeroClass.Hilvan, 8, 8, seat: 1) with { SecondBody = At(4, 3) };

        var (next, _) = Resolve(State([ember, hilvan]), (A, Skill(Direction.North)), (B, Interact(Direction.East)));

        Assert.Equal(1, next.H(B).Shield);
    }

    [Fact]
    public void A_stowaway_cannot_be_kindled_or_warmed_from_outside()
    {
        var ember = Ember(A, 4, 5);
        var polizon = Hero(B, HeroClass.Polizon, 4, 4, seat: 1) with { Hp = 1, HostEnemyId = 1, BoardedRound = 1 };
        var host = Enemy(1, EnemyKind.Scavenger, 4, 4);

        var (next, _) = Resolve(State([ember, polizon], [host]), (A, Skill(Direction.North)), (B, Order.DefaultGuard));

        Assert.Equal(0, next.H(B).Shield);
        Assert.Equal(1, next.H(B).Hp);
    }

    /// <summary>Una partida completa con los nueve héroes repartidos: no debe colgarse ni romper invariantes.</summary>
    [Fact]
    public void A_whole_match_with_new_heroes_ends_with_an_outcome_and_a_consistent_log()
    {
        var classes = new[] { HeroClass.Hilvan, HeroClass.Polizon, HeroClass.Usurera, HeroClass.Avaro };
        var players = classes.Select((c, i) => new PlayerSetup(new PlayerId($"p{i}"), c)).ToList();
        var rng = new SeededRandom(12345);
        var (state, created) = MatchFactory.Create(players, rng);
        state = MatchRules.AutoChooseBlessings(state).State;
        var bots = new SeededRandom(99);
        var log = new List<DomainEvent>(created);
        var types = Enum.GetValues<OrderType>();

        while (state.Phase == MatchPhase.InProgress)
        {
            foreach (var hero in state.Heroes.Where(h => !h.DraftOptions.IsEmpty).ToList())
            {
                state = MatchRules.ChooseBlessing(state, hero.Player, 0).State;
            }

            foreach (var hero in state.Heroes.Where(h => h.IsChoosingPath).ToList())
            {
                state = MatchRules.ChoosePath(state, hero.Player, bots.Next(2) == 0 ? PathKind.Friendship : PathKind.Vengeance).State;
            }

            var orders = state.Heroes.Where(h => h.CanAct).ToDictionary(
                h => h.Player,
                _ =>
                {
                    var type = types[bots.Next(types.Length)];
                    return type == OrderType.Guard ? Order.DefaultGuard : new Order(type, Directions.All[bots.Next(4)]);
                });
            var (next, events) = Resolver.Resolve(state, orders, rng);
            log.AddRange(events);
            state = next;
        }

        Assert.NotNull(state.Outcome);
        Assert.Contains(log, e => e is MatchEnded);
        Assert.Equal(state.Heroes.Count, state.Outcome!.Scores.Count);
        foreach (var hero in state.Heroes)
        {
            Assert.NotNull(PlayerViewProjector.For(state, hero.Player).Outcome);
        }
    }
}
