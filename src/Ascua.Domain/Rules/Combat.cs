using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>
/// Quién origina un golpe. <see cref="Origin"/> decide frente y espalda (sin origen no hay inmunidad frontal).
/// <see cref="Hidden"/>: golpe anónimo; <see cref="Hero"/> se lleva el crédito (golpe final, ascuas), pero el registro
/// solo muestra a <see cref="EnemyId"/> o a nadie.
/// </summary>
internal readonly record struct Attacker(PlayerId? Hero, int? EnemyId, Position? Origin, bool Hidden = false)
{
    public static Attacker None => default;

    public static Attacker FromHero(Hero hero) => new(hero.Player, null, hero.Position);

    /// <summary>Golpe de un héroe desde otra casilla: su segundo cuerpo o su Eco.</summary>
    public static Attacker FromHero(Hero hero, Position origin) => new(hero.Player, null, origin);

    /// <summary>Golpe de un héroe sin origen (Tensar, Corrimiento): no importa hacia dónde mira el objetivo.</summary>
    public static Attacker Originless(Hero hero) => new(hero.Player, null, null);

    public static Attacker FromEnemy(Enemy enemy) => new(null, enemy.Id, enemy.Position);

    /// <summary>Golpe anónimo con crédito para <paramref name="credit"/>; el registro culpa a <paramref name="shownEnemy"/>.</summary>
    public static Attacker Anonymous(PlayerId credit, int? shownEnemy, Position? origin) => new(credit, shownEnemy, origin, Hidden: true);

    public EntityRef? Ref => EnemyId is { } enemy
        ? EntityRef.ForEnemy(enemy)
        : !Hidden && Hero is { } player ? EntityRef.ForHero(player) : null;
}

/// <summary>Daño, curación, muertes, ascuas, deudas y fuego.</summary>
internal static class Combat
{
    public const int FriendlyFireSteal = 1;
    public const int AssistReward = 1;

    /// <summary>Oro que reparte el Celador al morir, según el daño hecho a él.</summary>
    public const int WardenBounty = 6;

    public const int WardenFinalBlowReward = 1;

