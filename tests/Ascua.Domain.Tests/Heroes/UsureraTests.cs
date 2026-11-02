using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Domain.Tests.Support;
using Ascua.Domain.Views;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests.Heroes;

/// <summary>La Usurera (05-heroes.md §9): «Ayudar es gratis».</summary>
public sealed class UsureraTests
{
    private static Hero Usurera(int x, int y, int seat = 0) => Hero(A, HeroClass.Usurera, x, y, seat);

    private static Hero Debtor(int debt = 2, int x = 8, int y = 8) =>
        Hero(B, HeroClass.Mampostero, x, y, seat: 1) with { Debt = debt, DebtTo = A, DebtSinceRound = 1 };

    [Fact]
    public void Loan_heals_three_to_the_first_wounded_ally_in_line_and_creates_a_debt_of_two()
    {
        var ally = Hero(B, HeroClass.Mampostero, 4, 3, seat: 1) with { Hp = 1 };

        var (next, _) = Resolve(State([Usurera(4, 5), ally]), (A, Skill(Direction.North)));

        Assert.Equal(4, next.H(B).Hp);
        Assert.Equal(HeroRules.LoanDebt, next.H(B).Debt);
        Assert.Equal(A, next.H(B).DebtTo);
        Assert.Equal(HeroCatalog.For(HeroClass.Usurera).SkillCooldown - 1, next.H(A).SkillCooldown);
    }

    [Fact]
    public void Loan_lifts_a_fallen_ally_with_three_without_the_bonus_embers_or_the_pact()
    {
        var fallen = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1) with { Hp = 0, Status = HeroStatus.Down };
        var usurera = Usurera(4, 5) with { Pact = PactKind.ReviveTwoAllies };

        var (next, events) = Resolve(State([usurera, fallen]), (A, Skill(Direction.North)));

