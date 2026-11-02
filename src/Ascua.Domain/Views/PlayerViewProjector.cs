using System.Collections.Immutable;
using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Views;

/// <summary>
/// Filtra la información oculta (sección 8 de las reglas): niebla de radio 3 (Manhattan), pactos, bendiciones,
/// drafts y contenido de cofres ajenos. Con los caminos (planteamiento/04-caminos-tras-morir.md): la elección, los
/// ecos y las ascuas guardadas solo los ve su dueño; la Brasa es visible para el grupo; Renegados y héroes se ven
/// entre sí solo dentro de la visión; el Faro de una Brasa suma visión al grupo. Con los héroes
/// (planteamiento/05-heroes.md): Polizón dentro de un huésped no aparece para nadie, La Escucha solo percibe su radio
/// y lo que hizo ruido, Errata ve todos los anuncios sin su autor, las deudas son del deudor y la Usurera, el Grito
/// revela la sala a un bando y las traiciones anónimas no salen hasta el final.
/// </summary>
public static class PlayerViewProjector
{
    public static PlayerView For(GameState state, PlayerId viewer)
    {
        ArgumentNullException.ThrowIfNull(state);
        var me = state.FindHero(viewer) ?? throw new ArgumentException("El jugador no participa en esta partida.", nameof(viewer));
        var room = state.CurrentRoom;
        bool CanSee(Position p) => IsVisible(state, me, p);

        var allies = state.Heroes
            .Where(h => h.Player != viewer)
            .Select(h =>
            {
                var shown = IsShown(state, me, h, h.Position);
                return new AllyView(
                    h.Player,
                    h.Seat,
                    h.Class,
                    shown ? h.Position : null,
                    shown ? h.Facing : null,
                    h.Hp,
                    h.Status,
                    h.Embers,
                    h.Blessings.Count)
                {
                    SecondBody = h.SecondBody is { } second && IsShown(state, me, h, second) ? second : null,
                    DebtToMe = h.DebtTo == viewer && h.Debt > 0 ? h.Debt : null,
                };
            })
            .ToImmutableList();

        // La Escucha conoce el terreno de toda la sala aunque no perciba lo que hay encima.
        var tiles = Room.AllPositions()
            .Where(p => me.IsBlind || CanSee(p))
            .Select(p => new TileView(p, room.TerrainAt(p)))
            .ToImmutableList();

        var enemies = state.Enemies
            .Where(e => CanSeeEnemy(state, me, e))
            .Select(e => new EnemyView(e.Id, e.Kind, e.Position, e.Facing, e.Hp, e.MaxHp, e.Phase))
            .ToImmutableList();

        var plunderer = me.HasSynergy(Synergy.Plunderer);
        var chests = room.Chests
            .Where(c => CanSee(c.Position))
            .Select(c => new ChestView(
                c.Id,
                c.Position,
                c.Opened,
                plunderer && !c.Opened ? c.HasSilverKey : null,
                c.Opened && c.OpenedBy == viewer ? c.Loot : null))
            .ToImmutableList();

        var fires = room.Fires.Where(f => CanSee(f.Position)).Select(f => new FireView(f.Position, f.Rounds)).ToImmutableList();

        var telegraphs = state.Enemies
            .Select(e => new TelegraphView(CanSeeEnemy(state, me, e) ? e.Id : null, VisibleTelegraph(state, me, e.Id, e.Telegraph)))
            .Where(t => !t.Tiles.IsEmpty)
            .ToImmutableList();

        var echoes = state.Heroes
            .SelectMany(h => h.Player == viewer
                ? h.EchoQueue.Select(e => (h.Player, Echo: e))
                : h.EchoQueue.Take(1).Where(e => SeesTile(state, me, e.Origin)).Select(e => (h.Player, Echo: e)))
            .Select(e => new EchoView(e.Player, e.Echo.Origin, e.Echo.Direction, e.Echo.FireRound))
            .ToImmutableList();

        var ground = room.Ground.Where(g => CanSee(g.Position)).Select(g => new GroundEmbersView(g.Position, g.Amount)).ToImmutableList();

        var rewind = me.CanAct ? me.History.Find(t => t.Round == state.Round - HeroRules.RewindDelay) : null;
        var self = new SelfView(
            me.Class,
            me.Position,
            me.Facing,
            me.Hp,
            me.MaxHp,
            me.Shield,
            me.Embers,
            me.Status,
            me.DownRounds,
            me.SkillCooldown,
            me.Frozen,
            me.BurnRounds,
            me.SilverKeys,
            me.VisionRadius,
            me.Blessings,
            BlessingRules.SynergiesOf(me.Blessings),
            me.DraftOptions,
            me.Pact,
            new PathView(
                me.IsChoosingPath,
                me.Extinctions,
                me.PathChoice,
                me.SavedEmbers,
                me.Echoes,
                me.HinderCooldown,
                me.AwaitingSpawn,
                me.AwaitingSpawn && me.SpawnRound > 0 ? me.SpawnRound : null))
        {
            SecondBody = me.SecondBody,
            HostEnemyId = me.HostEnemyId,
            Debt = me.Debt,
            DebtTo = me.DebtTo,
            RewindTile = rewind?.Position,
            RewindHp = rewind?.Hp,
        };

        var outcome = state.Outcome is { } o ? new OutcomeView(o.Kind, o.Reason, o.Winners, o.Scores) { Betrayals = o.Betrayals } : null;

        return new PlayerView(
            viewer,
            state.Round,
            state.MaxRounds,
            state.Phase,
            state.RoomIndex,
            state.Rooms.Count,
            state.LastSeq,
            self,
            allies,
            tiles,
            enemies,
            chests,
            fires,
            telegraphs,
            outcome)
        {
            Echoes = echoes,
            Ground = ground,
            Noise = me.IsBlind ? state.NoiseTiles : [],
            BellRinger = state.IsBellRound ? state.BellRinger : null,
            BellClosed = state.IsBellRound && state.BellClosed,
        };
    }

