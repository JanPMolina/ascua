using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>
/// Fase 2: Corrimiento del Mampostero. Desliza su fila o su columna 1 casilla con todo lo que tenga encima; lo que
/// sale por un borde entra por el otro y, si es una criatura, recibe daño. Los anuncios no se mueven. Nunca deja la
/// sala rota: si la escalera o un héroe activo quedarían incomunicados, no se hace y no gasta el enfriamiento.
/// </summary>
internal static class RoomShift
{
    public static void Resolve(RoundContext ctx, Dictionary<PlayerId, Order> plan)
    {
        foreach (var (player, order) in plan.OrderBy(p => ctx.Hero(p.Key).Seat))
        {
            var hero = ctx.Hero(player);
            if (order is { Type: OrderType.Skill, Direction: { } direction } && hero is { Class: HeroClass.Mampostero, IsEmber: false, HasBody: true })
            {
                Shift(ctx, hero, direction);
            }
        }
    }

    private static void Shift(RoundContext ctx, Hero mason, Direction direction)
    {
        var horizontal = direction is Direction.East or Direction.West;
        var line = horizontal ? mason.Position.Y : mason.Position.X;
        var step = direction is Direction.East or Direction.South ? 1 : -1;

        bool OnLine(Position p) => (horizontal ? p.Y : p.X) == line;
        Position Slide(Position p)
        {
            if (!OnLine(p))
            {
                return p;
            }

            var moved = ((horizontal ? p.X : p.Y) + step + Room.Size) % Room.Size;
            return horizontal ? p with { X = moved } : p with { Y = moved };
        }

        bool Wraps(Position p) => OnLine(p) && p.DistanceTo(Slide(p)) > 1;

        var room = ctx.Room with
        {
            Features = [.. ctx.Room.Features.Select(f => f with { Position = Slide(f.Position) })],
            Chests = [.. ctx.Room.Chests.Select(c => c with { Position = Slide(c.Position) })],
            Fires = [.. ctx.Room.Fires.Select(f => f with { Position = Slide(f.Position) })],
            Ground = [.. ctx.Room.Ground.Select(g => g with { Position = Slide(g.Position) })],
        };
        var activeBodies = ctx.Heroes.Where(h => h.IsActive && h.HasBody).SelectMany(h => h.Bodies).Select(Slide).ToList();
        if (!StaysConnected(room, activeBodies))
        {
            return;
        }

        mason = mason with { Facing = direction, SkillCooldown = mason.SkillCooldownOnUse };
        ctx.Update(mason);
        ctx.Emit(new SkillUsed(mason.Player, mason.Class, direction) { Audience = RoundContext.AudienceOf(mason) });
        ctx.Room = room;

        var wrappedHeroes = new List<(PlayerId Player, Position Tile)>();
        foreach (var hero in ctx.Heroes.Where(h => h.HasBody && h.Bodies.Any(OnLine)).ToList())
        {
            foreach (var body in hero.Bodies.Where(Wraps))
            {
                wrappedHeroes.Add((hero.Player, Slide(body)));
            }

            var moved = hero with { Position = Slide(hero.Position), SecondBody = hero.SecondBody is { } second ? Slide(second) : null };
            ctx.Update(moved);
            ctx.MakeNoise(moved);
        }

        var wrappedEnemies = new List<int>();
        foreach (var enemy in ctx.Enemies.Where(e => OnLine(e.Position)).ToList())
        {
            if (Wraps(enemy.Position))
            {
                wrappedEnemies.Add(enemy.Id);
            }

            var moved = enemy with { Position = Slide(enemy.Position) };
            ctx.Update(moved);
            ctx.MakeNoise(moved);
            if (ctx.PassengerOf(enemy.Id) is { } passenger)
            {
                ctx.Update(passenger with { Position = moved.Position });
            }
        }

        ctx.Emit(new RoomShifted(mason.Player, horizontal ? ShiftAxis.Row : ShiftAxis.Column, line, direction));

        var attacker = Attacker.Originless(mason);
        foreach (var id in wrappedEnemies.Where(_ => !mason.IsRenegade))
        {
            Combat.HitEnemy(ctx, id, HeroRules.WrapDamage, attacker);
        }

        foreach (var (player, tile) in wrappedHeroes)
        {
            // Los Renegados no se dañan entre sí; contra los héroes, el tope de 1 lo aplica el golpe.
            if (!(mason.IsRenegade && ctx.Hero(player).IsRenegade))
            {
                Combat.HitHero(ctx, player, HeroRules.WrapDamage, attacker, tile);
            }
        }
    }

    /// <summary>¿Quedan todos los héroes activos y la escalera en un mismo trozo de sala transitable?</summary>
    private static bool StaysConnected(Room room, List<Position> activeBodies)
    {
        var required = new List<Position>(activeBodies);
        required.AddRange(room.PositionsOf(Terrain.Stairs));
        if (required.Count == 0)
        {
            return true;
        }

        var reachable = new HashSet<Position> { required[0] };
        var queue = new Queue<Position>();
        queue.Enqueue(required[0]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var direction in Directions.All)
            {
                var next = current.Step(direction);
                if ((RoomValidator.IsSafeFloor(room, next) || required.Contains(next)) && reachable.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return required.TrueForAll(reachable.Contains);
    }
}
