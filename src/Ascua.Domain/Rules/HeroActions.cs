using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>
/// Fase 4: ataques, habilidades e interacciones de los héroes, por iniciativa. El Eco de Anteayer actúa justo
/// después de su dueño, también si está caído o extinto.
/// </summary>
internal static class HeroActions
{
    public const int PotionHeal = 2;

    public static void Resolve(RoundContext ctx, Dictionary<PlayerId, Order> plan)
    {
        var initiative = ctx.Heroes
            .Where(h => plan.ContainsKey(h.Player) || h.EchoQueue.Exists(e => e.FireRound <= ctx.Round))
            .OrderByDescending(h => h.Stats.Initiative)
            .ThenBy(h => h.Seat)
            .Select(h => h.Player)
            .ToList();

        foreach (var player in initiative)
        {
            if (plan.TryGetValue(player, out var order))
            {
                Act(ctx, ctx.Hero(player), order);
            }

            HeroSkills.FireEchoes(ctx, player);
        }
    }

    private static void Act(RoundContext ctx, Hero hero, Order order)
    {
        if (!hero.CanAct || order.Direction is not { } direction)
        {
            return;
        }

        if (hero.IsEmber)
        {
            EmberAction(ctx, hero, order.Type, direction);
        }
        else if (hero.IsInHost)
        {
            HostAction(ctx, hero, order.Type, direction);
        }
        else
        {
            switch (order.Type)
            {
                case OrderType.Attack:
                    BasicAttack(ctx, hero, direction);
                    break;
                case OrderType.Skill:
                    HeroSkills.Use(ctx, hero, direction);
                    break;
                case OrderType.Interact when !hero.IsRenegade:
                    Interact(ctx, hero, direction);
                    break;
                case OrderType.Sabotage:
                    Treachery.Sabotage(ctx, hero, direction);
                    break;
                case OrderType.Push:
                    Treachery.Push(ctx, hero, direction);
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>Órdenes de la Brasa: HABILIDAD = Avivar, INTERACTUAR = Reavivar, ATACAR = Estorbo.</summary>
    private static void EmberAction(RoundContext ctx, Hero ember, OrderType type, Direction direction)
    {
        switch (type)
        {
            case OrderType.Skill:
                Paths.Kindle(ctx, ember, direction);
                break;
            case OrderType.Interact:
                Paths.Rekindle(ctx, ember, direction);
                break;
            case OrderType.Attack:
                Paths.Hinder(ctx, ember, direction);
                break;
            default:
                break;
        }
    }

    /// <summary>Polizón dentro de un huésped: ATACAR hace que golpee, HABILIDAD sale; GUARDAR e INTERACTUAR no hacen nada.</summary>
    private static void HostAction(RoundContext ctx, Hero polizon, OrderType type, Direction direction)
    {
        switch (type)
        {
            case OrderType.Attack:
                HeroSkills.HostStrike(ctx, polizon, direction);
                break;
            case OrderType.Skill:
                HeroSkills.LeaveHost(ctx, polizon, direction);
                break;
            default:
                break;
        }
    }

    private static void BasicAttack(RoundContext ctx, Hero hero, Direction direction)
    {
        hero = hero with { Facing = direction };
        if (hero.Class == HeroClass.Anteayer)
        {
            hero = hero with { EchoQueue = hero.EchoQueue.Add(new PendingEcho(ctx.Round + HeroRules.EchoDelay, hero.Position, direction)) };
        }

        ctx.Update(hero);
        ctx.MakeNoise(hero);
        ctx.Emit(new HeroAttacked(hero.Player, direction) { Audience = RoundContext.AudienceOf(hero) });

        // Puño de piedra: atacar plantado pega más. Un paso que rebotó también cuenta como paso.
        var damage = hero.Damage + (hero.Has(Blessing.StoneFist) && !hero.IsRenegade && !ctx.Stepped.Contains(hero.Player) ? 1 : 0);
        LineAttack(ctx, hero.Player, hero.Position, direction, hero.Stats.Range, damage);
        if (hero.SecondBody is { } second && ctx.Hero(hero.Player).HasBody)
        {
            LineAttack(ctx, hero.Player, second, direction.Mirrored(), hero.Stats.Range, damage);
        }
    }

    /// <summary>Ataque en línea desde <paramref name="origin"/>: golpea lo primero que encuentre; muros y braseros lo detienen.</summary>
    public static void LineAttack(RoundContext ctx, PlayerId player, Position origin, Direction direction, int range, int damage, bool echo = false)
    {
        var position = origin;
        for (var i = 0; i < range; i++)
        {
            position = position.Step(direction);
            var terrain = ctx.Room.TerrainAt(position);
            if (terrain == Terrain.Wall)
            {
                return;
            }

            if (terrain == Terrain.Brazier)
            {
                Combat.ToppleBrazier(ctx, position, ctx.Hero(player));
                return;
            }

            if (!echo)
            {
                Assist(ctx, player, position);
            }

            if (StrikeAt(ctx, player, origin, position, damage, echo))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Asistencia: el ataque llega a la casilla de un enemigo que murió esta misma ronda. Gana +1, una vez por enemigo, y
    /// la línea sigue. Los Renegados no asisten: no dañan monstruos.
    /// </summary>
    private static void Assist(RoundContext ctx, PlayerId player, Position tile)
    {
        if (ctx.Hero(player).IsRenegade)
        {
            return;
        }

        foreach (var corpse in ctx.Corpses.Where(c => c.Tile == tile && c.Killer != player))
        {
            if (corpse.Assisted.Add(player))
            {
                Combat.GainEmbers(ctx, player, Combat.AssistReward, EmberReason.Assist);
            }
        }
    }

    private static void Interact(RoundContext ctx, Hero hero, Direction direction)
    {
        ctx.Update(hero with { Facing = direction });
        InteractAt(ctx, hero.Player, hero.Position.Step(direction));
        if (hero.SecondBody is { } second)
        {
            InteractAt(ctx, hero.Player, second.Step(direction.Mirrored()));
        }
    }

    private static void InteractAt(RoundContext ctx, PlayerId player, Position target)
    {
        var hero = ctx.Hero(player);
        if (ctx.HeroAt(target) is { Status: HeroStatus.Down } fallen)
        {
            // Ganzúa no levanta y Último aliento gastado no se deja levantar.
            if (!hero.Has(Blessing.Lockpick) && fallen.CanBeRevived)
            {
                Revive(ctx, hero, fallen);
            }
        }
        else if (ctx.Room.ChestAt(target) is { Opened: false } chest)
        {
            OpenChest(ctx, hero, chest);
        }
        else if (ctx.Room.TerrainAt(target) == Terrain.Altar && hero.DraftOptions.IsEmpty
            && (hero.SilverKeys > 0 || ctx.Room.Type == RoomType.Sanctuary))
        {
            ActivateAltar(ctx, hero, target);
        }
    }

    private static void Revive(RoundContext ctx, Hero hero, Hero fallen)
    {
        Combat.StandUp(ctx, fallen, HeroCatalog.ReviveHp, HeroRules.MiserReviveEmbers);
        ctx.Update(hero with { Progress = hero.Progress with { Revives = hero.Progress.Revives + 1 } });
        ctx.MakeNoise(hero);
        ctx.Emit(new HeroRevived(hero.Player, fallen.Player));
        Combat.GainEmbers(ctx, hero.Player, 2, EmberReason.AllyRevived);
    }

    private static void OpenChest(RoundContext ctx, Hero hero, Chest chest)
    {
        ctx.Room = ctx.Room with
        {
            Chests = ctx.Room.Chests.Replace(chest, chest with { Opened = true, OpenedBy = hero.Player }),
        };
        hero = hero with
        {
            Shield = hero.Shield + (chest.Loot == ChestLoot.Shield && !hero.Has(Blessing.BrokenMirror) ? 1 : 0),
            SilverKeys = hero.SilverKeys + (chest.HasSilverKey ? 1 : 0),
            Progress = hero.Progress with { ChestsOpened = hero.Progress.ChestsOpened + 1 },
        };
        ctx.Update(hero);
        ctx.MakeNoise(hero);
        ctx.Emit(new ChestOpened(hero.Player, chest.Id) { Audience = RoundContext.AudienceOf(hero) });
        ctx.Emit(new ChestLooted(hero.Player, chest.Id, chest.Loot, chest.HasSilverKey) { Audience = Audience.Only(hero.Player) });
        Combat.GainEmbers(ctx, hero.Player, 1 + (hero.Has(Blessing.Lockpick) ? 2 : 0), EmberReason.ChestOpened);
        if (chest.Loot == ChestLoot.HealingPotion)
        {
            Combat.Heal(ctx, hero.Player, PotionHeal);
        }

        // El precio de Brasa viva: el cofre le quema al abrirlo.
        if (hero.Has(Blessing.LivingEmber))
        {
            Combat.HitHero(ctx, hero.Player, 1, Attacker.None);
        }
    }

    private static void ActivateAltar(RoundContext ctx, Hero hero, Position altar)
    {
        var available = BlessingRules.All.Where(b => !hero.Has(b)).ToList();
        var options = new List<Blessing>();
        while (options.Count < 2 && available.Count > 0)
        {
            var index = ctx.Rng.Next(available.Count);
            options.Add(available[index]);
            available.RemoveAt(index);
        }

        ctx.Room = ctx.Room.WithTerrain(altar, Terrain.Floor);
        var keyless = ctx.Room.Type == RoomType.Sanctuary;
        ctx.Update(hero with { SilverKeys = hero.SilverKeys - (keyless ? 0 : 1), DraftOptions = [.. options] });
        ctx.MakeNoise(hero);
        ctx.Emit(new AltarActivated(hero.Player, altar) { Audience = RoundContext.AudienceOf(hero) });
        ctx.Emit(new DraftOffered(hero.Player, [.. options]) { Audience = Audience.Only(hero.Player) });
    }

    /// <summary>Golpea lo que haya en la casilla. Devuelve si había un objetivo (para cortar el proyectil).</summary>
    /// <remarks>El Renegado no daña a los monstruos ni a otros Renegados: lo detienen sin efecto.</remarks>
    private static bool StrikeAt(RoundContext ctx, PlayerId attackerId, Position origin, Position position, int damage, bool echo = false)
    {
        var attacker = ctx.Hero(attackerId);
        if (ctx.EnemyAt(position) is { } enemy)
        {
            if (attacker.IsRenegade)
            {
                return true;
            }

            var chilling = attacker.Has(Blessing.Frost) && enemy.Kind != EnemyKind.Warden;
            var dealt = chilling ? 0 : damage + QuietBonus(attacker, !ctx.Start.NoisyEnemies.Contains(enemy.Id));
            if (chilling || Combat.HitEnemy(ctx, enemy.Id, dealt, Attacker.FromHero(attacker, origin)))
            {
                if (!echo)
                {
                    MarkAttacker(ctx, enemy.Id, attackerId);
                }

                Combat.ApplyOnHitEffects(ctx, attackerId, EntityRef.ForEnemy(enemy.Id));
            }

            return true;
        }

        if (ctx.HeroAt(position) is { IsVulnerable: true } target && target.Player != attackerId)
        {
            if (attacker.IsRenegade && target.IsRenegade)
            {
                return true;
            }

            // Escarcha no daña a quien puede congelar. A un Renegado no se le congela: le hace daño normal.
            var chilling = attacker.Has(Blessing.Frost) && target.IsActive;
            var dealt = damage + QuietBonus(attacker, !ctx.Start.NoisyHeroes.Contains(target.Player));
            if (chilling || Combat.HitHero(ctx, target.Player, dealt, Attacker.FromHero(attacker, origin), position) > 0)
            {
                Combat.ApplyOnHitEffects(ctx, attackerId, EntityRef.ForHero(target.Player));
            }

            return true;
        }

        return false;
    }

    /// <summary>Le dio un ataque esta ronda: si muere y no fue él quien lo mató, cobra asistencia.</summary>
    public static void MarkAttacker(RoundContext ctx, int enemyId, PlayerId attacker)
    {
        if (!ctx.Attackers.TryGetValue(enemyId, out var attackers))
        {
            ctx.Attackers[enemyId] = attackers = [];
        }

        attackers.Add(attacker);
    }

    /// <summary>A tientas: la Escucha hace +1 a lo que no hizo ruido en la última resolución (no como Renegada).</summary>
    private static int QuietBonus(Hero attacker, bool quiet) =>
        attacker is { Class: HeroClass.Escucha, Status: HeroStatus.Active } && quiet ? HeroRules.QuietTargetBonus : 0;
}