    /// <summary>
    /// Golpea a un héroe. Orden: fuego amigo (½, redondeo arriba) → GUARDAR (½, redondeo abajo) → Codicia (+1) → escudo
    /// → vida (o ascuas, si es el Avaro). Devuelve el daño que conectó (0 si no conectó). <paramref name="tile"/>: la
    /// casilla del cuerpo golpeado, si no es la principal. <paramref name="piercing"/>: ni Defender ni el escudo lo
    /// reducen (el frío).
    /// </summary>
    public static int HitHero(RoundContext ctx, PlayerId targetId, int baseDamage, Attacker attacker, Position? tile = null, bool piercing = false)
    {
        var target = ctx.Hero(targetId);
        if (!target.IsVulnerable || baseDamage <= 0)
        {
            return 0;
        }

        var targetRef = EntityRef.ForHero(targetId);
        var audience = tile is { } at ? Audience.At(at) : RoundContext.AudienceOf(target);
        ctx.MakeNoise(target);

        var source = !attacker.Hidden && attacker.Hero is { } sourceId && sourceId != targetId ? ctx.Hero(sourceId) : null;
        var friendly = source is not null && source.IsSameFaction(target);
        var renegadeStrike = source is { IsRenegade: true } && !target.IsRenegade;
        var damage = baseDamage;
        if (friendly)
        {
            damage = (damage + 1) / 2;
        }

        if (renegadeStrike)
        {
            // El primer golpe del Renegado a cada héroe lo absorbe el escudo de aviso; los demás hacen como máximo 1.
            if (!source!.WarnedHeroes.Contains(targetId))
            {
                ctx.Update(source with { WarnedHeroes = source.WarnedHeroes.Add(targetId) });
                ctx.Emit(new HitPrevented(targetRef, attacker.Ref, PreventionReason.WarningShield) { Audience = audience });
                return 0;
            }

            damage = Math.Min(damage, PathRules.RenegadeMaxDamage);
        }

        if (ctx.Guarding.Contains(targetId) && !piercing)
        {
            damage /= 2;
        }

        if (damage > 0 && target.Has(Blessing.Greed))
        {
            damage++;
        }

        if (damage <= 0)
        {
            ctx.Emit(new HitPrevented(targetRef, attacker.Ref, PreventionReason.Guard) { Audience = audience });
            return 0;
        }

        var absorbed = Math.Min(target.Has(Blessing.BrokenMirror) || piercing ? 0 : target.Shield, damage);
        var kindled = target.EmberShieldBy.Take(Math.Min(absorbed, target.EmberShieldBy.Count)).ToList();
        var remaining = damage - absorbed;
        target = target with { Shield = target.Shield - absorbed, EmberShieldBy = target.EmberShieldBy.RemoveRange(0, kindled.Count) };

        var stolen = 0;
        if (target.IsMiser)
        {
            (target, stolen) = HitMiser(ctx, target, remaining, friendly || renegadeStrike, tile ?? target.Position, vanish: piercing);
        }
        else
        {
            target = target with { Hp = Math.Max(0, target.Hp - remaining) };
        }

        ctx.Update(target);
        ctx.Emit(new EntityDamaged(targetRef, damage, attacker.Ref, target.Hp) { Audience = audience });
        var defeated = target is { Hp: 0, IsRenegade: true };
        if (target.Hp == 0 && !defeated)
        {
            if (target.IsInHost)
            {
                // Sin casa y con vida 0 dentro no ocurre: sale primero y cae afuera.
                var host = ctx.Enemy(target.HostEnemyId ?? -1);
                var free = host is null ? null : ctx.FreeTilesNear(host.Position).Select(p => (Position?)p).FirstOrDefault();
                ExitHost(ctx, targetId, free ?? target.Position);
                target = ctx.Hero(targetId);
            }

            target = Fall(ctx, target);
            ctx.Update(target);
        }

        // Cada punto que absorbió el escudo de Avivar da un eco a la Brasa que lo dio.
        foreach (var ember in kindled)
        {
            ctx.GainEchoes(ember, 1, EchoReason.ShieldAbsorbed);
        }

        if (friendly)
        {
            OnFriendlyFire(ctx, attacker, targetId, stolen);
        }
        else if (renegadeStrike)
        {
            OnRenegadeStrike(ctx, source!.Player, targetId, stolen);
        }

        if (defeated)
        {
            DefeatRenegade(ctx, targetId, attacker);
            return damage;
        }

        Reflect(ctx, targetId, attacker);
        return damage;
    }

    /// <summary>
    /// Todo lo que tengo: cada punto tira 1 ascua a su casilla; con 0 ascuas y otro golpe, cae. Fuego amigo y Renegados
    /// le roban 1; el resto cae al suelo (salvo el del frío, que se pierde). Devuelve lo robado para dárselo al atacante.
    /// </summary>
    private static (Hero Target, int Stolen) HitMiser(RoundContext ctx, Hero target, int points, bool theft, Position tile, bool vanish = false)
    {
        if (points <= 0)
        {
            return (target, 0);
        }

        var lost = Math.Min(target.Embers, points);
        var fatal = points > target.Embers;
        var stolen = theft && lost > 0 ? FriendlyFireSteal : 0;
        target = target with { Embers = target.Embers - lost, Hp = fatal ? 0 : target.Hp };
        if (lost > 0)
        {
            ctx.Emit(new EmbersChanged(target.Player, -lost, EmberReason.MiserHit, target.Embers) { Audience = Audience.Only(target.Player) });
        }

        // El oro que se lleva el frío no cae al suelo: si cayera, el Avaro lo recogería y nunca se apagaría.
        if (!vanish)
        {
            DropEmbers(ctx, tile, lost - stolen);
        }

        return (target, stolen);
    }

