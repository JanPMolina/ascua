using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>Quién origina un golpe. <see cref="Origin"/> decide frente/espalda.</summary>
internal readonly record struct Attacker(PlayerId? Hero, int? EnemyId, Position? Origin)
{
    public static Attacker None => default;

    public static Attacker FromHero(Hero hero) => new(hero.Player, null, hero.Position);

    public static Attacker FromEnemy(Enemy enemy) => new(null, enemy.Id, enemy.Position);

    public EntityRef? Ref => Hero is { } player
        ? EntityRef.ForHero(player)
        : EnemyId is { } id ? EntityRef.ForEnemy(id) : null;
}

/// <summary>Daño, curación, muertes, ascuas y fuego.</summary>
internal static class Combat
{
    public const int FriendlyFireSteal = 1;

    /// <summary>
    /// Golpea a un héroe. Orden: Muralla → bloqueo frontal → fuego amigo (½, redondeo arriba) →
    /// GUARDAR (½, redondeo abajo) → Codicia (+1) → escudo → vida. Devuelve si el golpe conectó.
    /// </summary>
    public static bool HitHero(RoundContext ctx, PlayerId targetId, int baseDamage, Attacker attacker)
    {
        var target = ctx.Hero(targetId);
        if (!target.IsActive || baseDamage <= 0)
        {
            return false;
        }

        var targetRef = EntityRef.ForHero(targetId);
        var audience = RoundContext.AudienceOf(target);

        if (IsBehindRampart(ctx, target))
        {
            ctx.Emit(new HitPrevented(targetRef, attacker.Ref, PreventionReason.Rampart) { Audience = audience });
            return false;
        }

        if (target.BlockPercent > 0
            && attacker.Origin is { } origin
            && target.Position.DirectionTo(origin) == target.Facing
            && ctx.Chance(target.BlockPercent))
        {
            ctx.Emit(new HitPrevented(targetRef, attacker.Ref, PreventionReason.Block) { Audience = audience });
            return false;
        }

        var friendly = attacker.Hero is { } attackerId && attackerId != targetId;
        var damage = baseDamage;
        if (friendly)
        {
            damage = (damage + 1) / 2;
        }

        if (ctx.Guarding.Contains(targetId))
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
            return false;
        }

        var absorbed = Math.Min(target.Shield, damage);
        var hp = Math.Max(0, target.Hp - (damage - absorbed));
        target = target with { Shield = target.Shield - absorbed, Hp = hp };
        ctx.Emit(new EntityDamaged(targetRef, damage, attacker.Ref, hp) { Audience = audience });
        if (hp == 0)
        {
            target = Fall(ctx, target);
        }

        ctx.Update(target);

        if (friendly)
        {
            OnFriendlyFire(ctx, attacker, targetId);
        }

        Reflect(ctx, targetId, attacker);
        return true;
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
        if (hp == 0)
        {
            Kill(ctx, enemy, attacker.Hero);
            return true;
        }

        enemy = enemy with { Hp = hp };
        ctx.Update(enemy);
        if (enemy.Kind == EnemyKind.Warden && enemy.Phase == 1 && hp * 2 <= enemy.MaxHp)
        {
            ctx.Update(enemy with { Phase = 2, CrossNext = true });
            ctx.Emit(new WardenEnraged(enemy.Id));
            foreach (var tile in ctx.FreeTilesNear(enemy.Position).Take(2))
            {
                ctx.Spawn(EnemyKind.Totem, tile);
            }
        }

