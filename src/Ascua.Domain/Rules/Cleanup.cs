using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>Fase 6: trampas, fuego, quemaduras, caídos, contadores y paso de sala.</summary>
internal static class Cleanup
{
    public const int SpikeDamage = 1;
    public const int SpikePeriod = 2;

    public static void Run(RoundContext ctx, HashSet<PlayerId> downAtStart)
    {
        TriggerSpikes(ctx);
        BurnFires(ctx);
        TickBurns(ctx);
        TickDownedHeroes(ctx, downAtStart);
        TickCounters(ctx);
        TryAdvanceRoom(ctx);
    }

    private static void TriggerSpikes(RoundContext ctx)
    {
        if (ctx.Round % SpikePeriod != 0)
        {
            return;
        }

        foreach (var tile in ctx.Room.PositionsOf(Terrain.Spikes).ToList())
        {
            var hero = ctx.HeroAt(tile);
            var enemy = ctx.EnemyAt(tile);
            if (hero is { IsActive: true } || enemy is { Flies: false })
            {
                ctx.Emit(new TrapTriggered(tile) { Audience = Audience.At(tile) });
            }

            if (hero is { IsActive: true })
            {
                Combat.HitHero(ctx, hero.Player, SpikeDamage, Attacker.None);
            }

            if (enemy is { Flies: false })
            {
                Combat.HitEnemy(ctx, enemy.Id, SpikeDamage, Attacker.None);
            }
        }
    }

    private static void BurnFires(RoundContext ctx)
    {
        foreach (var fire in ctx.Room.Fires.ToList())
        {
            if (ctx.HeroAt(fire.Position) is { IsActive: true } hero)
            {
                Combat.HitHero(ctx, hero.Player, fire.Damage, Attacker.None);
            }

            if (ctx.EnemyAt(fire.Position) is { } enemy)
            {
                Combat.HitEnemy(ctx, enemy.Id, fire.Damage, Attacker.None);
            }
        }

        ctx.Room = ctx.Room with
        {
            Fires = [.. ctx.Room.Fires.Select(f => f with { Rounds = f.Rounds - 1 }).Where(f => f.Rounds > 0)],
        };
    }

    private static void TickBurns(RoundContext ctx)
    {
        foreach (var hero in ctx.Heroes.Where(h => h.IsActive && h.BurnRounds > 0).ToList())
        {
            Combat.HitHero(ctx, hero.Player, hero.BurnDamage, Attacker.None);
            var current = ctx.Hero(hero.Player);
            ctx.Update(current with { BurnRounds = Math.Max(0, current.BurnRounds - 1) });
        }

        foreach (var id in ctx.Enemies.Where(e => e.BurnRounds > 0).Select(e => e.Id).ToList())
        {
            var enemy = ctx.Enemy(id)!;
            ctx.Update(enemy with { BurnRounds = enemy.BurnRounds - 1 });
            Combat.HitEnemy(ctx, id, enemy.BurnDamage, Attacker.None);
        }
    }

    /// <summary>Quien pasó la ronda entera caído suma 1; al llegar a 3 se extingue.</summary>
    private static void TickDownedHeroes(RoundContext ctx, HashSet<PlayerId> downAtStart)
    {
        foreach (var player in downAtStart)
        {
            var hero = ctx.Hero(player);
            if (hero.Status != HeroStatus.Down)
            {
                continue;
            }

            hero = hero with { DownRounds = hero.DownRounds + 1 };
            ctx.Update(hero);
            if (hero.DownRounds >= HeroCatalog.RoundsUntilExtinct)
            {
                Combat.Extinguish(ctx, player, ExtinctionReason.NotRevived);
            }
        }
    }

    private static void TickCounters(RoundContext ctx)
    {
        foreach (var hero in ctx.Heroes.ToList())
        {
            var charges = hero.Charges;
            if (hero is { IsActive: true, Class: HeroClass.Chispa } && !ctx.Attacked.Contains(hero.Player))
            {
                charges = Math.Min(HeroCatalog.MaxCharges, charges + 1);
            }

            var stealth = ctx.FreshStealth.Contains(hero.Player) ? hero.StealthRounds : Math.Max(0, hero.StealthRounds - 1);
            ctx.Update(hero with
            {
                SkillCooldown = Math.Max(0, hero.SkillCooldown - 1),
                StealthRounds = stealth,
                Charges = charges,
            });
        }
    }

    /// <summary>Con la sala sin enemigos, un héroe activo en la escalera lleva a todos a la siguiente sala.</summary>
    private static void TryAdvanceRoom(RoundContext ctx)
    {
        if (ctx.Enemies.Count > 0 || ctx.RoomIndex >= ctx.Rooms.Count - 1)
        {
            return;
        }

        var onStairs = ctx.Heroes.Any(h => h.IsActive && ctx.Room.TerrainAt(h.Position) == Terrain.Stairs);
        if (!onStairs)
        {
            return;
        }

        ctx.RoomIndex++;
        var room = ctx.Room;
        ctx.Enemies.AddRange(room.InitialEnemies);
        ctx.NextEnemyId = Math.Max(ctx.NextEnemyId, room.InitialEnemies.Select(e => e.Id + 1).DefaultIfEmpty(0).Max());
        var seat = 0;
        foreach (var hero in ctx.Heroes.Where(h => h.IsOnBoard).OrderBy(h => h.Seat).ToList())
        {
            ctx.Update(hero with { Position = room.SpawnPoints[seat++ % room.SpawnPoints.Count], Facing = Direction.North });
        }

        ctx.Emit(new RoomEntered(ctx.RoomIndex));
    }
}
