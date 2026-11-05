using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>Sabotaje y Empujón (06-version-3.md §2 y §7). Se resuelven en la fase de acciones, por iniciativa.</summary>
internal static class Treachery
{
    /// <summary>
    /// Sabotaje: 1 de daño anónimo al primer aliado activo en línea a ≤2. Sin aliado no se gasta. No hace ruido ni gira al
    /// héroe: nada en la vista de los demás lo delata. Si un Defender lo anula, se gasta y no cuenta como traición.
    /// </summary>
    public static void Sabotage(RoundContext ctx, Hero hero, Direction direction)
    {
        if (!hero.CanSabotage(ctx.RoomIndex))
        {
            return;
        }

        var (victim, tile) = FirstAlly(ctx, hero, hero.Position, direction);
        if (victim is null && hero.SecondBody is { } second)
        {
            (victim, tile) = FirstAlly(ctx, hero, second, direction.Mirrored());
        }

        if (victim is null)
        {
            return;
        }

        ctx.Update(hero with { SabotagedRoom = ctx.RoomIndex });
        var dealt = Combat.HitHero(ctx, victim.Player, HeroRules.SabotageDamage, Attacker.None, tile);
        if (dealt > 0)
        {
            Combat.RecordBetrayal(ctx, hero.Player, victim.Player, dealt, BetrayalKind.Sabotage);
        }
    }

    private static (Hero? Victim, Position Tile) FirstAlly(RoundContext ctx, Hero hero, Position origin, Direction direction)
    {
        var position = origin;
        for (var i = 0; i < HeroRules.SabotageReach; i++)
        {
            position = position.Step(direction);
            if (ctx.Room.TerrainAt(position) is Terrain.Wall or Terrain.Brazier)
            {
                break;
            }

            if (ctx.HeroAt(position) is { IsActive: true } ally && ally.Player != hero.Player)
            {
                return (ally, position);
            }
        }

        return (null, position);
    }

    /// <summary>
    /// Empujón: mueve 1 casilla a lo que esté adyacente en esa dirección, si la llegada se puede pisar y está libre. Es
    /// visible y con autor. Sin efecto no se gasta. Hilván empuja desde sus dos cuerpos con un solo uso.
    /// </summary>
    public static void Push(RoundContext ctx, Hero hero, Direction direction)
    {
        if (!hero.CanPush || hero.PushCooldown > 0)
        {
            return;
        }

        var pushed = PushFrom(ctx, hero, hero.Position, direction);
        hero = ctx.Hero(hero.Player);
        if (hero.SecondBody is { } second && hero.HasBody)
        {
            pushed |= PushFrom(ctx, hero, second, direction.Mirrored());
        }

        if (!pushed)
        {
            return;
        }

        hero = ctx.Hero(hero.Player);
        ctx.Update(hero with
        {
            Facing = direction,
            PushCooldown = HeroRules.PushCooldown,
            Progress = hero.Progress with { Pushes = hero.Progress.Pushes + 1 },
        });
        ctx.MakeNoise(ctx.Hero(hero.Player));
    }

    private static bool PushFrom(RoundContext ctx, Hero pusher, Position origin, Direction direction)
    {
        var from = origin.Step(direction);
        var to = from.Step(direction);
        if (!Room.IsInside(to) || !ctx.IsWalkable(to, flies: false, avoidCracks: false) || ctx.IsOccupied(to))
        {
            return false;
        }

        if (ctx.EnemyAt(from) is { } enemy)
        {
            return !pusher.IsRenegade && PushEnemy(ctx, pusher, enemy, to, direction);
        }

        return ctx.HeroAt(from) is { } target && target.Player != pusher.Player && PushHero(ctx, pusher, target, from, to);
    }

    private static bool PushEnemy(RoundContext ctx, Hero pusher, Enemy enemy, Position to, Direction direction)
    {
        if (enemy.Kind is EnemyKind.Warden or EnemyKind.Totem)
        {
            return false;
        }

        var from = enemy.Position;
        enemy = enemy with { Position = to };
        ctx.Update(enemy);
        ctx.MakeNoise(enemy);
        if (ctx.PassengerOf(enemy.Id) is { } passenger)
        {
            ctx.Update(passenger with { Position = to });
        }

        ctx.Emit(new Pushed(pusher.Player, EntityRef.ForEnemy(enemy.Id), from, to) { Audience = Audience.At(from) });

        var terrain = ctx.Room.TerrainAt(to);
        if (!enemy.Flies && (terrain is Terrain.Crack or Terrain.Spikes || ctx.Room.FireAt(to) is not null))
        {
            var current = ctx.Hero(pusher.Player);
            ctx.Update(current with { Progress = current.Progress with { HazardPushes = current.Progress.HazardPushes + 1 } });
        }

        if (terrain == Terrain.Crack && !enemy.Flies)
        {
            // Golpe final de quien empuja; se le quita toda la vida sin inmunidad posible.
            Combat.HitEnemy(ctx, enemy.Id, enemy.Hp, Attacker.Originless(pusher));
        }

        return true;
    }

    private static bool PushHero(RoundContext ctx, Hero pusher, Hero target, Position from, Position to)
    {
        // El Traidor empuja héroes, no a otros Traidores: no se dañan entre sí.
        if (pusher.IsRenegade && target.IsRenegade)
        {
            return false;
        }

        target = target.Position == from ? target with { Position = to } : target with { SecondBody = to };
        ctx.Update(target);
        ctx.MakeNoise(target);
        ctx.Emit(new Pushed(pusher.Player, EntityRef.ForHero(target.Player), from, to) { Audience = Audience.At(from) });

        if (ctx.Room.TerrainAt(to) == Terrain.Crack && target.Class != HeroClass.Mampostero)
        {
            if (target.IsRenegade)
            {
                Combat.DefeatRenegade(ctx, target.Player, Attacker.Originless(pusher));
            }
            else
            {
                Combat.KnockDown(ctx, target.Player);
            }

            return true;
        }

        if (target.IsActive || target.IsPresentRenegade)
        {
            Combat.PickUpGroundEmbers(ctx, target.Player, to);
        }

        return true;
    }
}
