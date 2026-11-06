using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>
/// Activas y pasivas de los nueve héroes (planteamiento/05-heroes.md). Una habilidad que no hace nada no gasta el
/// enfriamiento. Corrimiento (fase 2) vive en <see cref="RoomShift"/>; Volver se resuelve en la fase 1.
/// </summary>
internal static class HeroSkills
{
    /// <summary>Habilidades que se resuelven en la fase 4, por iniciativa.</summary>
    public static void Use(RoundContext ctx, Hero hero, Direction direction)
    {
        switch (hero.Class)
        {
            case HeroClass.Errata:
                Correct(ctx, hero, direction);
                break;
            case HeroClass.Hilvan:
                Tug(ctx, hero, direction);
                break;
            case HeroClass.Polizon:
                Board(ctx, hero, direction);
                break;
            case HeroClass.Escucha:
                Shout(ctx, hero, direction);
                break;
            case HeroClass.Usurera:
                Loan(ctx, hero, direction);
                break;
            case HeroClass.Avaro:
                Bribe(ctx, hero, direction);
                break;
            case HeroClass.Campanero:
                RingBell(ctx, hero, direction);
                break;
            default:
                // Volver (fase 1) y Corrimiento (fase 2) ya se resolvieron.
                break;
        }
    }

    /// <summary>Deja constancia de la habilidad: enfriamiento, mirada, ruido y evento para quien vea al héroe.</summary>
    private static void Spend(RoundContext ctx, Hero hero, Direction direction, int? cooldown = null, Audience? audience = null)
    {
        hero = hero with { Facing = direction, SkillCooldown = cooldown ?? hero.SkillCooldownOnUse };
        ctx.Update(hero);
        ctx.MakeNoise(hero);
        ctx.Emit(new SkillUsed(hero.Player, hero.Class, direction) { Audience = audience ?? RoundContext.AudienceOf(hero) });
    }

    // ───────────── Errata ─────────────

    /// <summary>
    /// Corregir: cada enemigo con alguna casilla anunciada a ≤2 desplaza todo su anuncio 1 casilla. La que caería en
    /// un muro o fuera de la sala se pierde. El uso es anónimo: los demás solo ven que el anuncio se movió.
    /// </summary>
    private static void Correct(RoundContext ctx, Hero errata, Direction direction)
    {
        var affected = ctx.Enemies
            .Where(e => e.Telegraph.Exists(t => t.DistanceTo(errata.Position) <= HeroRules.CorrectReach))
            .ToList();
        if (affected.Count == 0)
        {
            return;
        }

        Spend(ctx, errata, direction, audience: Audience.Only(errata.Player));
        foreach (var enemy in affected)
        {
            var tiles = enemy.Telegraph
                .Select(t => t.Step(direction))
                .Where(t => ctx.Room.TerrainAt(t) != Terrain.Wall)
                .ToList();
            ctx.Update(enemy with { Telegraph = [.. tiles], DivertedBy = errata.Player });
            ctx.Emit(new AttackCorrected(enemy.Id, [.. tiles]) { Audience = Audience.At(enemy.Position) });
        }
    }

    /// <summary>
    /// Ejecuta una casilla de un anuncio desviado: golpea todo lo que haya, con el daño de quien lo anunció. El golpe
    /// no tiene origen y el registro culpa al monstruo; si cae sobre un aliado es una traición.
    /// </summary>
    public static void DivertedStrike(RoundContext ctx, Enemy announcer, Position tile, PlayerId errataId)
    {
        var errata = ctx.Hero(errataId);
        var damage = announcer.Stats.Damage;

        // Se golpea lo que había al caer el golpe: si muere un huésped, Polizón sale a esa casilla y no recibe este golpe.
        var hero = ctx.HeroAt(tile);
        if (ctx.EnemyAt(tile) is { } enemy && !errata.IsRenegade)
        {
            // El Celador recibe la mitad, hacia arriba, de su propio golpe desviado.
            var dealt = enemy.Id == announcer.Id && enemy.Kind == EnemyKind.Warden ? (damage + 1) / 2 : damage;
            Combat.HitEnemy(ctx, enemy.Id, dealt, Attacker.Anonymous(errataId, announcer.Id, null));
        }

        if (hero is { IsVulnerable: true })
        {
            AnonymousHit(ctx, errata, hero, damage, announcer.Id, tile, BetrayalKind.Diverted);
        }
    }

