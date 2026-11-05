using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Domain.Tests.Support;
using Ascua.Domain.Views;
using static Ascua.Domain.Tests.Support.TestWorld;

namespace Ascua.Domain.Tests.Heroes;

/// <summary>El Avaro (05-heroes.md §10): «La vida y la puntuación son cosas distintas».</summary>
public sealed class AvaroTests
{
    private static Hero Avaro(int x, int y, int seat = 0, int? embers = null)
    {
        var hero = Hero(A, HeroClass.Avaro, x, y, seat);
        return embers is { } amount ? hero with { Embers = amount, PeakEmbers = Math.Max(amount, hero.PeakEmbers) } : hero;
    }

    private static Enemy Striker(int x, int y, EnemyKind kind = EnemyKind.Scavenger) => Enemy(9, kind, 0, 0) with { Telegraph = [At(x, y)] };

    [Fact]
    public void He_starts_with_his_embers_and_no_life_bar()
    {
        var avaro = Model.Hero.Create(A, 0, HeroClass.Avaro, At(4, 4));

        Assert.Equal(HeroRules.MiserStartEmbers, avaro.Embers);
        Assert.Equal(1, avaro.MaxHp);
        Assert.Equal(1, avaro.Hp);
    }

    [Fact]
    public void Each_point_of_damage_throws_an_ember_onto_his_tile()
    {
        var (next, events) = Resolve(State([Avaro(4, 4)], [Striker(4, 4, EnemyKind.Crossbowman)]), (A, Interact(Direction.East)));

        Assert.Equal(1, next.H(A).Embers);
        Assert.Equal(new GroundEmbers(At(4, 4), 2), Assert.Single(next.CurrentRoom.Ground));
        Assert.Equal(HeroStatus.Active, next.H(A).Status);
        Assert.Contains(events, e => e is EmbersDropped { Amount: 2 });
    }

    [Fact]
    public void Guard_and_shield_come_first()
    {
        var shielded = Avaro(4, 4) with { Shield = 1 };
        var (afterShield, _) = Resolve(State([shielded], [Striker(4, 4)]), (A, Interact(Direction.East)));
        Assert.Equal(3, afterShield.H(A).Embers);
        Assert.Empty(afterShield.CurrentRoom.Ground);

        var (guarded, _) = Resolve(State([Avaro(4, 4)], [Striker(4, 4)]), (A, Order.DefaultGuard));
        Assert.Equal(3, guarded.H(A).Embers);
    }

    [Fact]
    public void With_zero_embers_and_another_blow_he_falls()
    {
        var other = Hero(B, HeroClass.Mampostero, 8, 8, seat: 1);

        var (standing, _) = Resolve(State([Avaro(4, 4, embers: 1), other], [Striker(4, 4)]), (A, Interact(Direction.East)), (B, Order.DefaultGuard));
        Assert.Equal(HeroStatus.Active, standing.H(A).Status);
        Assert.Equal(0, standing.H(A).Embers);

        var (fallen, _) = Resolve(State([Avaro(4, 4, embers: 0), other], [Striker(4, 4)]), (A, Interact(Direction.East)), (B, Order.DefaultGuard));
        Assert.Equal(HeroStatus.Down, fallen.H(A).Status);
        Assert.Equal(0, fallen.H(A).Hp);
    }

    [Fact]
    public void A_big_blow_can_take_his_last_embers_and_drop_him_at_once()
    {
        var other = Hero(B, HeroClass.Mampostero, 8, 8, seat: 1);

        var (next, _) = Resolve(State([Avaro(4, 4, embers: 1), other], [Striker(4, 4, EnemyKind.Crossbowman)]), (A, Interact(Direction.East)), (B, Order.DefaultGuard));

        Assert.Equal(HeroStatus.Down, next.H(A).Status);
        Assert.Equal(1, next.CurrentRoom.Ground.Single().Amount);
    }

    [Fact]
    public void Whoever_steps_on_it_first_keeps_it_including_renegades_and_he_can_take_his_own_back()
    {
        var ground = new GroundEmbers(At(4, 3), 2);
        var room = EmptyRoom() with { Ground = [ground] };

        var hero = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1);
        var (taken, events) = Resolve(State([Avaro(0, 0), hero], room: room), (B, Move(Direction.North)));
        Assert.Equal(2, taken.H(B).Embers);
        Assert.Empty(taken.CurrentRoom.Ground);
        Assert.Contains(events, e => e is EmbersPickedUp { Amount: 2 });

