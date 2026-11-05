using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>
/// Fase 7: trampas, fuego, quemaduras, caídos, drenaje del huésped, contadores, interés de las deudas, Calidez de
/// las Brasas, Renegados que reaparecen y paso de sala (con el renacer de las Brasas).
/// </summary>
internal static class Cleanup
{
    public const int SpikeDamage = 1;
    public const int SpikePeriod = 2;

    public static void Run(RoundContext ctx, HashSet<PlayerId> downAtStart)
    {
        TriggerSpikes(ctx);
        BurnFires(ctx);
        Flood(ctx);
        TickBurns(ctx);
        TickDownedHeroes(ctx, downAtStart);
        HeroSkills.DrainHosts(ctx);
        TickCounters(ctx);
        TickDebts(ctx);
        Paths.Warmth(ctx);
        Paths.SpawnDueRenegades(ctx);
        SettleBell(ctx);
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
            // Conoce las piedras: los pinchos no le afectan al Mampostero.
            var hero = ctx.HeroAt(tile) is { IsActive: true, Class: not HeroClass.Mampostero } exposed ? exposed : null;
            var enemy = ctx.EnemyAt(tile);
            if (hero is not null || enemy is { Flies: false })
            {
                ctx.Emit(new TrapTriggered(tile) { Audience = Audience.At(tile) });
            }

            if (hero is not null)
            {
                Combat.HitHero(ctx, hero.Player, SpikeDamage, Attacker.None, tile);
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
            // Infierno: su fuego no le daña. Paso ígneo: el fuego le hace 1 más.
            if (ctx.HeroAt(fire.Position) is { IsActive: true } hero && !hero.HasSynergy(Synergy.Inferno))
            {
                Combat.HitHero(ctx, hero.Player, fire.Damage + (hero.Has(Blessing.FireStep) ? 1 : 0), Attacker.None, fire.Position);
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

    /// <summary>Sala inundada: el agua sube desde el norte y daña a quien no vuela (se define por fila: un Corrimiento no la mueve).</summary>
    private static void Flood(RoundContext ctx)
    {
        var rows = ctx.Room.FloodedRows(ctx.Round);
        if (rows == 0)
        {
            return;
        }

        if (rows != ctx.Room.FloodedRows(ctx.Round - 1))
        {
            ctx.Emit(new RoomFlooded(rows));
        }

        foreach (var hero in ctx.Heroes.Where(h => h is { IsActive: true, HasBody: true }).ToList())
        {
            foreach (var body in hero.Bodies.Where(b => b.Y < rows).ToList())
            {
                Combat.HitHero(ctx, hero.Player, RoomTypes.FloodDamage, Attacker.None, body);
            }
        }

        foreach (var id in ctx.Enemies.Where(e => !e.Flies && e.Position.Y < rows).Select(e => e.Id).ToList())
        {
            Combat.HitEnemy(ctx, id, RoomTypes.FloodDamage, Attacker.None);
        }
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
            ctx.Update(hero with
            {
                SkillCooldown = Math.Max(0, hero.SkillCooldown - 1),
                HinderCooldown = Math.Max(0, hero.HinderCooldown - 1),
                PushCooldown = Math.Max(0, hero.PushCooldown - 1),
                WarmthCooldown = Math.Max(0, hero.WarmthCooldown - 1),
            });
        }
    }

    /// <summary>Interés: cada 3 rondas con deuda, la deuda sube 1 (el interés solo no pasa del tope de 6).</summary>
    private static void TickDebts(RoundContext ctx)
    {
        foreach (var hero in ctx.Heroes.Where(h => h is { Debt: > 0, DebtTo: not null }).ToList())
        {
            var elapsed = ctx.Round - hero.DebtSinceRound;
            if (elapsed > 0 && elapsed % HeroRules.InterestEvery == 0 && hero.Debt < HeroRules.DebtCap)
            {
                Combat.AddDebt(ctx, hero.Player, hero.DebtTo!.Value, 1, DebtReason.Interest);
            }
        }
    }

    /// <summary>El Repique dura una ronda; si quien lo tocó ya no puede dar órdenes, tampoco suena.</summary>
    private static void SettleBell(RoundContext ctx)
    {
        if (ctx.BellRinger is { } ringer && (ctx.BellRound <= ctx.Round || !ctx.Hero(ringer).CanAct))
        {
            ctx.BellRound = 0;
            ctx.BellRinger = null;
            ctx.BellClosed = false;
        }
    }

    /// <summary>Con la sala sin enemigos, un héroe activo en la escalera abre el interludio antes de la sala siguiente.</summary>
    private static void TryAdvanceRoom(RoundContext ctx)
    {
        if (ctx.Enemies.Count > 0 || ctx.RoomIndex >= ctx.Rooms.Count - 1)
        {
            return;
        }

        // Con Hilván basta un cuerpo sobre la escalera.
        var onStairs = ctx.Heroes.Any(h => h is { IsActive: true, HasBody: true } && h.Bodies.Any(b => ctx.Room.TerrainAt(b) == Terrain.Stairs));
        if (!onStairs)
        {
            return;
        }

        Interludes.Open(ctx);
    }

    /// <summary>
    /// Al cerrarse el interludio, todos pasan a la sala siguiente: las Brasas renacen en la entrada, cada Renegado quita un
    /// monstruo de la sala (mínimo 1) y aparece lejos del grupo.
    /// </summary>
    internal static void EnterNextRoom(RoundContext ctx)
    {
        ctx.RoomIndex++;
        var room = ctx.Room;
        var enemies = room.InitialEnemies;
        var renegades = ctx.Heroes.Count(h => h.IsPresentRenegade || h is { IsRenegade: true, SpawnRound: 0 });
        if (!ctx.IsWardenRoom && renegades > 0)
        {
            enemies = enemies.RemoveRange(0, Math.Min(renegades, Math.Max(0, enemies.Count - 1)));
        }

        ctx.Enemies.AddRange(enemies);
        ctx.NextEnemyId = Math.Max(ctx.NextEnemyId, room.InitialEnemies.Select(e => e.Id + 1).DefaultIfEmpty(0).Max());
        var reborn = Paths.Rebirth(ctx);

        // La historia y los ecos de Anteayer no pasan de sala.
        foreach (var hero in ctx.Heroes.ToList())
        {
            ctx.Update(hero with { EchoQueue = [], History = [], SecondBody = null });
        }

        var seat = 0;
        var placed = ctx.Heroes.Where(h => h.Status is HeroStatus.Active or HeroStatus.Down).OrderBy(h => h.Seat).ToList();
        foreach (var hero in placed)
        {
            ctx.Update(hero with { Position = room.SpawnPoints[seat++ % room.SpawnPoints.Count], Facing = Direction.North, SecondBody = null, EchoQueue = [], History = [] });
        }

        foreach (var hero in placed)
        {
            HeroSkills.PlaceSecondBody(ctx, ctx.Hero(hero.Player));
        }

        // En la sala nueva nadie hizo ruido todavía.
        ctx.ClearNoise();
        ctx.Emit(new RoomEntered(ctx.RoomIndex) { Type = room.Type });
        foreach (var player in reborn)
        {
            var hero = ctx.Hero(player);
            ctx.Emit(new EmberReborn(hero.Player, hero.Position, hero.Hp));
        }

        Paths.PlaceRenegades(ctx);
    }
}