        return true;
    }

    public static void GainEmbers(RoundContext ctx, PlayerId player, int delta, EmberReason reason)
    {
        var hero = ctx.Hero(player);
        var total = Math.Max(0, hero.Embers + delta);
        ctx.Update(hero with { Embers = total });
        ctx.Emit(new EmbersChanged(player, total - hero.Embers, reason, total) { Audience = Audience.Only(player) });
    }

    public static void Heal(RoundContext ctx, PlayerId player, int amount)
    {
        var hero = ctx.Hero(player);
        if (!hero.IsActive)
        {
            return;
        }

        var hp = Math.Min(hero.MaxHp, hero.Hp + amount);
        ctx.Update(hero with { Hp = hp });
        ctx.Emit(new Healed(EntityRef.ForHero(player), hp - hero.Hp, hp) { Audience = RoundContext.AudienceOf(hero) });
    }

    public static void Extinguish(RoundContext ctx, PlayerId player, ExtinctionReason reason)
    {
        var hero = ctx.Hero(player);
        var lost = hero.Embers / 2;
        ctx.Update(hero with
        {
            Status = HeroStatus.Extinct,
            Hp = 0,
            Embers = hero.Embers - lost,
            DownRounds = 0,
            StealthRounds = 0,
            BurnRounds = 0,
            Frozen = false,
        });
        ctx.Emit(new HeroExtinguished(player, reason, lost));
    }

    /// <summary>Efectos "al atacar" de las bendiciones del atacante sobre un objetivo alcanzado.</summary>
    public static void ApplyOnHitEffects(RoundContext ctx, PlayerId attackerId, EntityRef target)
    {
        var attacker = ctx.Hero(attackerId);
        if (attacker.Has(Blessing.LivingEmber) && ctx.Chance(20))
        {
            var inferno = attacker.HasSynergy(Synergy.Inferno);
            Burn(ctx, target, rounds: inferno ? 3 : 2, damage: inferno ? 2 : 1);
        }

        if (attacker.Has(Blessing.Frost) && ctx.Chance(20))
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
        var fire = new Fire(position, inferno ? 3 : 2, inferno ? 2 : 1);
        ctx.Room = ctx.Room with { Fires = ctx.Room.Fires.RemoveAll(f => f.Position == position).Add(fire) };
        ctx.Emit(new FireStarted(position, fire.Rounds) { Audience = Audience.At(position) });
    }

    public static void ToppleBrazier(RoundContext ctx, Position position, Hero owner)
    {
        ctx.Room = ctx.Room.WithTerrain(position, Terrain.Floor);
        ctx.Emit(new BrazierToppled(position) { Audience = Audience.At(position) });
        StartFire(ctx, position, owner);
        foreach (var direction in Directions.All)
        {
            StartFire(ctx, position.Step(direction), owner);
        }
    }

    private static Hero Fall(RoundContext ctx, Hero target)
    {
        if (target.Has(Blessing.LastBreath) && !target.LastBreathUsed)
        {
            ctx.Emit(new LastBreathTriggered(target.Player) { Audience = RoundContext.AudienceOf(target) });
            return target with { Hp = 1, LastBreathUsed = true };
        }

        ctx.Emit(new HeroDowned(target.Player));
        return target with
        {
            Status = HeroStatus.Down,
            DownRounds = 0,
            BurnRounds = 0,
            Frozen = false,
            StealthRounds = 0,
            Progress = target.Progress with { HasFallen = true },
        };
    }

    private static bool IsBehindRampart(RoundContext ctx, Hero target)
    {
        foreach (var (anvilId, direction) in ctx.Ramparts)
        {
            var anvil = ctx.Hero(anvilId);
            if (anvilId != target.Player && anvil.IsActive && anvil.Position.IsInLine(target.Position, direction.Opposite()))
            {
                return true;
            }
        }

        return false;
    }

    private static void OnFriendlyFire(RoundContext ctx, Attacker attacker, PlayerId victimId)
    {
        var attackerId = attacker.Hero!.Value;
        var victim = ctx.Hero(victimId);
        if (attacker.Origin is { } origin && victim.Position.DirectionTo(origin) == victim.Facing.Opposite())
        {
            var traitor = ctx.Hero(attackerId);
            ctx.Update(traitor with { Progress = traitor.Progress with { SneakyAllyHits = traitor.Progress.SneakyAllyHits + 1 } });
        }

        if (victim.Embers >= FriendlyFireSteal)
        {
            GainEmbers(ctx, victimId, -FriendlyFireSteal, EmberReason.StolenByAlly);
            GainEmbers(ctx, attackerId, FriendlyFireSteal, EmberReason.StolenFromAlly);
        }
    }

    private static void Reflect(RoundContext ctx, PlayerId targetId, Attacker attacker)
    {
        var target = ctx.Hero(targetId);
        if (!target.Has(Blessing.BrokenMirror) || attacker.Ref is null)
        {
            return;
        }

        if (!ctx.Chance(target.HasSynergy(Synergy.LivingArmor) ? 50 : 25))
        {
            return;
        }

        if (attacker.EnemyId is { } enemyId)
        {
            HitEnemy(ctx, enemyId, 1, new Attacker(targetId, null, null));
        }
        else if (attacker.Hero is { } heroId)
        {
            HitHero(ctx, heroId, 1, Attacker.None);
        }
    }

    private static void Kill(RoundContext ctx, Enemy enemy, PlayerId? killer)
    {
        ctx.Enemies.RemoveAll(e => e.Id == enemy.Id);
        ctx.Emit(new EnemyKilled(enemy.Id, enemy.Kind, killer) { Audience = Audience.At(enemy.Position) });
        var isWarden = enemy.Kind == EnemyKind.Warden;
        if (isWarden)
        {
            ctx.WardenSlain = true;
        }

        if (killer is { } player && ctx.Hero(player) is var hero)
        {
            var reward = (isWarden ? 5 : 1) + (hero.Has(Blessing.Greed) ? 1 : 0);
            GainEmbers(ctx, player, reward, isWarden ? EmberReason.WardenSlain : EmberReason.EnemySlain);
            if (isWarden)
            {
                hero = ctx.Hero(player);
                ctx.Update(hero with { Progress = hero.Progress with { KilledWarden = true } });
            }

            if (hero.Has(Blessing.HungryBlade))
            {
                Heal(ctx, player, 1);
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