    /// <summary>Golpea a un enemigo. Devuelve si el golpe conectó.</summary>
    public static bool HitEnemy(RoundContext ctx, int enemyId, int damage, Attacker attacker)
    {
        var enemy = ctx.Enemy(enemyId);
        if (enemy is null || damage <= 0)
        {
            return false;
        }

        var enemyRef = EntityRef.ForEnemy(enemyId);
        var audience = Audience.At(enemy.Position);
        ctx.MakeNoise(enemy);
        if (enemy.Kind == EnemyKind.Warden
            && enemy.Phase == 1
            && attacker.Origin is { } origin
            && enemy.Position.DirectionTo(origin) == enemy.Facing)
        {
            ctx.Emit(new HitPrevented(enemyRef, attacker.Ref, PreventionReason.FrontImmunity) { Audience = audience });
            return false;
        }

        var hp = Math.Max(0, enemy.Hp - damage);
        ctx.Emit(new EntityDamaged(enemyRef, damage, attacker.Ref, hp) { Audience = audience });
        if (enemy.Kind == EnemyKind.Warden && attacker.Hero is { } striker)
        {
            // Solo cuenta el daño real: el exceso del último golpe no reparte oro.
            var hero = ctx.Hero(striker);
            ctx.Update(hero with { Progress = hero.Progress with { WardenDamage = hero.Progress.WardenDamage + (enemy.Hp - hp) } });
        }

        if (hp == 0)
        {
            Kill(ctx, enemy, attacker);
            return true;
        }

        enemy = enemy with { Hp = hp };
        ctx.Update(enemy);
        if (enemy.Kind == EnemyKind.Warden && enemy.Phase == 1 && hp * 2 <= enemy.MaxHp)
        {
            ctx.Update(enemy with { Phase = 2, CrossNext = true });
            ctx.Emit(new WardenEnraged(enemy.Id));
            // Con algún Renegado en la partida, la fase 2 invoca un solo tótem.
            foreach (var tile in ctx.FreeTilesNear(enemy.Position).Take(ctx.Heroes.Any(h => h.IsRenegade) ? 1 : 2))
            {
                ctx.Spawn(EnemyKind.Totem, tile);
            }
        }

        return true;
    }

    /// <summary>
    /// Suma ascuas. Lo ganado paga primero la deuda con la Usurera; un extinto o una Brasa lo acumula en sus ascuas
    /// guardadas (el Eco de Anteayer sigue dando golpes finales).
    /// </summary>
    public static void GainEmbers(RoundContext ctx, PlayerId player, int delta, EmberReason reason)
    {
        var hero = ctx.Hero(player);
        if (hero.Status == HeroStatus.Retired)
        {
            return;
        }

        if (delta > 0 && hero.Debt > 0 && hero.DebtTo is { } creditor)
        {
            var paid = Math.Min(delta, hero.Debt);
            delta -= paid;
            hero = PayDebt(ctx, hero, creditor, paid, DebtReason.Paid);
        }

        if (delta == 0)
        {
            return;
        }

        if (delta > 0 && hero.Status is HeroStatus.Extinct or HeroStatus.EmberSpirit)
        {
            ctx.Update(hero with { SavedEmbers = hero.SavedEmbers + delta });
            return;
        }

        var total = Math.Max(0, hero.Embers + delta);
        ctx.Update(hero with { Embers = total, PeakEmbers = Math.Max(hero.PeakEmbers, total) });
        ctx.Emit(new EmbersChanged(player, total - hero.Embers, reason, total) { Audience = Audience.Only(player) });
    }

    public static void Heal(RoundContext ctx, PlayerId player, int amount)
    {
        var hero = ctx.Hero(player);
        if (!hero.IsVulnerable || !hero.CanHeal)
        {
            // Curarse no existe para el Avaro ni con Corazón doble: ni pociones ni Calidez ni Préstamo.
            return;
        }

        var hp = Math.Min(hero.MaxHp, hero.Hp + amount);
        ctx.Update(hero with { Hp = hp });
        ctx.Emit(new Healed(EntityRef.ForHero(player), hp - hero.Hp, hp) { Audience = RoundContext.AudienceOf(hero) });
    }

    /// <summary>Un caído vuelve a estar en pie. El Avaro vuelve con ascuas en lugar de vida.</summary>
    public static void StandUp(RoundContext ctx, Hero fallen, int hp, int miserEmbers)
    {
        var standing = fallen with { Status = HeroStatus.Active, Hp = Math.Min(hp, fallen.MaxHp), DownRounds = 0 };
        ctx.Update(standing);
        if (standing.IsMiser && standing.Embers < miserEmbers)
        {
            GainEmbers(ctx, standing.Player, miserEmbers - standing.Embers, EmberReason.MiserRevived);
        }
    }

