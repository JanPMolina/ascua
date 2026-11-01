using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>Fase 3: ataques, habilidades e interacciones de los héroes, por iniciativa.</summary>
internal static class HeroActions
{
    public const int DischargeArmLength = 3;
    public const int SproutHeal = 2;
    public const int SproutRange = 2;
    public const int PotionHeal = 2;

    public static void Resolve(RoundContext ctx, Dictionary<PlayerId, Order> plan)
    {
        var initiative = plan.Keys
            .Select(ctx.Hero)
            .OrderByDescending(h => h.Stats.Initiative)
            .ThenBy(h => h.Seat)
            .Select(h => h.Player)
            .ToList();

        foreach (var player in initiative)
        {
            var hero = ctx.Hero(player);
            var order = plan[player];
            if (!hero.CanAct || order.Direction is not { } direction)
            {
                continue;
            }

            if (hero.IsEmber)
            {
                EmberAction(ctx, hero, order.Type, direction);
                continue;
            }

            switch (order.Type)
            {
                case OrderType.Attack:
                    BasicAttack(ctx, hero, direction);
                    break;
                case OrderType.Skill when hero.Class == HeroClass.Chispa:
                    Discharge(ctx, hero, direction);
                    break;
                case OrderType.Skill when hero.Class == HeroClass.Raiz:
                    Sprout(ctx, hero, direction);
                    break;
                case OrderType.Interact when !hero.IsRenegade:
                    Interact(ctx, hero, direction);
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

    private static void BasicAttack(RoundContext ctx, Hero hero, Direction direction)
    {
        hero = hero with { Facing = direction };
        ctx.Update(hero);
        ctx.Attacked.Add(hero.Player);
        ctx.Emit(new HeroAttacked(hero.Player, direction) { Audience = RoundContext.AudienceOf(hero) });

        var position = hero.Position;
        for (var i = 0; i < hero.Stats.Range; i++)
        {
            position = position.Step(direction);
            var terrain = ctx.Room.TerrainAt(position);
            if (terrain == Terrain.Wall)
            {
                return;
            }

            if (terrain == Terrain.Brazier)
            {
                Combat.ToppleBrazier(ctx, position, hero);
                return;
            }

            if (StrikeAt(ctx, hero.Player, position, hero.Damage))
            {
                return;
            }
        }
    }

    /// <summary>Descarga de Chispa: cruz desde sí (brazos de 3) con 1 + cargas de daño.</summary>
    private static void Discharge(RoundContext ctx, Hero hero, Direction direction)
    {
        var damage = hero.IsRenegade ? PathRules.RenegadeMaxDamage : 1 + hero.Charges + (hero.Has(Blessing.StoneFist) ? 1 : 0);
        hero = hero with { Facing = direction, Charges = 0 };
        ctx.Update(hero);
        ctx.Attacked.Add(hero.Player);
        ctx.Emit(new SkillUsed(hero.Player, hero.Class, direction) { Audience = RoundContext.AudienceOf(hero) });

        foreach (var arm in Directions.All)
        {
            var position = hero.Position;
            for (var i = 0; i < DischargeArmLength; i++)
            {
                position = position.Step(arm);
                var terrain = ctx.Room.TerrainAt(position);
                if (terrain == Terrain.Wall)
                {
                    break;
                }

                if (terrain == Terrain.Brazier)
                {
                    Combat.ToppleBrazier(ctx, position, hero);
                    break;
                }

                StrikeAt(ctx, hero.Player, position, damage);
            }
        }
    }

    /// <summary>
    /// Brote de Raíz: cura 2 al aliado más cercano a ≤2 casillas en esa dirección; si no hay, a sí mismo.
    /// Para un Renegado, "aliado" es otro Renegado.
    /// </summary>
    private static void Sprout(RoundContext ctx, Hero hero, Direction direction)
    {
        hero = hero with { Facing = direction, SkillCooldown = hero.SkillCooldownOnUse };
        ctx.Update(hero);
        ctx.Emit(new SkillUsed(hero.Player, hero.Class, direction) { Audience = RoundContext.AudienceOf(hero) });

        var origin = hero.Position;
        var target = ctx.Heroes
            .Where(h => h.IsVulnerable && h.Player != hero.Player && h.IsSameFaction(hero))
            .Where(h => origin.DistanceTo(h.Position) <= SproutRange && origin.DirectionTo(h.Position) == direction)
            .OrderBy(h => origin.DistanceTo(h.Position))
            .ThenBy(h => h.Seat)
            .FirstOrDefault();
        Combat.Heal(ctx, target?.Player ?? hero.Player, SproutHeal);
    }

    private static void Interact(RoundContext ctx, Hero hero, Direction direction)
    {
        hero = hero with { Facing = direction };
        ctx.Update(hero);
        var target = hero.Position.Step(direction);

        if (ctx.HeroAt(target) is { Status: HeroStatus.Down } fallen)
        {
            Revive(ctx, hero, fallen);
        }
        else if (ctx.Room.ChestAt(target) is { Opened: false } chest)
        {
            OpenChest(ctx, hero, chest);
        }
        else if (ctx.Room.TerrainAt(target) == Terrain.Altar && hero.SilverKeys > 0 && hero.DraftOptions.IsEmpty)
        {
            ActivateAltar(ctx, hero, target);
        }
    }

    private static void Revive(RoundContext ctx, Hero hero, Hero fallen)
    {
        ctx.Update(fallen with { Status = HeroStatus.Active, Hp = Math.Min(HeroCatalog.ReviveHp, fallen.MaxHp), DownRounds = 0 });
        ctx.Update(hero with { Progress = hero.Progress with { Revives = hero.Progress.Revives + 1 } });
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
            Shield = hero.Shield + (chest.Loot == ChestLoot.Shield ? 1 : 0),
            SilverKeys = hero.SilverKeys + (chest.HasSilverKey ? 1 : 0),
            Progress = hero.Progress with { ChestsOpened = hero.Progress.ChestsOpened + 1 },
        };
        ctx.Update(hero);
        ctx.Emit(new ChestOpened(hero.Player, chest.Id) { Audience = RoundContext.AudienceOf(hero) });
        ctx.Emit(new ChestLooted(hero.Player, chest.Id, chest.Loot, chest.HasSilverKey) { Audience = Audience.Only(hero.Player) });
        Combat.GainEmbers(ctx, hero.Player, 1 + (hero.Has(Blessing.Lockpick) ? 1 : 0), EmberReason.ChestOpened);
        if (chest.Loot == ChestLoot.HealingPotion)
        {
            Combat.Heal(ctx, hero.Player, PotionHeal + (hero.Class == HeroClass.Raiz ? 1 : 0));
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
        ctx.Update(hero with { SilverKeys = hero.SilverKeys - 1, DraftOptions = [.. options] });
        ctx.Emit(new AltarActivated(hero.Player, altar) { Audience = RoundContext.AudienceOf(hero) });
        ctx.Emit(new DraftOffered(hero.Player, [.. options]) { Audience = Audience.Only(hero.Player) });
    }

    /// <summary>Golpea lo que haya en la casilla. Devuelve si había un objetivo (para cortar el proyectil).</summary>
    /// <remarks>El Renegado no daña a los monstruos ni a otros Renegados: lo detienen sin efecto.</remarks>
    private static bool StrikeAt(RoundContext ctx, PlayerId attackerId, Position position, int damage)
    {
        var attacker = ctx.Hero(attackerId);
        if (ctx.EnemyAt(position) is { } enemy)
        {
            if (attacker.IsRenegade)
            {
                return true;
            }

            var dealt = IsBackstab(attacker, enemy.Position, enemy.Facing) ? damage * 2 : damage;
            if (Combat.HitEnemy(ctx, enemy.Id, dealt, Attacker.FromHero(attacker)))
            {
                Combat.ApplyOnHitEffects(ctx, attackerId, EntityRef.ForEnemy(enemy.Id));
            }

            return true;
        }

        if (ctx.HeroAt(position) is { IsVulnerable: true } ally && ally.Player != attackerId)
        {
            if (attacker.IsRenegade && ally.IsRenegade)
            {
                return true;
            }

            var dealt = IsBackstab(attacker, ally.Position, ally.Facing) ? damage * 2 : damage;
            if (Combat.HitHero(ctx, ally.Player, dealt, Attacker.FromHero(attacker)))
            {
                Combat.ApplyOnHitEffects(ctx, attackerId, EntityRef.ForHero(ally.Player));
            }

            return true;
        }

        return false;
    }

    /// <summary>Pasiva de Cuervo: daño ×2 si golpea al objetivo por la espalda (el Renegado no tiene ese bonus).</summary>
    private static bool IsBackstab(Hero attacker, Position target, Direction targetFacing) =>
        attacker is { Class: HeroClass.Cuervo, IsRenegade: false } && target.DirectionTo(attacker.Position) == targetFacing.Opposite();
}
