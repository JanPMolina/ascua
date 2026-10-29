using System.Collections.Immutable;
using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Views;

/// <summary>
/// Filtra la información oculta (sección 8 de las reglas): niebla de radio 3 (Manhattan),
/// sigilo, pactos, bendiciones, drafts y contenido de cofres ajenos.
/// </summary>
public static class PlayerViewProjector
{
    public static PlayerView For(GameState state, PlayerId viewer)
    {
        ArgumentNullException.ThrowIfNull(state);
        var me = state.FindHero(viewer) ?? throw new ArgumentException("El jugador no participa en esta partida.", nameof(viewer));
        var room = state.CurrentRoom;
        bool CanSee(Position p) => IsVisible(me, p);

        var allies = state.Heroes
            .Where(h => h.Player != viewer)
            .Select(h =>
            {
                var shown = h.IsOnBoard && !h.IsStealthed;
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
            .Select(e => new TelegraphView(CanSee(e.Position) ? e.Id : null, VisibleTelegraph(me, e.Telegraph)))
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
            me.Pact);

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
            var tiles = VisibleTelegraph(me, telegraphed.Tiles);
            var source = audience.Position is { } origin && IsVisible(me, origin) ? telegraphed.EnemyId : null;
            return tiles.IsEmpty ? null : telegraphed with { EnemyId = source, Tiles = tiles };
        }

        var allowed = audience.Kind switch
        {
            AudienceKind.Everyone => true,
            AudienceKind.Player => audience.Player == me.Player,
            AudienceKind.Location => audience.Position is { } p && IsVisible(me, p),
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
            _ => domainEvent,
        };
    }

    private static bool HidesSource(GameState state, Hero me, EntityRef? source) =>
        source?.Hero is { } author && author != me.Player && state.FindHero(author)?.IsStealthed == true;

    private static ImmutableList<Position> VisibleTelegraph(Hero me, ImmutableList<Position> tiles) =>
        me.Has(Blessing.OwlEye) ? tiles : [.. tiles.Where(t => IsVisible(me, t))];

    private static bool IsVisible(Hero me, Position position) =>
        me.Position.DistanceTo(position) <= me.VisionRadius;
}