    /// <summary>
    /// Extinción: ya no se pierde la mitad de las ascuas; pasan a ser ascuas guardadas (de ahí cobra la Usurera lo que
    /// alcance) y se abre la elección de camino (privada). Vale para un héroe caído, una grieta o un Renegado derrotado.
    /// </summary>
    public static void Extinguish(RoundContext ctx, PlayerId player, ExtinctionReason reason)
    {
        var hero = ctx.Hero(player);
        var kept = hero.IsMiser ? Math.Max(hero.Embers, (hero.PeakEmbers + 1) / 2) : hero.Embers;
        var saved = hero.SavedEmbers + kept;
        var extinctions = hero.Extinctions + 1;
        hero = hero with
        {
            Progress = hero.Progress with { GoldAtDeath = Math.Max(hero.Progress.GoldAtDeath, kept) },
            Status = HeroStatus.Extinct,
            Hp = 0,
            Shield = 0,
            Embers = 0,
            SavedEmbers = saved,
            Extinctions = extinctions,
            PathOfferPending = true,
            PathChoice = null,
            DownRounds = 0,
            BurnRounds = 0,
            Frozen = false,
            AwaitingSpawn = false,
            SpawnRound = 0,
            EmberShieldBy = [],
            WarnedHeroes = [],
            SecondBody = null,
            History = [],
            Shouting = false,
        };
        ctx.Update(hero);
        ctx.Emit(new HeroExtinguished(player, reason, 0));
        if (kept > 0 && !hero.IsMiser)
        {
            ctx.Emit(new EmbersChanged(player, -kept, EmberReason.Extinction, 0) { Audience = Audience.Only(player) });
        }

        if (hero.Debt > 0 && hero.DebtTo is { } creditor && saved > 0)
        {
            var paid = Math.Min(hero.Debt, saved);
            hero = PayDebt(ctx, hero with { SavedEmbers = saved - paid }, creditor, paid, DebtReason.Extinction);
            saved = hero.SavedEmbers;
        }

        ctx.Emit(new PathOffered(player, extinctions, saved) { Audience = Audience.Only(player) });
    }

    /// <summary>Crea o aumenta una deuda con la Usurera. El interés cuenta desde que empezó la deuda vigente.</summary>
    public static void AddDebt(RoundContext ctx, PlayerId debtor, PlayerId creditor, int amount, DebtReason reason)
    {
        var hero = ctx.Hero(debtor);
        var total = hero.Debt + amount;
        ctx.Update(hero with { Debt = total, DebtTo = creditor, DebtSinceRound = hero.Debt == 0 ? ctx.Round : hero.DebtSinceRound });
        EmitDebt(ctx, debtor, creditor, amount, total, reason);
    }

    /// <summary>Le baja la deuda al deudor y le da las ascuas a la Usurera. Devuelve al deudor actualizado.</summary>
    public static Hero PayDebt(RoundContext ctx, Hero debtor, PlayerId creditor, int paid, DebtReason reason)
    {
        var total = debtor.Debt - paid;
        debtor = debtor with { Debt = total, DebtTo = total > 0 ? creditor : null, DebtSinceRound = total > 0 ? debtor.DebtSinceRound : 0 };
        ctx.Update(debtor);
        EmitDebt(ctx, debtor.Player, creditor, -paid, total, reason);
        GainEmbers(ctx, creditor, paid, EmberReason.DebtCollected);
        return ctx.Hero(debtor.Player);
    }

    /// <summary>La deuda de cada uno solo la ven el deudor y ella.</summary>
    public static void EmitDebt(RoundContext ctx, PlayerId debtor, PlayerId creditor, int delta, int total, DebtReason reason)
    {
        var change = new DebtChanged(debtor, creditor, delta, total, reason);
        ctx.Emit(change with { Audience = Audience.Only(debtor) });
        ctx.Emit(change with { Audience = Audience.Only(creditor) });
    }

    public static void RecordBetrayal(RoundContext ctx, PlayerId traitorId, PlayerId victim, int damage, BetrayalKind kind)
    {
        ctx.Betrayals.Add(new Betrayal(traitorId, victim, damage) { Kind = kind, Room = ctx.RoomIndex });
        var traitor = ctx.Hero(traitorId);
        ctx.Update(traitor with { Progress = traitor.Progress with { Betrayals = traitor.Progress.Betrayals + 1 } });
        ctx.Emit(new BetrayalRecorded(traitorId, victim, damage) { Kind = kind, Audience = Audience.Only(traitorId) });
    }

