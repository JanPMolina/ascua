using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>
/// Fase 3: movimientos simultáneos con rebote. Se mueven cuerpos, no héroes: Hilván tiene dos que rebotan cada uno
/// por su cuenta, y Polizón mueve a su huésped. La Deriva de la Brasa atraviesa todo y no entra en los rebotes.
/// </summary>
internal static class HeroMovement
{
    /// <summary>Lo que se mueve: un cuerpo de un héroe (<see cref="Body"/> 0 o 1) o un huésped.</summary>
    private readonly record struct Mover(PlayerId? Player, int Body, int? EnemyId)
    {
        public static Mover Of(PlayerId player, int body) => new(player, body, null);

        public static Mover Host(int enemyId) => new(null, 0, enemyId);
    }

    private sealed record Intent(Mover Mover, Position From, Position To, PlayerId Player);

    public static void Resolve(RoundContext ctx, Dictionary<PlayerId, Order> plan)
    {
        var intents = new Dictionary<Mover, Intent>();
        var bounced = new List<Intent>();
        foreach (var (player, order) in plan.OrderBy(p => ctx.Hero(p.Key).Seat))
        {
            if (order.Type != OrderType.Move || order.Direction is not { } direction)
            {
                continue;
            }

            var hero = ctx.Hero(player);
            if (hero.IsEmber)
            {
                Paths.Drift(ctx, hero, direction);
            }
            else if (hero.HostEnemyId is { } hostId)
            {
                AddHostIntent(ctx, hero, hostId, direction, intents);
            }
            else
            {
                hero = hero with { Facing = direction };
                ctx.Update(hero);
                AddBodyIntent(ctx, player, 0, hero.Position, direction, intents, bounced);
                if (hero.SecondBody is { } second)
                {
                    AddBodyIntent(ctx, player, 1, second, direction.Mirrored(), intents, bounced);
                }
            }
        }

        var moving = intents.Keys.ToHashSet();
        while (true)
        {
            var blocked = moving.Where(m => IsBlocked(ctx, intents, moving, intents[m])).ToList();
            if (blocked.Count == 0)
            {
                break;
            }

            foreach (var mover in blocked)
            {
                moving.Remove(mover);
                bounced.Add(intents[mover]);
            }
        }

        foreach (var intent in bounced.Where(i => i.Mover.Player is not null))
        {
            ctx.Emit(new MoveBounced(intent.Player, intent.To) { Body = intent.Mover.Body, Audience = Audience.At(intent.From) });
        }

        foreach (var mover in moving.OrderBy(m => ctx.Hero(intents[m].Player).Seat).ThenBy(m => m.Body))
        {
            var intent = intents[mover];
            if (mover.EnemyId is { } enemyId)
            {
                MoveHost(ctx, intent, enemyId);
            }
            else
            {
                MoveBody(ctx, intent);
            }
        }
    }

    private static void AddBodyIntent(
        RoundContext ctx, PlayerId player, int body, Position from, Direction direction, Dictionary<Mover, Intent> intents, List<Intent> bounced)
    {
        var target = from.Step(direction);
        var intent = new Intent(Mover.Of(player, body), from, target, player);
        if (CanEnter(ctx, target))
        {
            intents[intent.Mover] = intent;
        }
        else
        {
            bounced.Add(intent);
        }
    }

    /// <summary>MOVE dentro de un huésped: lo lleva a su velocidad, hasta donde el terreno lo deje.</summary>
    private static void AddHostIntent(RoundContext ctx, Hero polizon, int hostId, Direction direction, Dictionary<Mover, Intent> intents)
    {
        var host = ctx.Enemy(hostId);
        if (host is null)
        {
            return;
        }

        ctx.Update(polizon with { Facing = direction });
        var target = host.Position;
        for (var step = 0; step < host.Stats.Speed; step++)
        {
            var next = target.Step(direction);
            if (!ctx.IsWalkable(next, host.Flies, avoidCracks: true))
            {
                break;
            }

            target = next;
        }

        ctx.Update(host with { Facing = direction });
        if (target != host.Position)
        {
            var mover = Mover.Host(hostId);
            intents[mover] = new Intent(mover, host.Position, target, polizon.Player);
        }
    }

    private static void MoveBody(RoundContext ctx, Intent intent)
    {
        var hero = ctx.Hero(intent.Player);
        if (!hero.HasBody)
        {
            // El otro cuerpo de Hilván cayó en una grieta: ya no queda a quién mover.
            return;
        }

        hero = intent.Mover.Body == 0 ? hero with { Position = intent.To } : hero with { SecondBody = intent.To };
        ctx.Update(hero);
        ctx.MakeNoise(hero);
        ctx.Emit(new HeroMoved(intent.Player, intent.From, intent.To) { Body = intent.Mover.Body, Audience = Audience.At(intent.To) });

        if (hero.Has(Blessing.FireStep) && ctx.Chance(15))
        {
            Combat.StartFire(ctx, intent.From, hero);
        }

        if (ctx.Room.TerrainAt(intent.To) == Terrain.Crack && hero.Class != HeroClass.Mampostero)
        {
            Combat.Extinguish(ctx, intent.Player, ExtinctionReason.Chasm);
            return;
        }

        Combat.PickUpGroundEmbers(ctx, intent.Player, intent.To);
    }

    private static void MoveHost(RoundContext ctx, Intent intent, int enemyId)
    {
        var host = ctx.Enemy(enemyId)!;
        host = host with { Position = intent.To };
        ctx.Update(host);
        ctx.MakeNoise(host);
        ctx.Update(ctx.Hero(intent.Player) with { Position = intent.To });
        ctx.Emit(new EnemyMoved(enemyId, intent.From, intent.To) { Audience = Audience.At(intent.To) });
    }

    /// <summary>Terreno transitable para un héroe (las grietas se pueden pisar... y matan).</summary>
    private static bool CanEnter(RoundContext ctx, Position target) =>
        Room.IsInside(target) && ctx.IsWalkable(target, flies: false, avoidCracks: false);

    private static bool IsBlocked(RoundContext ctx, Dictionary<Mover, Intent> intents, HashSet<Mover> moving, Intent intent)
    {
        foreach (var other in moving)
        {
            if (other == intent.Mover)
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

        if (ctx.EnemyAt(intent.To) is { } enemy && !moving.Contains(Mover.Host(enemy.Id)))
        {
            return true;
        }

        if (ctx.HeroAt(intent.To) is { } occupant)
        {
            var body = occupant.Position == intent.To ? 0 : 1;
            return !moving.Contains(Mover.Of(occupant.Player, body));
        }

        return false;
    }
}
