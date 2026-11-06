using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>
/// Caminos tras la extinción (planteamiento/04-caminos-tras-morir.md): revelar la elección, las órdenes
/// de la Brasa errante y su Calidez, el renacer al bajar de piso y la aparición del Renegado.
/// </summary>
internal static class Paths
{
    /// <summary>
    /// Inicio de la resolución: se apagan los Faros de la ronda anterior y se revelan los caminos elegidos
    /// durante la planificación. Una oferta sin elegir toma Amistad (el plazo ya venció al cerrar la ronda).
    /// </summary>
    public static void RevealChoices(RoundContext ctx)
    {
        foreach (var hero in ctx.Heroes.Where(h => h.BeaconActive).ToList())
        {
            ctx.Update(hero with { BeaconActive = false });
        }

        foreach (var hero in ctx.Heroes.Where(h => h.PathOfferPending).OrderBy(h => h.Seat).ToList())
        {
            var path = hero.PathChoice ?? PathKind.Friendship;
            if (hero.PathChoice is null)
            {
                ctx.Emit(new PathSelected(hero.Player, path, Auto: true) { Audience = Audience.Only(hero.Player) });
            }

            var cleared = hero with
            {
                PathOfferPending = false,
                PathChoice = null,
                SkillCooldown = 0,
                HinderCooldown = 0,
                Shield = 0,
                EmberShieldBy = [],
                EchoQueue = [],
            };
            ctx.Emit(new PathChosen(hero.Player, path));
            if (path == PathKind.Friendship)
            {
                ctx.Update(cleared with { Status = HeroStatus.EmberSpirit, Echoes = 0 });
                continue;
            }

            var embers = PathRules.RenegadeEmbers(hero);
            ctx.Update(cleared with
            {
                Status = HeroStatus.Renegade,
                AwaitingSpawn = true,
                SpawnRound = ctx.IsWardenRoom ? ctx.Round + PathRules.RenegadeSpawnDelay : 0,
                Embers = 0,
                SavedEmbers = 0,
                WarnedHeroes = [],
            });

            // La deuda sobrevive: lo que recupera paga primero a la Usurera.
            Combat.GainEmbers(ctx, hero.Player, embers, EmberReason.RenegadeReturn);
        }
    }

    /// <summary>Faro (GUARDAR de la Brasa): ilumina un radio de 2 para todo el grupo hasta la próxima resolución.</summary>
    public static void Beacon(RoundContext ctx, Hero ember)
    {
        ctx.Update(ember with { BeaconActive = true });
        ctx.Beacons[ember.Player] = ember.Position;
        ctx.Emit(new EmberAbilityUsed(ember.Player, EmberAbility.Beacon, null, ember.Position) { Audience = Audience.Group(ember.Position) });
    }

    /// <summary>Deriva (MOVER de la Brasa): hasta 2 casillas atravesando todo, sin salir de la sala.</summary>
    public static void Drift(RoundContext ctx, Hero ember, Direction direction)
    {
        var target = ember.Position;
        for (var i = 0; i < PathRules.DriftDistance && ctx.Room.IsInside(target.Step(direction)); i++)
        {
            target = target.Step(direction);
        }

        ctx.Update(ember with { Position = target, Facing = direction });
        if (target != ember.Position)
        {
            ctx.Emit(new HeroMoved(ember.Player, ember.Position, target) { Audience = Audience.Group(target) });
        }
    }

    /// <summary>Avivar: +1 de escudo al primer aliado a ≤2 casillas en esa dirección (máx. 2 de escudo de brasas).</summary>
    public static void Kindle(RoundContext ctx, Hero ember, Direction direction)
    {
        ctx.Update(ember with { Facing = direction, SkillCooldown = PathRules.KindleCooldown });
        var target = RoundContext.FirstInLine(ember.Position, direction, PathRules.KindleRange, p =>
            ctx.HeroAt(p) is { IsActive: true } hero ? hero : null);
        if (target is not null && target.EmberShieldBy.Count < PathRules.MaxEmberShield && !target.Has(Blessing.BrokenMirror))
        {
            ctx.Update(target with { Shield = target.Shield + 1, EmberShieldBy = target.EmberShieldBy.Add(ember.Player) });
        }
        else
        {
            target = null;
        }

        ctx.Emit(new EmberAbilityUsed(ember.Player, EmberAbility.Kindle, target is null ? null : EntityRef.ForHero(target.Player), null)
        {
            Audience = Audience.Group(ember.Position),
        });
    }