    /// <summary>Tira ascuas al suelo: se quedan en la casilla hasta que alguien la pise o se cambie de sala.</summary>
    public static void DropEmbers(RoundContext ctx, Position tile, int amount)
    {
        if (amount <= 0)
        {
            return;
        }

        var existing = ctx.Room.Ground.Find(g => g.Position == tile)?.Amount ?? 0;
        ctx.Room = ctx.Room with { Ground = ctx.Room.Ground.RemoveAll(g => g.Position == tile).Add(new GroundEmbers(tile, existing + amount)) };
        ctx.Emit(new EmbersDropped(tile, amount) { Audience = Audience.At(tile) });
    }

    /// <summary>Quien pisa una casilla con ascuas se las queda (Renegados incluidos); paga antes sus deudas.</summary>
    public static void PickUpGroundEmbers(RoundContext ctx, PlayerId player, Position tile)
    {
        if (ctx.Room.Ground.Find(g => g.Position == tile) is not { } ground)
        {
            return;
        }

        ctx.Room = ctx.Room with { Ground = ctx.Room.Ground.Remove(ground) };
        ctx.Emit(new EmbersPickedUp(player, tile, ground.Amount) { Audience = Audience.At(tile) });
        GainEmbers(ctx, player, ground.Amount, EmberReason.PickedUp);
    }

    /// <summary>Polizón sale del huésped a esa casilla: reaparece y empieza el enfriamiento de Abordar.</summary>
    public static void ExitHost(RoundContext ctx, PlayerId player, Position tile)
    {
        var hero = ctx.Hero(player);
        hero = hero with { HostEnemyId = null, BoardedRound = 0, Position = tile, SkillCooldown = hero.SkillCooldownOnUse };
        ctx.Update(hero);
        ctx.MakeNoise(hero);
        ctx.Emit(new HostExited(player, tile) { Audience = Audience.At(tile) });
        PickUpGroundEmbers(ctx, player, tile);
    }

    /// <summary>Efectos "al atacar" de las bendiciones del atacante sobre un objetivo alcanzado.</summary>
    public static void ApplyOnHitEffects(RoundContext ctx, PlayerId attackerId, EntityRef target)
    {
        var attacker = ctx.Hero(attackerId);
        if (attacker.Has(Blessing.LivingEmber))
        {
            Burn(ctx, target, rounds: 1, damage: 1);
        }

        if (attacker.Has(Blessing.Frost))
        {
            Freeze(ctx, target);
        }
    }

    /// <summary>Prende fuego en una casilla. El fuego de quien tiene Infierno dura +1 y hace +1.</summary>
    public static void StartFire(RoundContext ctx, Position position, Hero? owner)
    {
        if (ctx.Room.TerrainAt(position) == Terrain.Wall)
        {
            return;
        }

        var inferno = owner?.HasSynergy(Synergy.Inferno) == true;
        var fire = new Fire(position, inferno ? 3 : 2, 1);
        ctx.Room = ctx.Room with { Fires = ctx.Room.Fires.RemoveAll(f => f.Position == position).Add(fire) };
        ctx.Emit(new FireStarted(position, fire.Rounds) { Audience = Audience.At(position) });
    }

    public static void ToppleBrazier(RoundContext ctx, Position position, Hero? owner)
    {
        ctx.Room = ctx.Room.WithTerrain(position, Terrain.Floor);
        ctx.Emit(new BrazierToppled(position) { Audience = Audience.At(position) });
        StartFire(ctx, position, owner);
        foreach (var direction in Directions.All)
        {
            StartFire(ctx, position.Step(direction), owner);
        }
    }

    /// <summary>Lo derriban sin daño (empujón a una grieta). Último aliento se aplica como en cualquier caída.</summary>
    public static void KnockDown(RoundContext ctx, PlayerId player)
    {
        var target = ctx.Hero(player);
        if (target.IsActive)
        {
            ctx.Update(Fall(ctx, target with { Hp = 0 }));
        }
    }