    /// <summary>
    /// Golpe anónimo de un héroe a otro portador: sin fuego amigo ni robo. A un aliado es una traición secreta; a un
    /// rival cuenta para quien lo dio (y un Renegado nunca hace más de 1).
    /// </summary>
    private static void AnonymousHit(RoundContext ctx, Hero author, Hero target, int damage, int shownEnemy, Position tile, BetrayalKind kind)
    {
        if (target.Player == author.Player)
        {
            Combat.HitHero(ctx, target.Player, damage, new Attacker(null, shownEnemy, null), tile);
            return;
        }

        if (author.IsRenegade)
        {
            if (!target.IsRenegade)
            {
                Combat.HitHero(ctx, target.Player, Math.Min(damage, PathRules.RenegadeMaxDamage), new Attacker(null, shownEnemy, null), tile);
            }

            return;
        }

        if (target.IsRenegade)
        {
            Combat.HitHero(ctx, target.Player, damage, Attacker.Anonymous(author.Player, shownEnemy, null), tile);
            return;
        }

        var dealt = Combat.HitHero(ctx, target.Player, damage, new Attacker(null, shownEnemy, null), tile);
        if (dealt > 0)
        {
            Combat.RecordBetrayal(ctx, author.Player, target.Player, dealt, kind);
        }
    }

    // ───────────── Anteayer ─────────────

    /// <summary>Guarda dónde está Anteayer al empezar la ronda: Volver salta a esta casilla 2 rondas después.</summary>
    public static void RecordHistory(RoundContext ctx)
    {
        foreach (var hero in ctx.Heroes.Where(h => h is { Class: HeroClass.Anteayer, HasBody: true } && h.CanAct).ToList())
        {
            var history = hero.History.RemoveAll(t => t.Round < ctx.Round - HeroRules.RewindDelay);
            ctx.Update(hero with { History = history.Add(new HeroTrace(ctx.Round, hero.Position, hero.Hp)) });
        }
    }

    /// <summary>Volver (fase 1): salta a donde estaba hace 2 rondas y recupera la vida que tenía si era más.</summary>
    public static void Rewind(RoundContext ctx, Dictionary<PlayerId, Order> plan)
    {
        foreach (var (player, order) in plan.OrderBy(p => ctx.Hero(p.Key).Seat))
        {
            var hero = ctx.Hero(player);
            if (order is not { Type: OrderType.Skill, Direction: { } direction } || hero is not { Class: HeroClass.Anteayer, IsEmber: false })
            {
                continue;
            }

            var trace = hero.History.Find(t => t.Round == ctx.Round - HeroRules.RewindDelay);
            if (trace is null)
            {
                continue;
            }

            var landing = trace.Position == hero.Position ? trace.Position : ctx.FreeTileOrNearest(trace.Position) ?? hero.Position;
            var hp = Math.Min(hero.MaxHp, Math.Max(hero.Hp, trace.Hp));
            if (landing == hero.Position && hp == hero.Hp)
            {
                continue;
            }

            var from = hero.Position;
            Spend(ctx, hero, direction);
            hero = ctx.Hero(player) with { Position = landing };
            ctx.Update(hero);
            ctx.MakeNoise(hero);
            if (landing != from)
            {
                ctx.Emit(new HeroMoved(player, from, landing) { Audience = Audience.At(landing) });
                Combat.PickUpGroundEmbers(ctx, player, landing);
            }

            if (hp > hero.Hp)
            {
                Combat.Heal(ctx, player, hp - hero.Hp);
            }
        }
    }