    /// <summary>
    /// Eventos que el jugador puede recibir, ya recortados: anuncios solo con las casillas que ve
    /// y sin delatar al autor de un golpe anónimo.
    /// </summary>
    public static IReadOnlyList<DomainEvent> VisibleEvents(GameState state, IEnumerable<DomainEvent> events, PlayerId viewer)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(events);
        var me = state.FindHero(viewer) ?? throw new ArgumentException("El jugador no participa en esta partida.", nameof(viewer));
        var result = new List<DomainEvent>();
        foreach (var domainEvent in events)
        {
            var projected = Project(state, me, domainEvent);
            if (projected is not null)
            {
                result.Add(projected);
            }
        }

        return result;
    }

    private static DomainEvent? Project(GameState state, Hero me, DomainEvent domainEvent)
    {
        var audience = domainEvent.Audience;
        switch (domainEvent)
        {
            case AttackTelegraphed telegraphed:
            {
                var tiles = VisibleTelegraph(state, me, telegraphed.EnemyId, telegraphed.Tiles);
                var source = audience.Position is { } origin && IsVisible(state, me, origin) ? telegraphed.EnemyId : null;
                return tiles.IsEmpty ? null : telegraphed with { EnemyId = source, Tiles = tiles };
            }

            case AttackCorrected corrected:
            {
                var tiles = VisibleTelegraph(state, me, corrected.EnemyId, corrected.Tiles);
                var source = audience.Position is { } origin && IsVisible(state, me, origin) ? corrected.EnemyId : null;
                return tiles.IsEmpty ? null : corrected with { EnemyId = source, Tiles = tiles };
            }

            default:
                break;
        }

        var allowed = audience.Kind switch
        {
            AudienceKind.Everyone => true,
            AudienceKind.Player => audience.Player == me.Player,
            AudienceKind.Location => audience.Position is { } p && IsVisible(state, me, p),
            AudienceKind.Group => (!me.IsRenegade && !me.IsBlind) || (audience.Position is { } g && IsVisible(state, me, g)),
            _ => false,
        };
        if (!allowed)
        {
            return null;
        }

        return domainEvent switch
        {
            EnemyKilled { HiddenKiller: true } killed when killed.Killer != me.Player => killed with { Killer = null },
            RenegadeDefeated { HiddenBy: true } defeated when defeated.By != me.Player => defeated with { By = null },
            RenegadeSpawned spawned when spawned.Position is { } at && !IsSpawnShown(state, me, spawned.Player, at) => spawned with { Position = null },
            _ => domainEvent,
        };
    }

    /// <summary>
    /// Casillas de un anuncio que el jugador ve. Errata y Ojo de búho ven todas (sin saber de quién son, salvo que vean
    /// al enemigo). La Escucha, solo si caen a su alcance o el enemigo es uno que oye.
    /// </summary>
    private static ImmutableList<Position> VisibleTelegraph(GameState state, Hero me, int? enemyId, ImmutableList<Position> tiles)
    {
        if (me.Has(Blessing.OwlEye) || me.SeesAllTelegraphs)
        {
            return tiles;
        }

        if (me.IsBlind && enemyId is { } id && state.NoisyEnemies.Contains(id))
        {
            return tiles;
        }

        return [.. tiles.Where(t => SeesTile(state, me, t))];
    }

    /// <summary>
    /// ¿Percibe lo que hay en esa casilla? La visión del jugador (su radio, 4 para la Brasa y 1 para La Escucha) y,
    /// según el bando, el Faro de una Brasa y el Grito de La Escucha. A ella se le suma lo que oyó. La usan la vista, los
    /// eventos y las pruebas de no filtración.
    /// </summary>
    public static bool IsVisible(GameState state, Hero me, Position position)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(me);
        return SeesTile(state, me, position) || (me.IsBlind && state.NoiseTiles.Contains(position));
    }

    /// <summary>¿Puede el jugador ver a este enemigo? La Escucha, solo a su alcance o si hizo ruido.</summary>
    public static bool CanSeeEnemy(GameState state, Hero me, Enemy enemy)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(me);
        ArgumentNullException.ThrowIfNull(enemy);
        return SeesTile(state, me, enemy.Position) || (me.IsBlind && state.NoisyEnemies.Contains(enemy.Id));
    }

    private static bool SeesTile(GameState state, Hero me, Position position)
    {
        if (me.DistanceTo(position) <= me.VisionRadius)
        {
            return true;
        }

        foreach (var hero in state.Heroes)
        {
            if (hero.Shouting && hero.IsSameFaction(me))
            {
                return true;
            }

            if (!me.IsRenegade && hero.BeaconActive && hero.Position.DistanceTo(position) <= PathRules.BeaconRadius)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// ¿Ve ese cuerpo de otro portador? Polizón dentro de un huésped no se ve nunca. Del propio bando siempre; del otro
    /// bando solo dentro de la visión. La Escucha, a cualquiera solo a su alcance o si hizo ruido (las Brasas no hacen
    /// ruido). Extintos, retirados y Renegados que aún no aparecieron no tienen posición.
    /// </summary>
    private static bool IsShown(GameState state, Hero me, Hero other, Position body)
    {
        if (!(other.IsOnBoard || other.IsEmber) || other.IsInHost)
        {
            return false;
        }

        if (me.IsBlind)
        {
            return SeesTile(state, me, body) || state.NoisyHeroes.Contains(other.Player);
        }

        return me.IsSameFaction(other) || IsVisible(state, me, body);
    }

    private static bool IsSpawnShown(GameState state, Hero me, PlayerId player, Position position) =>
        player == me.Player || (!me.IsBlind && state.FindHero(player) is { } other && me.IsSameFaction(other)) || IsVisible(state, me, position);
}