    /// <summary>Reavivar: levanta a un aliado caído adyacente con 1 de vida. +3 ecos.</summary>
    public static void Rekindle(RoundContext ctx, Hero ember, Direction direction)
    {
        ctx.Update(ember with { Facing = direction });
        if (ctx.HeroAt(ember.Position.Step(direction)) is not { Status: HeroStatus.Down, CanBeRevived: true } fallen)
        {
            return;
        }

        Combat.StandUp(ctx, fallen, PathRules.RekindleHp, HeroRules.MiserRekindleEmbers);
        ctx.Emit(new HeroRevived(ember.Player, fallen.Player));
        ctx.GainEchoes(ember.Player, PathRules.RekindleEchoes, EchoReason.Rekindled);
    }

    /// <summary>
    /// Estorbo: el primer enemigo a ≤2 casillas en esa dirección ejecuta su ataque anunciado una ronda más
    /// tarde. No funciona con el Celador en fase 2. +1 eco si ese ataque iba a golpear a un héroe.
    /// </summary>
    public static void Hinder(RoundContext ctx, Hero ember, Direction direction)
    {
        ctx.Update(ember with { Facing = direction });
        var enemy = RoundContext.FirstInLine(ember.Position, direction, PathRules.HinderRange, ctx.EnemyAt);
        if (enemy is null || enemy is { Kind: EnemyKind.Warden, Phase: 2 })
        {
            ctx.Emit(new EmberAbilityUsed(ember.Player, EmberAbility.Hinder, null, null) { Audience = Audience.Group(ember.Position) });
            return;
        }

        ctx.Update(ctx.Hero(ember.Player) with { HinderCooldown = PathRules.HinderCooldown });
        ctx.Update(enemy with { Delayed = true });
        ctx.Emit(new EmberAbilityUsed(ember.Player, EmberAbility.Hinder, EntityRef.ForEnemy(enemy.Id), null)
        {
            Audience = Audience.Group(ember.Position),
        });
        if (enemy.Telegraph.Any(t => ctx.HeroAt(t) is { IsActive: true }))
        {
            ctx.GainEchoes(ember.Player, 1, EchoReason.AttackHindered);
        }
    }

    /// <summary>+1 eco a cada Brasa cuyo Faro de esta ronda cubre la casilla del golpe final.</summary>
    public static void OnKill(RoundContext ctx, Position position)
    {
        foreach (var (ember, center) in ctx.Beacons.OrderBy(b => ctx.Hero(b.Key).Seat).ToList())
        {
            if (center.DistanceTo(position) <= PathRules.BeaconRadius)
            {
                ctx.GainEchoes(ember, 1, EchoReason.BeaconKill);
            }
        }
    }

    /// <summary>
    /// Calidez: un aliado herido que termina la ronda junto a la Brasa (o en su casilla) cura 1, una vez
    /// cada 3 rondas por aliado. +1 eco por curación.
    /// </summary>
    public static void Warmth(RoundContext ctx)
    {
        foreach (var ember in ctx.Heroes.Where(h => h.IsEmber).OrderBy(h => h.Seat).ToList())
        {
            var allies = ctx.Heroes
                .Where(h => h is { IsActive: true, HasBody: true, WarmthCooldown: 0, CanHeal: true } && h.Hp < h.MaxHp && h.DistanceTo(ember.Position) <= 1)
                .OrderBy(h => h.Seat)
                .ToList();
            foreach (var ally in allies)
            {
                Combat.Heal(ctx, ally.Player, PathRules.WarmthHeal);
                ctx.Update(ctx.Hero(ally.Player) with { WarmthCooldown = PathRules.WarmthEvery });
                ctx.Emit(new EmberAbilityUsed(ember.Player, EmberAbility.Warmth, EntityRef.ForHero(ally.Player), null)
                {
                    Audience = Audience.Group(ember.Position),
                });
                ctx.GainEchoes(ember.Player, 1, EchoReason.Warmth);
            }
        }
    }

    /// <summary>En la sala del Celador, el Renegado reaparece 2 rondas después de revelar su camino, junto al Celador.</summary>
    public static void SpawnDueRenegades(RoundContext ctx)
    {
        if (!ctx.IsWardenRoom)
        {
            return;
        }

        var due = ctx.Heroes
            .Where(h => h is { IsRenegade: true, AwaitingSpawn: true } && h.SpawnRound > 0 && h.SpawnRound <= ctx.Round)
            .OrderBy(h => h.Seat)
            .ToList();
        foreach (var renegade in due)
        {
            var warden = ctx.Enemies.Find(e => e.Kind == EnemyKind.Warden);
            var tile = warden is not null ? ctx.FreeTilesNear(warden.Position).Select(p => (Position?)p).FirstOrDefault() : FarthestFreeTile(ctx);
            if (tile is { } position)
            {
                Appear(ctx, renegade.Player, position);
            }
        }
    }

