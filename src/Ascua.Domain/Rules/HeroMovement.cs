using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>Fase 2: movimientos simultáneos con rebote (incluye el Paso sombrío de Cuervo).</summary>
internal static class HeroMovement
{
    public const int ShadowStepDistance = 2;
    public const int ShadowStepStealth = 2;

    private sealed record Intent(PlayerId Player, Position From, Position To);

    public static void Resolve(RoundContext ctx, Dictionary<PlayerId, Order> plan)
    {
        var intents = new Dictionary<PlayerId, Intent>();
        var bounced = new List<Intent>();
        foreach (var (player, order) in plan.OrderBy(p => ctx.Hero(p.Key).Seat))
        {
            if (order.Direction is not { } direction)
            {
                continue;
            }

            var hero = ctx.Hero(player);
            if (order.Type == OrderType.Move)
            {
                hero = hero with { Facing = direction };
                ctx.Update(hero);
                var target = hero.Position.Step(direction);
                if (CanEnter(ctx, target))
                {
                    intents[player] = new Intent(player, hero.Position, target);
                }
                else
                {
                    bounced.Add(new Intent(player, hero.Position, target));
                }
            }
            else if (order.Type == OrderType.Skill && hero.Class == HeroClass.Cuervo)
            {
                ctx.Emit(new SkillUsed(player, hero.Class, direction) { Audience = RoundContext.AudienceOf(hero) });
                hero = hero with
                {
                    Facing = direction,
                    SkillCooldown = hero.Stats.SkillCooldown,
                    StealthRounds = ShadowStepStealth,
                };
                ctx.Update(hero);
                ctx.FreshStealth.Add(player);
                ctx.Emit(new StatusApplied(EntityRef.ForHero(player), StatusEffect.Stealth, ShadowStepStealth)
                {
                    Audience = Audience.Only(player),
                });

                var first = hero.Position.Step(direction);
                var second = first.Step(direction);
                var target = CanEnter(ctx, second) ? second : first;
                if (CanEnter(ctx, target))
                {
                    intents[player] = new Intent(player, hero.Position, target);
                }
            }
        }

        var moving = intents.Keys.ToHashSet();
        while (true)
        {
            var blocked = moving.Where(p => IsBlocked(ctx, intents, moving, intents[p])).ToList();
            if (blocked.Count == 0)
            {
                break;
            }

            foreach (var player in blocked)
            {
                moving.Remove(player);
                bounced.Add(intents[player]);
            }
        }

        foreach (var intent in bounced)
        {
            ctx.Emit(new MoveBounced(intent.Player, intent.To) { Audience = RoundContext.AudienceOf(ctx.Hero(intent.Player)) });
        }

        foreach (var player in moving.OrderBy(p => ctx.Hero(p).Seat))
        {
            var intent = intents[player];
            var hero = ctx.Hero(player) with { Position = intent.To };
            ctx.Update(hero);
            ctx.Emit(new HeroMoved(player, intent.From, intent.To) { Audience = RoundContext.AudienceOf(hero) });

            if (hero.Has(Blessing.FireStep) && ctx.Chance(15))
            {
                Combat.StartFire(ctx, intent.From, hero);
            }

            if (ctx.Room.TerrainAt(intent.To) == Terrain.Crack)
            {
                Combat.Extinguish(ctx, player, ExtinctionReason.Chasm);
            }
        }
    }

    /// <summary>Terreno transitable para un héroe (las grietas se pueden pisar... y matan).</summary>
    private static bool CanEnter(RoundContext ctx, Position target) =>
        Room.IsInside(target) && ctx.IsWalkable(target, flies: false, avoidCracks: false);

    private static bool IsBlocked(RoundContext ctx, Dictionary<PlayerId, Intent> intents, HashSet<PlayerId> moving, Intent intent)
    {
        foreach (var other in moving)
        {
            if (other == intent.Player)
            {
                continue;
            }

            var otherIntent = intents[other];
            if (otherIntent.To == intent.To)
            {
                return true;
            }

            if (otherIntent.To == intent.From && otherIntent.From == intent.To)
            {
                return true;
            }
        }

        if (ctx.EnemyAt(intent.To) is not null)
        {
            return true;
        }

        var occupant = ctx.HeroAt(intent.To);
        return occupant is not null && occupant.Player != intent.Player && !moving.Contains(occupant.Player);
    }
}