    /// <summary>El Eco repite el ataque desde donde estaba, con el daño y el alcance actuales de su dueño.</summary>
    public static void FireEchoes(RoundContext ctx, PlayerId player)
    {
        var hero = ctx.Hero(player);
        var due = hero.EchoQueue.FindAll(e => e.FireRound <= ctx.Round);
        if (due.IsEmpty)
        {
            return;
        }

        ctx.Update(hero with { EchoQueue = hero.EchoQueue.RemoveAll(e => e.FireRound <= ctx.Round) });
        foreach (var echo in due)
        {
            ctx.Emit(new EchoFired(player, echo.Origin, echo.Direction) { Audience = Audience.At(echo.Origin) });
            HeroActions.LineAttack(ctx, player, echo.Origin, echo.Direction, hero.Stats.Range, hero.Damage, echo: true);
        }
    }

    // ───────────── Hilván ─────────────

    /// <summary>Tensar: con los cuerpos alineados, a ≥2 y sin muro entre ellos, daña todo lo que haya entre ellos.</summary>
    private static void Tug(RoundContext ctx, Hero hilvan, Direction direction)
    {
        if (hilvan.SecondBody is not { } second || (hilvan.Position.X != second.X && hilvan.Position.Y != second.Y))
        {
            return;
        }

        var between = Between(hilvan.Position, second);
        if (between.Count == 0 || between.Exists(t => ctx.Room.TerrainAt(t) == Terrain.Wall))
        {
            return;
        }

        var enemies = between.Select(ctx.EnemyAt).OfType<Enemy>().Where(_ => !hilvan.IsRenegade).ToList();
        var heroes = between
            .Select(t => (Tile: t, Hero: ctx.HeroAt(t)))
            .Where(c => c.Hero is { IsVulnerable: true } h && h.Player != hilvan.Player && !(hilvan.IsRenegade && h.IsRenegade))
            .ToList();
        if (enemies.Count == 0 && heroes.Count == 0)
        {
            return;
        }

        Spend(ctx, hilvan, direction);
        var damage = hilvan.IsRenegade ? PathRules.RenegadeMaxDamage : HeroRules.TugDamage;
        foreach (var enemy in enemies)
        {
            // Lo cruza desde ambos lados: no tiene origen, así que tampoco hay inmunidad frontal.
            Combat.HitEnemy(ctx, enemy.Id, damage, Attacker.Originless(hilvan));
        }

        foreach (var (tile, hero) in heroes)
        {
            Combat.HitHero(ctx, hero!.Player, damage, Attacker.Originless(hilvan), tile);
        }
    }

    private static List<Position> Between(Position a, Position b)
    {
        var tiles = new List<Position>();
        if (a.DirectionTo(b) is { } direction)
        {
            for (var tile = a.Step(direction); tile != b; tile = tile.Step(direction))
            {
                tiles.Add(tile);
            }
        }

        return tiles;
    }

    /// <summary>Segundo cuerpo: a 2 casillas en la misma fila (al este si se puede) o en la libre más cercana.</summary>
    public static Hero PlaceSecondBody(RoundContext ctx, Hero hero)
    {
        if (hero.Class != HeroClass.Hilvan)
        {
            return hero;
        }

        hero = hero with { SecondBody = null };
        ctx.Update(hero);
        var east = hero.Position.Step(Direction.East, HeroRules.BodySpacing);
        var west = hero.Position.Step(Direction.West, HeroRules.BodySpacing);
        var tile = ctx.IsFreeFor(east, flies: false) ? east
            : ctx.IsFreeFor(west, flies: false) ? west
            : ctx.FreeTilesNear(hero.Position).Select(p => (Position?)p).FirstOrDefault();
        hero = hero with { SecondBody = tile };
        ctx.Update(hero);
        return hero;
    }

    // ───────────── Polizón ─────────────