        var renegade = PresentRenegade(B, HeroClass.Mampostero, 4, 4, seat: 1);
        var (byRenegade, _) = Resolve(State([Avaro(0, 0), renegade], room: room), (B, Move(Direction.North)));
        Assert.Equal(2, byRenegade.H(B).Embers);

        var (own, _) = Resolve(State([Avaro(4, 4)], room: room), (A, Move(Direction.North)));
        Assert.Equal(5, own.H(A).Embers);
        Assert.Equal(5, own.H(A).PeakEmbers);
    }

    [Fact]
    public void Whoever_picks_them_up_pays_their_debts_first()
    {
        var room = EmptyRoom() with { Ground = [new GroundEmbers(At(4, 3), 2)] };
        var usurera = Hero(C, HeroClass.Usurera, 0, 0, seat: 2);
        var debtor = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1) with { Debt = 1, DebtTo = C, DebtSinceRound = 1 };

        var (next, _) = Resolve(State([Avaro(8, 8), debtor, usurera], room: room), (B, Move(Direction.North)));

        Assert.Equal(1, next.H(B).Embers);
        Assert.Equal(1, next.H(C).Embers);
    }

    [Fact]
    public void Embers_on_the_ground_are_lost_when_changing_room_and_embers_cannot_pick_them_up()
    {
        var ember = Ember(B, 4, 4, seat: 1);
        var room = EmptyRoom() with { Ground = [new GroundEmbers(At(4, 3), 2)] };
        var (drift, _) = Resolve(State([Avaro(8, 8), ember], room: room), (B, Move(Direction.North)));
        Assert.Single(drift.CurrentRoom.Ground);

        var hero = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1);
        var (next, _) = Resolve(TwoRooms([Avaro(8, 8), hero], first: room), (B, Move(Direction.North)));
        Assert.Equal(1, next.RoomIndex);
        Assert.Empty(next.CurrentRoom.Ground);
    }

    [Fact]
    public void Ground_embers_are_seen_only_within_view()
    {
        var room = EmptyRoom() with { Ground = [new GroundEmbers(At(1, 1), 2)] };
        var near = Hero(B, HeroClass.Mampostero, 1, 2, seat: 1);
        var far = Hero(C, HeroClass.Mampostero, 8, 8, seat: 2);
        var state = State([Avaro(0, 8), near, far], room: room);

        Assert.Equal(new GroundEmbersView(At(1, 1), 2), Assert.Single(PlayerViewProjector.For(state, B).Ground));
        Assert.Empty(PlayerViewProjector.For(state, C).Ground);
    }

    [Fact]
    public void Bribe_pays_one_ember_cancels_the_announcement_and_the_enemy_does_not_act()
    {
        var enemy = Enemy(1, EnemyKind.Scavenger, 4, 2) with { Telegraph = [At(4, 4)] };

        var (next, events) = Resolve(State([Avaro(4, 4)], [enemy]), (A, Skill(Direction.North)));

        Assert.Equal(2, next.H(A).Embers);
        Assert.Equal(1, next.E(1)!.Bribes);
        Assert.Equal(At(4, 2), next.E(1)!.Position);
        Assert.Empty(next.E(1)!.Telegraph);
        Assert.Empty(next.CurrentRoom.Ground);
        Assert.Equal(HeroCatalog.For(HeroClass.Avaro).SkillCooldown - 1, next.H(A).SkillCooldown);
        Assert.Contains(events, e => e is EnemyBribed { Cost: 1 });
    }

    [Fact]
    public void Bribe_reaches_the_first_enemy_within_three_tiles()
    {
        var far = Enemy(1, EnemyKind.Totem, 4, 0);

        var (next, _) = Resolve(State([Avaro(4, 4)], [far]), (A, Skill(Direction.North)));

        Assert.Equal(3, next.H(A).Embers);
        Assert.Equal(0, next.H(A).SkillCooldown);
    }

    [Fact]
    public void He_cannot_pay_with_his_last_ember()
    {
        var enemy = Enemy(1, EnemyKind.Scavenger, 4, 3) with { Telegraph = [At(4, 4)] };

        var (next, _) = Resolve(State([Avaro(4, 4, embers: 1)], [enemy]), (A, Skill(Direction.North)));

        Assert.Equal(0, next.E(1)!.Bribes);
        Assert.Equal(0, next.H(A).SkillCooldown);
        Assert.Equal(0, next.H(A).Embers);
    }

    [Fact]
    public void The_warden_costs_three_and_only_loses_its_announcement()
    {
        var warden = Model.Enemy.Create(1, EnemyKind.Warden, At(4, 2), maxHp: 24) with { Telegraph = [At(4, 4)] };

        var (poor, _) = Resolve(State([Avaro(4, 4)], [warden]), (A, Skill(Direction.North)));
        Assert.Equal(0, poor.E(1)!.Bribes);

        var (rich, _) = Resolve(State([Avaro(4, 4, embers: 5)], [warden]), (A, Skill(Direction.North)));
        Assert.Equal(2 + 0, rich.H(A).Embers);
        Assert.Equal(3, rich.E(1)!.Bribes);

        // Vuelve a anunciar en la misma ronda: solo perdió el anuncio.
        Assert.NotEmpty(rich.E(1)!.Telegraph);
    }

    [Fact]
    public void A_bribed_enemy_drops_the_payment_when_it_dies()
    {
        var enemy = Enemy(1, EnemyKind.Moth, 4, 3) with { Bribes = 2 };
        var hunter = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1);

        var (next, _) = Resolve(State([Avaro(0, 0), hunter], [enemy]), (B, Attack(Direction.North)));

        Assert.Equal(new GroundEmbers(At(4, 3), 2), Assert.Single(next.CurrentRoom.Ground));
    }

    [Fact]
    public void Healing_does_not_exist_for_him_neither_potions_nor_warmth()
    {
        var room = EmptyRoom() with { Chests = [new Chest(1, At(4, 3), ChestLoot.HealingPotion, HasSilverKey: false)] };

        var (potion, _) = Resolve(State([Avaro(4, 4, embers: 1)], room: room), (A, Interact(Direction.North)));
        Assert.Equal(2, potion.H(A).Embers);
        Assert.Equal(1, potion.H(A).Hp);

        var ember = Ember(B, 4, 5, seat: 1);
        var (warmed, events) = Resolve(State([Avaro(4, 4, embers: 1), ember]), (A, Interact(Direction.East)), (B, Order.DefaultGuard));
        Assert.Equal(1, warmed.H(A).Embers);
        Assert.DoesNotContain(events, e => e is Healed);
    }

    [Fact]
    public void Friendly_fire_steals_one_and_the_rest_falls_to_the_ground()
    {
        var attacker = Hero(B, HeroClass.Mampostero, 4, 5, seat: 1) with { Blessings = [Blessing.StoneFist] };

        // Daño 1 + 1 por Puño de piedra → 2, a la mitad hacia arriba = 1: el robado.
        var (next, _) = Resolve(State([Avaro(4, 4), attacker]) with { }, (A, Interact(Direction.East)), (B, Attack(Direction.North)));

        Assert.Equal(2, next.H(A).Embers);
        Assert.Equal(1, next.H(B).Embers);
        Assert.Empty(next.CurrentRoom.Ground);
    }

    [Fact]
    public void An_anonymous_blow_steals_nothing_and_everything_falls_to_the_ground()
    {
        var errata = Hero(B, HeroClass.Errata, 4, 6, seat: 1);
        var striker = Enemy(9, EnemyKind.Crossbowman, 0, 0) with { Telegraph = [At(4, 5)] };

        var (next, _) = Resolve(State([Avaro(4, 4), errata], [striker]), (A, Interact(Direction.East)), (B, Skill(Direction.North)));

        Assert.Equal(1, next.H(A).Embers);
        Assert.Equal(0, next.H(B).Embers);
        Assert.Equal(2, next.CurrentRoom.Ground.Single().Amount);
    }

    [Fact]
    public void Lifting_him_brings_him_back_with_two_embers_and_the_usual_bonus_for_the_lifter()
    {
        var fallen = Avaro(4, 4, embers: 0) with { Hp = 0, Status = HeroStatus.Down };
        var rescuer = Hero(B, HeroClass.Mampostero, 4, 5, seat: 1);

        var (next, _) = Resolve(State([fallen, rescuer]), (B, Interact(Direction.North)));

        Assert.Equal(HeroStatus.Active, next.H(A).Status);
        Assert.Equal(HeroRules.MiserReviveEmbers, next.H(A).Embers);
        Assert.Equal(1, next.H(A).Hp);
        Assert.Equal(2, next.H(B).Embers);
    }

    [Fact]
    public void An_ember_rekindles_him_with_one_ember()
    {
        var fallen = Avaro(4, 4, embers: 0) with { Hp = 0, Status = HeroStatus.Down };
        var ember = Ember(B, 4, 5, seat: 1);
        var other = Hero(C, HeroClass.Mampostero, 8, 8, seat: 2);

        var (next, _) = Resolve(State([fallen, ember, other]), (B, Interact(Direction.North)), (C, Order.DefaultGuard));

        Assert.Equal(HeroStatus.Active, next.H(A).Status);
        Assert.Equal(1, next.H(A).Embers);
    }

    [Fact]
    public void Double_heart_gives_one_ember_and_stone_fist_takes_one_without_touching_life()
    {
        var heart = BlessingRules.Grant(Avaro(4, 4), Blessing.DoubleHeart);
        Assert.Equal(4, heart.Embers);
        Assert.Equal(1, heart.MaxHp);
        Assert.Equal(4, heart.PeakEmbers);

        var fist = BlessingRules.Grant(Avaro(4, 4), Blessing.StoneFist);
        Assert.Equal(2, fist.Embers);
        Assert.Equal(1, fist.Hp);
        Assert.Equal(1, fist.Damage);
    }

    [Fact]
    public void Choosing_a_blessing_that_changes_his_embers_tells_him_privately()
    {
        var hero = Avaro(4, 4) with { DraftOptions = [Blessing.DoubleHeart, Blessing.Lamp] };
        var state = State([hero, Hero(B, HeroClass.Mampostero, 8, 8, seat: 1)]) with { Phase = MatchPhase.Drafting };

        var result = MatchRules.ChooseBlessing(state, A, 0);

        Assert.Equal(4, result.State.H(A).Embers);
        Assert.Contains(result.Events, e => e is EmbersChanged { Reason: EmberReason.Blessing, Delta: 1 } c && c.Audience == Audience.Only(A));
    }

    [Fact]
    public void Last_breath_leaves_him_with_one_ember()
    {
        var hero = Avaro(4, 4, embers: 0) with { Blessings = [Blessing.LastBreath] };

        var (next, _) = Resolve(State([hero], [Striker(4, 4)]), (A, Interact(Direction.East)));

        Assert.Equal(HeroStatus.Active, next.H(A).Status);
        Assert.Equal(1, next.H(A).Embers);
        Assert.True(next.H(A).LastBreathUsed);
    }

    [Theory]
    [InlineData(10, 5)]
    [InlineData(7, 4)]
    [InlineData(3, 2)]
    public void Going_extinct_his_saved_embers_are_half_the_most_he_ever_had(int peak, int saved)
    {
        var fallen = Avaro(4, 4, embers: 0) with { Hp = 0, Status = HeroStatus.Down, DownRounds = 2, PeakEmbers = peak };
        var other = Hero(B, HeroClass.Mampostero, 8, 8, seat: 1);

        var (next, _) = Resolve(State([fallen, other]), (B, Order.DefaultGuard));

        Assert.Equal(HeroStatus.Extinct, next.H(A).Status);
        Assert.Equal(saved, next.H(A).SavedEmbers);
    }

    [Fact]
    public void With_the_paths_he_returns_with_at_least_two_and_rebirth_gives_no_extra_life()
    {
        var chooser = ExtinctHero(A, HeroClass.Avaro, saved: 2) with { PathChoice = PathKind.Vengeance };
        var other = Hero(B, HeroClass.Mampostero, 8, 8, seat: 1);
        var (renegade, _) = Resolve(TwoRooms([chooser, other]), (B, Order.DefaultGuard));
        Assert.True(renegade.H(A).IsRenegade);
        Assert.Equal(HeroRules.MiserMinReturnEmbers, renegade.H(A).Embers);

        var ember = Ember(A, 2, 2, saved: 2, heroClass: HeroClass.Avaro);
        var hero = Hero(B, HeroClass.Mampostero, 4, 4, seat: 1);
        var (reborn, _) = Resolve(TwoRooms([ember, hero]), (B, Move(Direction.North)));
        Assert.True(reborn.H(A).IsActive);
        Assert.Equal(HeroRules.MiserMinReturnEmbers, reborn.H(A).Embers);
        Assert.Equal(1, reborn.H(A).Hp);
        Assert.Equal(HeroRules.MiserMinReturnEmbers, Outcomes.FinalEmbers(ember));
    }

    [Fact]
    public void As_a_renegade_his_life_is_his_embers_and_he_can_be_defeated()
    {
        var renegade = PresentRenegade(A, HeroClass.Avaro, 4, 4) with { Embers = 0 };
        var hero = Hero(B, HeroClass.Mampostero, 4, 5, seat: 1);

        var (next, _) = Resolve(State([renegade, hero]), (A, Interact(Direction.East)), (B, Attack(Direction.North)));

        Assert.Equal(HeroStatus.Extinct, next.H(A).Status);
        Assert.Equal(PathRules.RenegadeDefeatReward, next.H(B).Embers);
    }
}