    private static Hero Fall(RoundContext ctx, Hero target)
    {
        if (target.Has(Blessing.LastBreath) && !target.LastBreathUsed)
        {
            ctx.Emit(new LastBreathTriggered(target.Player) { Audience = RoundContext.AudienceOf(target) });
            return target with { Hp = 1, Embers = target.IsMiser ? 1 : target.Embers, LastBreathUsed = true };
        }

        ctx.Emit(new HeroDowned(target.Player));
        return target with
        {
            Status = HeroStatus.Down,
            DownRounds = 0,
            BurnRounds = 0,
            Frozen = false,
            Progress = target.Progress with { HasFallen = true },
        };
    }

    private static void OnFriendlyFire(RoundContext ctx, Attacker attacker, PlayerId victimId, int miserStolen)
    {
        Steal(ctx, attacker.Hero!.Value, victimId, miserStolen);
    }

    /// <summary>Golpe de un Renegado que conecta: roba 1 ascua (como el fuego amigo) y +2 si dejó caído al héroe.</summary>
    private static void OnRenegadeStrike(RoundContext ctx, PlayerId renegadeId, PlayerId victimId, int miserStolen)
    {
        Steal(ctx, renegadeId, victimId, miserStolen);
        if (ctx.Hero(victimId).Status == HeroStatus.Down)
        {
            GainEmbers(ctx, renegadeId, PathRules.RenegadeDownReward, EmberReason.HeroDowned);
        }
    }

    /// <summary>Roba 1 ascua. Al Avaro ya se le quitó con el golpe; solo falta dársela al atacante.</summary>
    private static void Steal(RoundContext ctx, PlayerId attackerId, PlayerId victimId, int miserStolen)
    {
        var victim = ctx.Hero(victimId);
        if (victim.IsMiser)
        {
            if (miserStolen > 0)
            {
                GainEmbers(ctx, attackerId, miserStolen, EmberReason.StolenFromAlly);
            }

            return;
        }

        if (victim.Embers >= FriendlyFireSteal)
        {
            GainEmbers(ctx, victimId, -FriendlyFireSteal, EmberReason.StolenByAlly);
            GainEmbers(ctx, attackerId, FriendlyFireSteal, EmberReason.StolenFromAlly);
        }
    }

    /// <summary>Quien derrota a un Renegado gana +2 ascuas; el Renegado se extingue de nuevo y vuelve a elegir camino.</summary>
    public static void DefeatRenegade(RoundContext ctx, PlayerId renegadeId, Attacker attacker)
    {
        ctx.Emit(new RenegadeDefeated(renegadeId, attacker.Hero) { HiddenBy = attacker.Hidden });
        if (attacker.Hero is { } killer && killer != renegadeId)
        {
            GainEmbers(ctx, killer, PathRules.RenegadeDefeatReward, EmberReason.RenegadeDefeated);
        }

        Extinguish(ctx, renegadeId, ExtinctionReason.Defeated);
    }

    private static void Reflect(RoundContext ctx, PlayerId targetId, Attacker attacker)
    {
        var target = ctx.Hero(targetId);
        if (!target.Has(Blessing.BrokenMirror) || attacker.Ref is null)
        {
            return;
        }

        var damage = target.HasSynergy(Synergy.LivingArmor) ? 2 : 1;
        if (attacker.EnemyId is { } enemyId)
        {
            HitEnemy(ctx, enemyId, damage, new Attacker(targetId, null, null));
        }
        else if (attacker.Hero is { } heroId)
        {
            HitHero(ctx, heroId, damage, Attacker.None);
        }
    }