    /// <summary>
    /// Abordar: entra en el enemigo adyacente, que deja de anunciar y de actuar por su cuenta. Al Tótem no se entra;
    /// al Celador tampoco: lo gira 180° hasta su próximo anuncio.
    /// </summary>
    private static void Board(RoundContext ctx, Hero polizon, Direction direction)
    {
        var target = ctx.EnemyAt(polizon.Position.Step(direction));
        if (target is null or { Kind: EnemyKind.Totem } || ctx.PassengerOf(target.Id) is not null)
        {
            return;
        }

        if (target.Kind == EnemyKind.Warden)
        {
            var cooldown = HeroRules.WardenTurnCooldown * (polizon.IsRenegade ? PathRules.RenegadeCooldownFactor : 1);
            Spend(ctx, polizon, direction, cooldown);
            var facing = target.Facing.Opposite();
            ctx.Update(target with { Facing = facing });
            ctx.Emit(new WardenTurned(target.Id, facing) { Audience = Audience.At(target.Position) });
            return;
        }

        ctx.Update(target with { Telegraph = [], Delayed = false, DivertedBy = null });
        ctx.Update(polizon with
        {
            HostEnemyId = target.Id,
            BoardedRound = ctx.Round,
            Position = target.Position,
            Facing = direction,
        });
        ctx.SkippingEnemies.Add(target.Id);
        ctx.Emit(new HostBoarded(polizon.Player, target.Id) { Audience = Audience.Only(polizon.Player) });
    }

    /// <summary>ATACAR desde dentro: el huésped golpea ya, sin anunciar, con su daño y su alcance.</summary>
    public static void HostStrike(RoundContext ctx, Hero polizon, Direction direction)
    {
        if (ctx.Enemy(polizon.HostEnemyId ?? -1) is not { } host)
        {
            return;
        }

        ctx.Update(polizon with { Facing = direction });
        ctx.Update(host with { Facing = direction });
        ctx.MakeNoise(host);

        var position = host.Position;
        var range = host.Kind == EnemyKind.Crossbowman ? ctx.Room.Size : 1;
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
                Combat.ToppleBrazier(ctx, position, null);
                return;
            }

            if (ctx.EnemyAt(position) is { } enemy)
            {
                if (!polizon.IsRenegade)
                {
                    HeroActions.MarkAttacker(ctx, enemy.Id, polizon.Player);
                    Combat.HitEnemy(ctx, enemy.Id, host.Stats.Damage, Attacker.Anonymous(polizon.Player, host.Id, host.Position));
                }

                return;
            }

