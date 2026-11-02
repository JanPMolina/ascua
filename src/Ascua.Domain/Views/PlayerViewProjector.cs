using System.Collections.Immutable;
using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Views;

/// <summary>
/// Filtra la información oculta (sección 8 de las reglas): niebla de radio 3 (Manhattan),
/// sigilo, pactos, bendiciones, drafts y contenido de cofres ajenos. Con los caminos
/// (planteamiento/04-caminos-tras-morir.md): la elección, los ecos y las ascuas guardadas solo los ve
/// su dueño; la Brasa es visible para el grupo; Renegados y héroes se ven entre sí solo dentro de la
/// visión; el Faro de una Brasa suma visión al grupo.
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
                var shown = IsShown(state, me, h);
                return new AllyView(
                    h.Player,
                    h.Seat,
                    h.Class,
                    shown ? h.Position : null,
                    shown ? h.Facing : null,
                    h.Hp,
                    h.Status,
                    h.Embers,
                    h.Blessings.Count);
            })
            .ToImmutableList();

        var tiles = Room.AllPositions().Where(CanSee).Select(p => new TileView(p, room.TerrainAt(p))).ToImmutableList();

        var enemies = state.Enemies
            .Where(e => CanSee(e.Position))
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
            .Select(e => new TelegraphView(CanSee(e.Position) ? e.Id : null, VisibleTelegraph(state, me, e.Telegraph)))
            .Where(t => !t.Tiles.IsEmpty)
            .ToImmutableList();

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
            me.Charges,
            me.StealthRounds,
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
                me.AwaitingSpawn && me.SpawnRound > 0 ? me.SpawnRound : null));

        var outcome = state.Outcome is { } o ? new OutcomeView(o.Kind, o.Reason, o.Winners, o.Scores) : null;

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
            outcome);
    }

    /// <summary>
    /// Eventos que el jugador puede recibir, ya recortados: anuncios solo con las casillas que ve
    /// y sin delatar al autor de un golpe que está en sigilo.
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
        if (domainEvent is AttackTelegraphed telegraphed)
        {
            var tiles = VisibleTelegraph(state, me, telegraphed.Tiles);
            var source = audience.Position is { } origin && IsVisible(state, me, origin) ? telegraphed.EnemyId : null;
            return tiles.IsEmpty ? null : telegraphed with { EnemyId = source, Tiles = tiles };
        }

        var allowed = audience.Kind switch
        {
            AudienceKind.Everyone => true,
            AudienceKind.Player => audience.Player == me.Player,
            AudienceKind.Location => audience.Position is { } p && IsVisible(state, me, p),
            AudienceKind.Group => !me.IsRenegade || (audience.Position is { } g && IsVisible(state, me, g)),
            _ => false,
        };
        if (!allowed)
        {
            return null;
        }

        return domainEvent switch
        {
            EntityDamaged damaged when HidesSource(state, me, damaged.Source) => damaged with { Source = null },
            HitPrevented prevented when HidesSource(state, me, prevented.Source) => prevented with { Source = null },
            RenegadeSpawned spawned when spawned.Position is { } at && !IsShownAt(state, me, spawned.Player, at) => spawned with { Position = null },
            _ => domainEvent,
        };
    }

    private static bool HidesSource(GameState state, Hero me, EntityRef? source) =>
        source?.Hero is { } author && author != me.Player && state.FindHero(author)?.IsStealthed == true;

    private static ImmutableList<Position> VisibleTelegraph(GameState state, Hero me, ImmutableList<Position> tiles) =>
        me.Has(Blessing.OwlEye) ? tiles : [.. tiles.Where(t => IsVisible(state, me, t))];

    /// <summary>
    /// Visión del jugador: su radio (4 para la Brasa) y, si es del grupo, el radio 2 de cada Faro encendido.
    /// La usan la vista, los eventos y las pruebas de no filtración.
    /// </summary>
    public static bool IsVisible(GameState state, Hero me, Position position)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(me);
        if (me.Position.DistanceTo(position) <= me.VisionRadius)
        {
            return true;
        }

        return !me.IsRenegade && state.Heroes.Any(h => h.BeaconActive && h.Position.DistanceTo(position) <= PathRules.BeaconRadius);
    }

    /// <summary>
    /// ¿Ve la posición de otro portador? Del propio bando siempre (salvo sigilo); del otro bando solo dentro de
    /// la visión. Extintos, retirados y Renegados que aún no aparecieron no tienen posición.
    /// </summary>
    private static bool IsShown(GameState state, Hero me, Hero other)
    {
        if (!(other.IsOnBoard || other.IsEmber) || other.IsStealthed)
        {
            return false;
        }

        return me.IsSameFaction(other) || IsVisible(state, me, other.Position);
    }

    private static bool IsShownAt(GameState state, Hero me, PlayerId player, Position position) =>
        player == me.Player || (state.FindHero(player) is { } other && me.IsSameFaction(other)) || IsVisible(state, me, position);
}
