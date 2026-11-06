using System.Collections.Immutable;
using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>Fases 5 y 6: los enemigos ejecutan lo anunciado, luego se mueven y anuncian (telegraph). El huésped de Polizón no actúa.</summary>
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
                ctx.Update(enemy with { Frozen = false, Telegraph = [], DivertedBy = null });
                ctx.SkippingEnemies.Add(id);
                ctx.Emit(new ActionLost(EntityRef.ForEnemy(id)) { Audience = Audience.At(enemy.Position) });
                continue;
            }

            if (enemy.Delayed)
            {
                // Estorbo de una Brasa: el anuncio se conserva y se ejecuta la ronda siguiente.
                ctx.Update(enemy with { Delayed = false });
                ctx.SkippingEnemies.Add(id);
                continue;
            }

            if (enemy.Telegraph.IsEmpty)
            {
                continue;
            }

            ctx.Update(enemy with { Telegraph = [], DivertedBy = null });
            ctx.MakeNoise(enemy);
            foreach (var tile in enemy.Telegraph)
            {
                if (enemy.DivertedBy is { } errata)
                {
                    HeroSkills.DivertedStrike(ctx, enemy, tile, errata);
                }
                else if (ctx.HeroAt(tile) is { IsActive: true } hero)
                {
                    Combat.HitHero(ctx, hero.Player, enemy.Stats.Damage, Attacker.FromEnemy(enemy), tile);
                }
            }
        }
    }

    public static void MoveAndAnnounce(RoundContext ctx)
    {
        foreach (var id in ctx.Enemies.Select(e => e.Id).ToList())
        {
            var enemy = ctx.Enemy(id);
            if (enemy is null || ctx.SkippingEnemies.Contains(id) || ctx.PassengerOf(id) is not null)
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
                case EnemyKind.Crossbowman when target is { } shot:
                    CrossbowmanTurn(ctx, enemy, shot);
                    break;
                case EnemyKind.Scavenger or EnemyKind.Moth or EnemyKind.Slime or EnemyKind.Hopper when target is { } prey:
                    MeleeTurn(ctx, enemy, prey);
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Casilla objetivo: la Escucha si gritó esta ronda; si no, el cuerpo más cercano del héroe activo más cercano (en
    /// empate, menor asiento). Los Renegados, las Brasas y Polizón dentro de un huésped no son objetivo.
    /// </summary>
    private static Position? FindTarget(RoundContext ctx, Enemy enemy)
    {
        if (ctx.Heroes.Find(h => h is { Shouting: true, IsActive: true }) is { } shouter)
        {
            return shouter.Position;
        }

        // Lámpara: te eligen siempre (con varias, la más cercana). Dentro de un huésped no atrae a nadie.
        var lamp = ctx.Heroes
            .Where(h => h.IsActive && !h.IsInHost && h.Has(Blessing.Lamp))
            .OrderBy(h => h.DistanceTo(enemy.Position))
            .ThenBy(h => h.Seat)
            .FirstOrDefault();
        if (lamp is not null)
        {
            return lamp.NearestBody(enemy.Position);
        }

        return ctx.Heroes
            .Where(h => h.IsActive && !h.IsInHost)
            .OrderBy(h => h.DistanceTo(enemy.Position))
            .ThenBy(h => h.Seat)
            .FirstOrDefault()
            ?.NearestBody(enemy.Position);
    }

    private static void MeleeTurn(RoundContext ctx, Enemy enemy, Position target)
    {
        for (var step = 0; step < enemy.Stats.Speed && enemy.Position.DistanceTo(target) > 1; step++)
        {
            enemy = StepToward(ctx, enemy, target);
        }

        if (enemy.Position.DistanceTo(target) == 1)
        {
            enemy = enemy with { Facing = enemy.Position.DirectionTo(target) ?? enemy.Facing };
            Announce(ctx, enemy, [target]);
        }
    }

    /// <summary>Ballestero: si está alineado, anuncia la línea completa; si no, se mueve para alinearse.</summary>
    private static void CrossbowmanTurn(RoundContext ctx, Enemy enemy, Position target)
    {
        if (!IsAligned(enemy.Position, target))
        {
            var dx = target.X - enemy.Position.X;
            var dy = target.Y - enemy.Position.Y;
            var align = Math.Abs(dx) <= Math.Abs(dy)
                ? (dx > 0 ? Direction.East : Direction.West)
                : (dy > 0 ? Direction.South : Direction.North);
            var next = enemy.Position.Step(align);
            enemy = ctx.IsFreeFor(next, enemy.Flies) ? MoveTo(ctx, enemy, next, align) : StepToward(ctx, enemy, target);
        }

        if (!IsAligned(enemy.Position, target))
        {
            return;
        }

        var direction = enemy.Position.DirectionTo(target) ?? enemy.Facing;
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

        ctx.MakeNoise(totem);
        var free = ctx.FreeTilesNear(totem.Position);
        if (free.Count > 0)
        {
            ctx.Spawn(EnemyKind.Scavenger, free[0]);
        }

        totem = totem with { Summoned = totem.Summoned + 1 };
        if (totem.Summoned >= Difficulty.TotemMaxSummons(ctx.Heroes.Count))
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
    private static void WardenTurn(RoundContext ctx, Enemy warden, Position? target)
    {
        if (warden.WardenRests)
        {
            // Golpe pesado: entre uno y otro toma aire, sin moverse ni girarse. Es la ventana para rodearlo.
            ctx.Update(warden with { WardenRests = false });
            return;
        }

        warden = warden with { WardenRests = true };
        if (target is { } tile)
        {
            if (warden.Position.DistanceTo(tile) > WardenReach)
            {
                warden = StepToward(ctx, warden, tile);
            }

            warden = warden with { Facing = warden.Position.DirectionTo(tile) ?? warden.Facing };
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
        [.. ctx.Room.AllPositions().Where(p =>
            p != origin && (p.X == origin.X || p.Y == origin.Y) && ctx.Room.TerrainAt(p) != Terrain.Wall)];

    private static bool IsAligned(Position a, Position b) => a.X == b.X || a.Y == b.Y;

    private static void Announce(RoundContext ctx, Enemy enemy, ImmutableList<Position> tiles)
    {
        ctx.Update(enemy with { Telegraph = tiles, DivertedBy = null });
        if (!tiles.IsEmpty)
        {
            ctx.Emit(new AttackTelegraphed(enemy.Id, tiles) { Audience = Audience.At(enemy.Position) });
        }
    }

    /// <summary>
    /// Un paso por el camino más corto hacia el objetivo, rodeando lo que estorba (ver 05-heroes.md, aclaraciones). En
    /// campo abierto va primero por el eje con más distancia. Si no hay camino, se queda quieto.
    /// </summary>
    private static Enemy StepToward(RoundContext ctx, Enemy enemy, Position target)
    {
        var distances = DistancesFrom(ctx, target, enemy.Flies);
        var best = int.MaxValue;
        Direction? chosen = null;
        foreach (var direction in CandidateDirections(enemy.Position, target).Concat(Directions.All))
        {
            var next = enemy.Position.Step(direction);
            if (ctx.IsFreeFor(next, enemy.Flies) && distances.TryGetValue(next, out var distance) && distance < best)
            {
                best = distance;
                chosen = direction;
            }
        }

        return chosen is { } step ? MoveTo(ctx, enemy, enemy.Position.Step(step), step) : enemy;
    }

    /// <summary>Distancia a pie hasta el objetivo por casillas libres (búsqueda en anchura).</summary>
    private static Dictionary<Position, int> DistancesFrom(RoundContext ctx, Position target, bool flies)
    {
        var distances = new Dictionary<Position, int> { [target] = 0 };
        var queue = new Queue<Position>();
        queue.Enqueue(target);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var distance = distances[current] + 1;
            foreach (var direction in Directions.All)
            {
                var next = current.Step(direction);
                if (!distances.ContainsKey(next) && ctx.IsFreeFor(next, flies))
                {
                    distances[next] = distance;
                    queue.Enqueue(next);
                }
            }
        }

        return distances;
    }

    private static Enemy MoveTo(RoundContext ctx, Enemy enemy, Position next, Direction direction)
    {
        var moved = enemy with { Position = next, Facing = direction };
        ctx.Update(moved);
        ctx.MakeNoise(moved);
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