    private static void Kill(RoundContext ctx, Enemy enemy, Attacker attacker)
    {
        var killer = attacker.Hero;
        ctx.Enemies.RemoveAll(e => e.Id == enemy.Id);
        ctx.Emit(new EnemyKilled(enemy.Id, enemy.Kind, killer) { HiddenKiller = attacker.Hidden, Audience = Audience.At(enemy.Position) });
        var isWarden = enemy.Kind == EnemyKind.Warden;
        PayAssists(ctx, enemy, killer);
        if (killer is not null)
        {
            Paths.OnKill(ctx, enemy.Position);
        }

        if (isWarden)
        {
            ctx.WardenSlain = true;
        }

        // Si otro mata al huésped, Polizón sale en esa casilla y recibe 1 de daño.
        if (ctx.PassengerOf(enemy.Id) is { } passenger)
        {
            ExitHost(ctx, passenger.Player, enemy.Position);
            if (killer != passenger.Player)
            {
                HitHero(ctx, passenger.Player, HeroRules.HostDeathDamage, Attacker.None);
            }
        }

        // El enemigo sobornado suelta lo que le pagaron.
        DropEmbers(ctx, enemy.Position, enemy.Bribes);

        if (isWarden)
        {
            PayWardenBounty(ctx);
        }

        if (killer is { } player && ctx.Hero(player) is var hero)
        {
            var kill = isWarden ? WardenFinalBlowReward : ctx.Room.Type == RoomType.Arena ? RoomTypes.ArenaKillReward : 1;
            var reward = kill + (hero.Has(Blessing.Greed) ? 1 : 0);
            hero = ctx.Hero(player);
            ctx.Update(hero with { Progress = hero.Progress with { Kills = hero.Progress.Kills + 1, KilledWarden = hero.Progress.KilledWarden || isWarden } });
            GainEmbers(ctx, player, reward, isWarden ? EmberReason.WardenSlain : EmberReason.EnemySlain);
            hero = ctx.Hero(player);
            if (hero.Has(Blessing.HungryBlade))
            {
                if (hero.IsMiser)
                {
                    GainEmbers(ctx, player, 1, EmberReason.EnemySlain);
                }
                else
                {
                    Heal(ctx, player, 1);
                }
            }
        }

        if (enemy.Kind == EnemyKind.Slime && !enemy.IsSplit)
        {
            foreach (var tile in ctx.FreeTilesNear(enemy.Position).Take(2))
            {
                ctx.Spawn(EnemyKind.Slime, tile, hp: 1, isSplit: true);
            }
        }
    }

    /// <summary>Quien le dio un ataque esta ronda sin matarlo gana +1. Un ataque posterior también (ver <see cref="HeroActions"/>).</summary>
    private static void PayAssists(RoundContext ctx, Enemy enemy, PlayerId? killer)
    {
        var corpse = new FallenEnemy(enemy.Id, enemy.Position, killer);
        foreach (var attacker in ctx.Attackers.GetValueOrDefault(enemy.Id, []).Where(a => a != killer).OrderBy(a => ctx.Hero(a).Seat))
        {
            corpse.Assisted.Add(attacker);
            GainEmbers(ctx, attacker, AssistReward, EmberReason.Assist);
        }

        ctx.Corpses.Add(corpse);
    }

    /// <summary>Reparte 6 de oro por el daño hecho al Celador: mayores restos y, en empate, menor asiento. Un retirado no cobra.</summary>
    private static void PayWardenBounty(RoundContext ctx)
    {
        var shares = ctx.Heroes.Where(h => h.Progress.WardenDamage > 0 && h.Status != HeroStatus.Retired)
            .Select(h => (h.Player, h.Seat, Damage: h.Progress.WardenDamage))
            .ToList();
        foreach (var (player, amount) in Outcomes.SplitByLargestRemainder(shares, WardenBounty))
        {
            GainEmbers(ctx, player, amount, EmberReason.WardenBounty);
        }
    }

    private static void Burn(RoundContext ctx, EntityRef target, int rounds, int damage)
    {
        if (target.Hero is { } player)
        {
            var hero = ctx.Hero(player);
            if (!hero.IsActive)
            {
                return;
            }

            ctx.Update(hero with { BurnRounds = rounds, BurnDamage = damage });
        }
        else if (ctx.Enemy(target.EnemyId ?? -1) is { } enemy)
        {
            ctx.Update(enemy with { BurnRounds = rounds, BurnDamage = damage });
        }
        else
        {
            return;
        }

        ctx.Emit(new StatusApplied(target, StatusEffect.Burning, rounds) { Audience = ctx.AudienceOf(target) });
    }

    private static void Freeze(RoundContext ctx, EntityRef target)
    {
        if (target.Hero is { } player)
        {
            var hero = ctx.Hero(player);
            if (!hero.IsActive)
            {
                return;
            }

            ctx.Update(hero with { Frozen = true });
        }
        else if (ctx.Enemy(target.EnemyId ?? -1) is { Kind: not EnemyKind.Warden } enemy)
        {
            ctx.Update(enemy with { Frozen = true });
        }
        else
        {
            return;
        }

        ctx.Emit(new StatusApplied(target, StatusEffect.Frozen, 1) { Audience = ctx.AudienceOf(target) });
    }
}