            if (ctx.HeroAt(position) is { IsVulnerable: true } hero)
            {
                AnonymousHit(ctx, polizon, hero, host.Stats.Damage, host.Id, position, BetrayalKind.FromHost);
                return;
            }
        }
    }

    /// <summary>HABILIDAD desde dentro: sale a esa casilla (o a la libre más cercana) y deja al huésped sin actuar esa ronda.</summary>
    public static void LeaveHost(RoundContext ctx, Hero polizon, Direction direction)
    {
        if (ctx.Enemy(polizon.HostEnemyId ?? -1) is not { } host)
        {
            return;
        }

        var tile = ctx.FreeTileOrNearest(host.Position.Step(direction));
        if (tile is null)
        {
            return;
        }

        ctx.SkippingEnemies.Add(host.Id);
        ctx.Update(polizon with { Facing = direction });
        Combat.ExitHost(ctx, polizon.Player, tile.Value);
    }

    /// <summary>
    /// Drenaje: cada 2 rondas dentro, el huésped pierde 1 de vida y Polizón cura 1. Si lo mata, es golpe final suyo
    /// (anónimo). Como Renegado no hay drenaje.
    /// </summary>
    public static void DrainHosts(RoundContext ctx)
    {
        foreach (var polizon in ctx.Heroes.Where(h => h is { IsInHost: true, IsRenegade: false }).ToList())
        {
            var inside = ctx.Round - polizon.BoardedRound;
            if (inside <= 0 || inside % HeroRules.DrainEvery != 0 || ctx.Enemy(polizon.HostEnemyId ?? -1) is not { } host)
            {
                continue;
            }

            ctx.Emit(new HostDrained(polizon.Player, host.Id) { Audience = Audience.Only(polizon.Player) });
            Combat.Heal(ctx, polizon.Player, HeroRules.DrainAmount);
            Combat.HitEnemy(ctx, host.Id, HeroRules.DrainAmount, Attacker.Anonymous(polizon.Player, null, null));
        }
    }

    // ───────────── La Escucha ─────────────

    /// <summary>Grito: su bando ve la sala entera hasta la próxima resolución y los monstruos la eligen esta ronda.</summary>
    private static void Shout(RoundContext ctx, Hero escucha, Direction direction)
    {
        Spend(ctx, escucha, direction);
        ctx.Update(ctx.Hero(escucha.Player) with { Shouting = true });
        ctx.Emit(new Shouted(escucha.Player));
    }

    public static void ClearShouts(RoundContext ctx)
    {
        foreach (var hero in ctx.Heroes.Where(h => h.Shouting).ToList())
        {
            ctx.Update(hero with { Shouting = false });
        }
    }

    // ───────────── La Usurera ─────────────

    /// <summary>
    /// Préstamo: al primer aliado en línea a ≤2, herido o caído, le cura 3 o lo levanta con 3 de vida y le crea una
    /// deuda de 2 ascuas. No pide permiso. Un aliado con la vida llena no cuenta, ni el Avaro.
    /// </summary>
    private static void Loan(RoundContext ctx, Hero usurera, Direction direction)
    {
        var target = RoundContext.FirstInLine(usurera.Position, direction, HeroRules.LoanReach, p =>
            ctx.HeroAt(p) is { } hero && hero.Player != usurera.Player && !hero.IsMiser && hero.IsSameFaction(usurera)
                && (hero is { Status: HeroStatus.Down, CanBeRevived: true } || (hero is { IsVulnerable: true, CanHeal: true } && hero.Hp < hero.MaxHp))
                ? hero
                : null);
        if (target is null)
        {
            return;
        }

        Spend(ctx, usurera, direction);
        if (target.Status == HeroStatus.Down)
        {
            Combat.StandUp(ctx, target, HeroRules.LoanReviveHp, HeroRules.MiserReviveEmbers);
            ctx.Emit(new HeroRevived(usurera.Player, target.Player));
        }
        else
        {
            Combat.Heal(ctx, target.Player, HeroRules.LoanHeal);
        }

        Combat.AddDebt(ctx, target.Player, usurera.Player, HeroRules.LoanDebt, DebtReason.Loan);
    }

    // ───────────── El Avaro ─────────────

    /// <summary>
    /// Soborno: paga al primer enemigo en línea a ≤3 para cancelar su anuncio; esa ronda no actúa. No se puede pagar
    /// con la última ascua. El Celador cuesta 3 y solo cancela el anuncio. El enemigo suelta el pago al morir.
    /// </summary>
    private static void Bribe(RoundContext ctx, Hero avaro, Direction direction)
    {
        var enemy = RoundContext.FirstInLine(avaro.Position, direction, HeroRules.BribeReach, ctx.EnemyAt);
        if (enemy is null)
        {
            return;
        }

        var warden = enemy.Kind == EnemyKind.Warden;
        var cost = warden ? HeroRules.WardenBribeCost : HeroRules.BribeCost;
        if (avaro.Embers <= cost)
        {
            return;
        }

        Spend(ctx, avaro, direction);
        Combat.GainEmbers(ctx, avaro.Player, -cost, EmberReason.Bribe);
        ctx.Update(enemy with { Telegraph = [], Delayed = false, DivertedBy = null, Bribes = enemy.Bribes + cost });
        if (!warden)
        {
            ctx.SkippingEnemies.Add(enemy.Id);
        }

        ctx.Emit(new EnemyBribed(avaro.Player, enemy.Id, cost) { Audience = Audience.At(enemy.Position) });
    }

    // ───────────── El Campanero ─────────────

    /// <summary>Repique: en la ronda siguiente los demás tienen menos tiempo; él conserva todo el reloj.</summary>
    private static void RingBell(RoundContext ctx, Hero campanero, Direction direction)
    {
        if (!ctx.Heroes.Exists(h => h.Player != campanero.Player && h.CanAct))
        {
            return;
        }

        Spend(ctx, campanero, direction);
        ctx.BellRound = ctx.Round + 1;
        ctx.BellRinger = campanero.Player;
        ctx.BellClosed = false;
        ctx.Emit(new BellRung(campanero.Player, ctx.BellRound));
    }
}