        Assert.Equal(HeroStatus.Active, next.H(B).Status);
        Assert.Equal(HeroRules.LoanReviveHp, next.H(B).Hp);
        Assert.Equal(0, next.H(A).Embers);
        Assert.Equal(0, next.H(A).Progress.Revives);
        Assert.Equal(HeroRules.LoanDebt, next.H(B).Debt);
        Assert.Contains(events, e => e is HeroRevived);
    }

    [Fact]
    public void An_ally_with_full_life_does_not_count_and_the_next_one_in_line_is_helped()
    {
        var healthy = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1);
        var wounded = Hero(C, HeroClass.Mampostero, 4, 3, seat: 2) with { Hp = 2 };

        var (next, _) = Resolve(State([Usurera(4, 5), healthy, wounded]), (A, Skill(Direction.North)));

        Assert.Equal(0, next.H(B).Debt);
        Assert.Equal(5, next.H(C).Hp);
        Assert.Equal(HeroRules.LoanDebt, next.H(C).Debt);
    }

    [Fact]
    public void Nobody_to_lend_to_keeps_the_cooldown_she_cannot_lend_to_herself()
    {
        var hurt = Usurera(4, 5) with { Hp = 1 };
        var (next, _) = Resolve(State([hurt]), (A, Skill(Direction.North)));

        Assert.Equal(1, next.H(A).Hp);
        Assert.Equal(0, next.H(A).Debt);
        Assert.Equal(0, next.H(A).SkillCooldown);
    }

    [Fact]
    public void An_ally_three_tiles_away_is_out_of_reach()
    {
        var far = Hero(B, HeroClass.Mampostero, 4, 2, seat: 1) with { Hp = 1 };

        var (next, _) = Resolve(State([Usurera(4, 5), far]), (A, Skill(Direction.North)));

        Assert.Equal(1, next.H(B).Hp);
    }

    [Fact]
    public void The_miser_does_not_accept_loans()
    {
        var avaro = Hero(B, HeroClass.Avaro, 4, 4, seat: 1) with { Hp = 0, Status = HeroStatus.Down, Embers = 0 };

        var (next, _) = Resolve(State([Usurera(4, 5), avaro]), (A, Skill(Direction.North)));

        Assert.Equal(HeroStatus.Down, next.H(B).Status);
        Assert.Equal(0, next.H(B).Debt);
        Assert.Equal(0, next.H(A).SkillCooldown);
    }

    [Fact]
    public void A_new_loan_adds_to_the_debt_without_restarting_the_interest()
    {
        var ally = Debtor(2, 4, 4) with { Hp = 1, DebtSinceRound = 1 };

        var (next, _) = Resolve(State([Usurera(4, 5), ally], round: 2), (A, Skill(Direction.North)));

        Assert.Equal(4, next.H(B).Debt);
        Assert.Equal(1, next.H(B).DebtSinceRound);
    }

    [Fact]
    public void Every_three_rounds_with_debt_it_grows_by_one_up_to_six()
    {
        var state = State([Usurera(0, 0), Debtor(2) with { DebtSinceRound = 1 }]);
        for (var i = 0; i < 2; i++)
        {
            state = Resolve(state, (A, Order.DefaultGuard), (B, Order.DefaultGuard)).State;
        }

        Assert.Equal(2, state.H(B).Debt);

        var (grown, events) = Resolve(state, (A, Order.DefaultGuard), (B, Order.DefaultGuard));
        Assert.Equal(2, grown.H(B).Debt);

        (grown, events) = Resolve(grown, (A, Order.DefaultGuard), (B, Order.DefaultGuard));
        Assert.Equal(3, grown.H(B).Debt);
        Assert.Contains(events, e => e is DebtChanged { Reason: DebtReason.Interest, Delta: 1 });

        var capped = grown with { Heroes = [grown.H(A), grown.H(B) with { Debt = HeroRules.DebtCap, DebtSinceRound = grown.Round - HeroRules.InterestEvery }] };
        var (top, _) = Resolve(capped, (A, Order.DefaultGuard), (B, Order.DefaultGuard));
        Assert.Equal(HeroRules.DebtCap, top.H(B).Debt);
    }

    [Fact]
    public void Each_ember_the_debtor_earns_goes_to_her_until_the_debt_is_paid()
    {
        var debtor = Debtor(1, 4, 5);
        var moth = Enemy(1, EnemyKind.Moth, 4, 4) with { };
        var chest = new Chest(1, At(3, 5), ChestLoot.Nothing, HasSilverKey: false);
        var room = EmptyRoom() with { Chests = [chest] };

        var (paid, _) = Resolve(State([Usurera(0, 0), debtor], [moth], room), (A, Order.DefaultGuard), (B, Attack(Direction.North)));
        Assert.Equal(0, paid.H(B).Debt);
        Assert.Null(paid.H(B).DebtTo);
        Assert.Equal(0, paid.H(B).Embers);
        Assert.Equal(1, paid.H(A).Embers);

        var (free, _) = Resolve(paid, (A, Order.DefaultGuard), (B, Interact(Direction.West)));
        Assert.Equal(1, free.H(B).Embers);
        Assert.Equal(1, free.H(A).Embers);
    }

    [Fact]
    public void If_the_debtor_goes_extinct_she_collects_what_she_can_from_the_saved_embers()
    {
        var fallen = Debtor(3, 4, 4) with { Hp = 0, Status = HeroStatus.Down, DownRounds = 2, Embers = 5 };
        var other = Hero(C, HeroClass.Mampostero, 0, 8, seat: 2);

        var (next, events) = Resolve(State([Usurera(0, 0), fallen, other]), (A, Order.DefaultGuard), (C, Order.DefaultGuard));

        Assert.Equal(HeroStatus.Extinct, next.H(B).Status);
        Assert.Equal(2, next.H(B).SavedEmbers);
        Assert.Equal(0, next.H(B).Debt);
        Assert.Equal(3, next.H(A).Embers);
        Assert.Contains(events, e => e is DebtChanged { Reason: DebtReason.Extinction });
    }

    [Fact]
    public void What_cannot_be_collected_on_extinction_survives_and_rebirth_pays_it()
    {
        var chooser = ExtinctHero(B, seat: 1, saved: 10) with { PathChoice = PathKind.Friendship, Debt = 3, DebtTo = A };
        var other = Hero(C, HeroClass.Mampostero, 4, 1, seat: 2);

        var (ember, _) = Resolve(TwoRooms([Usurera(0, 0), chooser, other]), (A, Order.DefaultGuard), (C, Order.DefaultGuard));
        Assert.True(ember.H(B).IsEmber);
        Assert.Equal(3, ember.H(B).Debt);

        // Renace con ⌈10 × 25%⌉ = 3 ascuas, que pagan la deuda.
        var (reborn, _) = Resolve(ember, (A, Order.DefaultGuard), (C, Move(Direction.North)));
        Assert.True(reborn.H(B).IsActive);
        Assert.Equal(0, reborn.H(B).Embers);
        Assert.Equal(0, reborn.H(B).Debt);
        Assert.Equal(3, reborn.H(A).Embers);
    }

    [Fact]
    public void The_debt_survives_the_renegade_and_its_returning_embers_pay()
    {
        var chooser = ExtinctHero(B, seat: 1, saved: 10) with { PathChoice = PathKind.Vengeance, Debt = 3, DebtTo = A };
        var other = Hero(C, HeroClass.Mampostero, 0, 8, seat: 2);

        var (next, _) = Resolve(TwoRooms([Usurera(0, 0), chooser, other]), (A, Order.DefaultGuard), (C, Order.DefaultGuard));

        Assert.True(next.H(B).IsRenegade);
        Assert.Equal(2, next.H(B).Embers);
        Assert.Equal(0, next.H(B).Debt);
        Assert.Equal(3, next.H(A).Embers);
    }

    [Fact]
    public void At_the_end_she_collects_what_is_left_before_the_winner_is_decided()
    {
        var warden = Model.Enemy.Create(1, EnemyKind.Warden, At(4, 2), maxHp: 24) with { Hp = 1, Phase = 2 };
        var usurera = Usurera(0, 0) with { Embers = 4, Pact = PactKind.ReviveTwoAllies };
        var debtor = Hero(B, HeroClass.Mampostero, 4, 3, seat: 1) with { Embers = 6, Debt = 5, DebtTo = A, DebtSinceRound = 1, Pact = PactKind.OpenThreeChests };

        var (next, events) = Resolve(State([usurera, debtor], [warden]), (B, Attack(Direction.North)));

        // El golpe final (+5) ya paga los 5 que debía; se queda con sus 6 y ella llega a 9.
        Assert.Equal(MatchPhase.Finished, next.Phase);
        Assert.Equal(9, next.H(A).Embers);
        Assert.Equal(6, next.H(B).Embers);
        Assert.Equal(A, Assert.Single(next.Outcome!.Winners));
        Assert.Contains(events, e => e is DebtChanged { Reason: DebtReason.Paid });
    }

    [Fact]
    public void The_debts_left_at_the_end_are_charged_against_the_remaining_embers()
    {
        var warden = Model.Enemy.Create(1, EnemyKind.Warden, At(4, 2), maxHp: 24) with { Hp = 1, Phase = 2 };
        var usurera = Usurera(4, 3) with { Embers = 0, Pact = PactKind.OpenThreeChests };
        var debtor = Hero(B, HeroClass.Mampostero, 8, 8, seat: 1) with { Embers = 3, Debt = 5, DebtTo = A, DebtSinceRound = 1, Pact = PactKind.OpenThreeChests };

        var (next, events) = Resolve(State([usurera, debtor], [warden]), (A, Attack(Direction.North)));

        Assert.Equal(0, next.H(B).Embers);
        Assert.Equal(5 + 3, next.H(A).Embers);
        Assert.Equal(2, next.H(B).Debt);
        Assert.Contains(events, e => e is DebtChanged { Reason: DebtReason.MatchEnd, Delta: -3 });
        Assert.Equal(8, next.Outcome!.Scores.Single(s => s.Player == A).Embers);
    }

    [Fact]
    public void A_stowaway_inside_a_host_cannot_be_found_but_his_embers_keep_paying()
    {
        var polizon = Hero(B, HeroClass.Polizon, 4, 4, seat: 1) with { Hp = 1, HostEnemyId = 1, BoardedRound = 1, Debt = 1, DebtTo = A };
        var host = Enemy(1, EnemyKind.Scavenger, 4, 4);
        var moth = Enemy(2, EnemyKind.Moth, 4, 3);
        var state = State([Usurera(4, 5), polizon], [host, moth]);

        var (lent, _) = Resolve(state, (A, Skill(Direction.North)), (B, Order.DefaultGuard));
        Assert.Equal(1, lent.H(B).Debt);
        Assert.Equal(0, lent.H(A).SkillCooldown);

        var (paid, _) = Resolve(state, (A, Order.DefaultGuard), (B, Attack(Direction.North)));
        Assert.Equal(0, paid.H(B).Debt);
        Assert.Equal(1, paid.H(A).Embers);
    }

    [Fact]
    public void As_a_renegade_she_only_lends_to_renegades_and_keeps_collecting()
    {
        var usurera = PresentRenegade(A, HeroClass.Usurera, 4, 5);
        var hero = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1) with { Hp = 1 };
        var (refused, _) = Resolve(State([usurera, hero]), (A, Skill(Direction.North)), (B, Interact(Direction.East)));
        Assert.Equal(1, refused.H(B).Hp);
        Assert.Equal(0, refused.H(A).SkillCooldown);

        var comrade = PresentRenegade(C, HeroClass.Mampostero, 4, 4, seat: 2) with { Hp = 1 };
        var (lent, _) = Resolve(State([usurera, comrade]), (A, Skill(Direction.North)), (C, Interact(Direction.East)));
        Assert.Equal(4, lent.H(C).Hp);
        Assert.Equal(HeroRules.LoanDebt, lent.H(C).Debt);
        Assert.Equal((HeroCatalog.For(HeroClass.Usurera).SkillCooldown * 2) - 1, lent.H(A).SkillCooldown);

        var debtor = Debtor(1, 4, 5);
        var (collected, _) = Resolve(
            State([usurera with { Position = At(0, 0) }, debtor], [Enemy(1, EnemyKind.Moth, 4, 4)]),
            (A, Order.DefaultGuard),
            (B, Attack(Direction.North)));
        Assert.Equal(1, collected.H(A).Embers);
    }

    [Fact]
    public void Each_debt_is_seen_only_by_the_debtor_and_by_her()
    {
        var debtor = Debtor(2);
        var witness = Hero(C, HeroClass.Mampostero, 0, 8, seat: 2);
        var state = State([Usurera(0, 0), debtor, witness]);

        Assert.Equal(2, state.Seen(A, B).DebtToMe);
        Assert.Null(state.Seen(C, B).DebtToMe);
        Assert.Null(state.Seen(B, A).DebtToMe);
        var mine = PlayerViewProjector.For(state, B).Self;
        Assert.Equal(2, mine.Debt);
        Assert.Equal(A, mine.DebtTo);
        Assert.Equal(0, PlayerViewProjector.For(state, C).Self.Debt);
        Assert.DoesNotContain("\"DebtToMe\":2", Json.Of(PlayerViewProjector.For(state, C)), StringComparison.Ordinal);

        var ally = Hero(B, HeroClass.Mampostero, 4, 3, seat: 1) with { Hp = 1 };
        var (next, events) = Resolve(State([Usurera(4, 5), ally, witness]), (A, Skill(Direction.North)));
        Assert.DoesNotContain(events.SeenBy(next, C), e => e is DebtChanged);
        Assert.Contains(events.SeenBy(next, B), e => e is DebtChanged { Reason: DebtReason.Loan });
        Assert.Contains(events.SeenBy(next, A), e => e is DebtChanged { Reason: DebtReason.Loan });
    }

    [Fact]
    public void If_she_retires_nobody_owes_her_anything()
    {
        var result = MatchRules.Retire(State([Usurera(0, 0), Debtor(3)]), A);

        Assert.Equal(0, result.State.H(B).Debt);
        Assert.Null(result.State.H(B).DebtTo);
    }
}