    /// <summary>Las Brasas renacen como héroes al entrar a la sala nueva (antes de colocar al grupo).</summary>
    public static List<PlayerId> Rebirth(RoundContext ctx)
    {
        var reborn = new List<PlayerId>();
        foreach (var ember in ctx.Heroes.Where(h => h.IsEmber).OrderBy(h => h.Seat).ToList())
        {
            reborn.Add(ember.Player);
            var hp = PathRules.RebirthHp(ember.MaxHp);
            var embers = PathRules.RebirthEmbers(ember, ember.Echoes);
            ctx.Update(ember with
            {
                Status = HeroStatus.Active,
                Hp = hp,
                Embers = 0,
                SavedEmbers = 0,
                Echoes = 0,
                SkillCooldown = 0,
                HinderCooldown = 0,
                WarmthCooldown = 0,
                BeaconActive = false,
            });

            // La deuda sobrevive a la Brasa: las ascuas con las que se renace pagan.
            Combat.GainEmbers(ctx, ember.Player, embers, EmberReason.Rebirth);
        }

        return reborn;
    }

    /// <summary>Al entrar a la sala nueva, cada Renegado aparece en la casilla libre más lejana de los héroes.</summary>
    public static void PlaceRenegades(RoundContext ctx)
    {
        var renegades = ctx.Heroes
            .Where(h => h.IsPresentRenegade || h is { IsRenegade: true, SpawnRound: 0 })
            .OrderBy(h => h.Seat)
            .ToList();
        foreach (var renegade in renegades)
        {
            if (FarthestFreeTile(ctx) is { } tile)
            {
                Appear(ctx, renegade.Player, tile);
            }
        }
    }

    /// <summary>
    /// Un Renegado aparece en la sala: vida 60% (40% en la del Celador) si vuelve de la extinción, +1 de escudo
    /// a cada héroe activo y, en la sala del Celador, 3 rondas sin habilidad y el Celador pierde 6 de vida máxima.
    /// </summary>
    private static void Appear(RoundContext ctx, PlayerId player, Position tile)
    {
        var renegade = ctx.Hero(player);
        var wardenRoom = ctx.IsWardenRoom;
        var fresh = renegade.AwaitingSpawn;
        renegade = renegade with
        {
            Position = tile,
            Facing = Direction.South,
            AwaitingSpawn = false,
            SpawnRound = 0,
            Hp = fresh ? PathRules.RenegadeHp(renegade.MaxHp, wardenRoom) : renegade.Hp,
            SkillCooldown = wardenRoom ? Math.Max(renegade.SkillCooldown, PathRules.RenegadeFinalSkillLock) : renegade.SkillCooldown,
        };
        ctx.Update(renegade);
        HeroSkills.PlaceSecondBody(ctx, renegade);
        ctx.MakeNoise(ctx.Hero(player));
        ctx.Emit(new RenegadeSpawned(player, tile));

        foreach (var hero in ctx.Heroes.Where(h => h.IsActive).ToList())
        {
            ctx.Update(hero with { Shield = hero.Shield + (hero.Has(Blessing.BrokenMirror) ? 0 : 1) });
        }

        if (wardenRoom && ctx.Enemies.Find(e => e.Kind == EnemyKind.Warden) is { } warden)
        {
            var maxHp = Math.Max(PathRules.WardenMinHp, warden.MaxHp - PathRules.WardenHpPerRenegade);
            var lost = warden.MaxHp - maxHp;
            ctx.Update(warden with { MaxHp = maxHp, Hp = Math.Max(1, warden.Hp - lost) });
        }
    }

    /// <summary>Casilla libre más lejana de los héroes (a ≥4 de todos si se puede); en empate, fila y columna menores.</summary>
    private static Position? FarthestFreeTile(RoundContext ctx)
    {
        var heroes = ctx.Heroes.Where(h => h.Status is HeroStatus.Active or HeroStatus.Down).SelectMany(h => h.Bodies).ToList();
        var candidates = ctx.Room.AllPositions()
            .Where(p => ctx.IsFreeFor(p, flies: false) && ctx.Room.TerrainAt(p) != Terrain.Stairs)
            .Select(p => (Position: p, Distance: heroes.Count == 0 ? int.MaxValue : heroes.Min(h => h.DistanceTo(p))))
            .OrderByDescending(c => c.Distance)
            .ThenBy(c => c.Position.Y)
            .ThenBy(c => c.Position.X)
            .ToList();
        return candidates.Count == 0 ? null : candidates[0].Position;
    }
}
