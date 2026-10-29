using System.Collections.Immutable;
using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>Fases 4 y 5: los enemigos ejecutan lo anunciado, luego se mueven y anuncian (telegraph).</summary>
internal static class EnemyTurn
{
    public const int WardenReach = 2;

    public static void ExecuteTelegraphs(RoundContext ctx)
    {
        foreach (var id in ctx.Enemies.Select(e => e.Id).ToList())
        {
            var enemy = ctx.Enemy(id);
            if (enemy is null)
            {
                continue;
            }

            if (enemy.Frozen)
            {
                ctx.Update(enemy with { Frozen = false, Telegraph = [] });
                ctx.SkippingEnemies.Add(id);
                ctx.Emit(new ActionLost(EntityRef.ForEnemy(id)) { Audience = Audience.At(enemy.Position) });
                continue;
            }

            if (enemy.Telegraph.IsEmpty)
            {
                continue;
            }

            ctx.Update(enemy with { Telegraph = [] });
            foreach (var tile in enemy.Telegraph)
            {
                if (ctx.HeroAt(tile) is { IsActive: true } hero)
                {
                    Combat.HitHero(ctx, hero.Player, enemy.Stats.Damage, Attacker.FromEnemy(enemy));
                }
            }
        }
    }

    public static void MoveAndAnnounce(RoundContext ctx)
    {
        foreach (var id in ctx.Enemies.Select(e => e.Id).ToList())
        {
            var enemy = ctx.Enemy(id);
            if (enemy is null || ctx.SkippingEnemies.Contains(id))
            {
                continue;
            }

            var target = FindTarget(ctx, enemy);
            switch (enemy.Kind)
            {
                case EnemyKind.Totem:
                    TotemTurn(ctx, enemy);
                    break;
                case EnemyKind.Warden:
                    WardenTurn(ctx, enemy, target);
                    break;
                case EnemyKind.Crossbowman when target is not null:
                    CrossbowmanTurn(ctx, enemy, target);
                    break;
                case EnemyKind.Scavenger or EnemyKind.Moth or EnemyKind.Slime or EnemyKind.Hopper when target is not null:
                    MeleeTurn(ctx, enemy, target);
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>Objetivo: el héroe activo y visible (no en sigilo) más cercano; en empate, menor asiento.</summary>
    private static Hero? FindTarget(RoundContext ctx, Enemy enemy) =>
        ctx.Heroes
            .Where(h => h.IsActive && !h.IsStealthed)
            .OrderBy(h => h.Position.DistanceTo(enemy.Position))
            .ThenBy(h => h.Seat)
            .FirstOrDefault();

    private static void MeleeTurn(RoundContext ctx, Enemy enemy, Hero target)
    {
        for (var step = 0; step < enemy.Stats.Speed && enemy.Position.DistanceTo(target.Position) > 1; step++)
        {
            enemy = StepToward(ctx, enemy, target.Position);
        }

        if (enemy.Position.DistanceTo(target.Position) == 1)
        {
            enemy = enemy with { Facing = enemy.Position.DirectionTo(target.Position) ?? enemy.Facing };
            Announce(ctx, enemy, [target.Position]);
        }
    }

    /// <summary>Ballestero: si está alineado, anuncia la línea completa; si no, se mueve para alinearse.</summary>
    private static void CrossbowmanTurn(RoundContext ctx, Enemy enemy, Hero target)
    {
        if (!IsAligned(enemy.Position, target.Position))
        {
            var dx = target.Position.X - enemy.Position.X;
            var dy = target.Position.Y - enemy.Position.Y;
            var align = Math.Abs(dx) <= Math.Abs(dy)
                ? (dx > 0 ? Direction.East : Direction.West)
                : (dy > 0 ? Direction.South : Direction.North);
            var next = enemy.Position.Step(align);
            enemy = ctx.IsFreeFor(next, enemy.Flies) ? MoveTo(ctx, enemy, next, align) : StepToward(ctx, enemy, target.Position);
        }

        if (!IsAligned(enemy.Position, target.Position))
        {
            return;
        }

        var direction = enemy.Position.DirectionTo(target.Position) ?? enemy.Facing;
        var line = ImmutableList.CreateBuilder<Position>();
        var position = enemy.Position.Step(direction);
        while (ctx.Room.TerrainAt(position) is not (Terrain.Wall or Terrain.Brazier))
        {
            line.Add(position);
            position = position.Step(direction);
        }

        Announce(ctx, enemy with { Facing = direction }, line.ToImmutable());
    }

    private static void TotemTurn(RoundContext ctx, Enemy totem)
    {
        totem = totem with { SummonTimer = totem.SummonTimer + 1 };
        ctx.Update(totem);
        if (totem.SummonTimer % EnemyCatalog.TotemSummonEvery != 0)
        {
            return;
        }

        var free = ctx.FreeTilesNear(totem.Position);
        if (free.Count > 0)
        {
            ctx.Spawn(EnemyKind.Scavenger, free[0]);
        }

        totem = totem with { Summoned = totem.Summoned + 1 };
        if (totem.Summoned >= EnemyCatalog.TotemMaxSummons)
        {
            ctx.Enemies.RemoveAll(e => e.Id == totem.Id);
            ctx.Emit(new TotemExtinguished(totem.Id) { Audience = Audience.At(totem.Position) });
        }
        else
        {
            ctx.Update(totem);
        }
    }

    /// <summary>
    /// Celador. Fase 1: mira al objetivo y anuncia el 3×3 delante de él. Fase 2: alterna ese 3×3
    /// con la cruz completa de la sala (su fila y su columna).
    /// </summary>
    private static void WardenTurn(RoundContext ctx, Enemy warden, Hero? target)
    {
        if (target is not null)
        {
            if (warden.Position.DistanceTo(target.Position) > WardenReach)
            {
                warden = StepToward(ctx, warden, target.Position);
            }

            warden = warden with { Facing = warden.Position.DirectionTo(target.Position) ?? warden.Facing };
        }

        var cross = warden.Phase == 2 && warden.CrossNext;
        var tiles = cross ? Cross(ctx, warden.Position) : Square(ctx, warden.Position.Step(warden.Facing, 2));
        Announce(ctx, warden with { CrossNext = warden.Phase == 2 && !cross }, tiles);
    }

    private static ImmutableList<Position> Square(RoundContext ctx, Position center)
    {
        var tiles = ImmutableList.CreateBuilder<Position>();
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var tile = new Position(center.X + dx, center.Y + dy);
                if (ctx.Room.TerrainAt(tile) != Terrain.Wall)
                {
                    tiles.Add(tile);
                }
            }
        }

        return tiles.ToImmutable();
    }

    private static ImmutableList<Position> Cross(RoundContext ctx, Position origin) =>
        [.. Room.AllPositions().Where(p =>
            p != origin && (p.X == origin.X || p.Y == origin.Y) && ctx.Room.TerrainAt(p) != Terrain.Wall)];

    private static bool IsAligned(Position a, Position b) => a.X == b.X || a.Y == b.Y;

    private static void Announce(RoundContext ctx, Enemy enemy, ImmutableList<Position> tiles)
    {
        ctx.Update(enemy with { Telegraph = tiles });
        if (!tiles.IsEmpty)
        {
            ctx.Emit(new AttackTelegraphed(enemy.Id, tiles) { Audience = Audience.At(enemy.Position) });
        }
    }

    /// <summary>Un paso codicioso hacia el objetivo: primero el eje con más distancia; si está bloqueado, el otro.</summary>
    private static Enemy StepToward(RoundContext ctx, Enemy enemy, Position target)
    {
        foreach (var direction in CandidateDirections(enemy.Position, target))
        {
            var next = enemy.Position.Step(direction);
            if (ctx.IsFreeFor(next, enemy.Flies))
            {
                return MoveTo(ctx, enemy, next, direction);
            }
        }

        return enemy;
    }

    private static Enemy MoveTo(RoundContext ctx, Enemy enemy, Position next, Direction direction)
    {
        var moved = enemy with { Position = next, Facing = direction };
        ctx.Update(moved);
        ctx.Emit(new EnemyMoved(enemy.Id, enemy.Position, next) { Audience = Audience.At(next) });
        return moved;
    }

    private static IEnumerable<Direction> CandidateDirections(Position from, Position to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var horizontal = dx > 0 ? Direction.East : Direction.West;
        var vertical = dy > 0 ? Direction.South : Direction.North;
        if (Math.Abs(dx) >= Math.Abs(dy))
        {
            if (dx != 0)
            {
                yield return horizontal;
            }

            if (dy != 0)
            {
                yield return vertical;
            }
        }
        else
        {
            yield return vertical;
            if (dx != 0)
            {
                yield return horizontal;
            }
        }
    }
}
