using Ascua.Api.Realtime;
using Ascua.Application.Matches;
using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Api.Endpoints;

/// <summary>
/// Arma la reproducción ronda a ronda desde las confirmaciones guardadas. En vista de jugador todo pasa
/// por <see cref="RealtimeMapper"/> (y por lo tanto por <c>PlayerViewProjector</c>): cada confirmación se
/// proyecta con su estado posterior, igual que en vivo, así que nunca se muestra lo que no se vio entonces.
/// </summary>
public static class MatchReplayMapper
{
    private static readonly ViewContext Offline = new(new HashSet<Guid>(), new Dictionary<PlayerId, Order>());

    public static MatchReplayResponse Map(MatchReplay replay, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(replay);
        var rounds = replay.Steps
            .GroupBy(step => step.Round)
            .OrderBy(group => group.Key)
            .Select(group => replay.Viewer is { } viewer ? PlayerRound(replay.Match, viewer, [.. group], now) : FullRound([.. group]))
            .ToList();

        return new MatchReplayResponse(
            replay.Match.MatchId,
            replay.Viewer is null ? "FULL" : "PLAYER",
            replay.Viewer?.PlayerId.Value,
            replay.Match.Status.ToCode(),
            replay.FromRound,
            replay.ToRound,
            rounds);
    }

    private static ReplayRound PlayerRound(MatchSnapshot match, MatchParticipant viewer, IReadOnlyList<MatchStep> steps, DateTimeOffset now)
    {
        var last = steps[^1];
        var frame = match with { State = last.State, Status = MatchStatusExtensions.From(last.State), Deadline = null };
        var events = steps.SelectMany(step => RealtimeMapper.VisibleEvents(step.State, step.Events, viewer.PlayerId)).ToList();
        return new ReplayRound(last.Round, last.State.LastSeq, RealtimeMapper.Snapshot(frame, viewer, Offline, now).View, null, events);
    }

    private static ReplayRound FullRound(IReadOnlyList<MatchStep> steps)
    {
        var last = steps[^1];
        var events = steps.SelectMany(step => step.Events).Select(FullEvent).ToList();
        return new ReplayRound(last.Round, last.State.LastSeq, null, FullState(last.State), events);
    }

    private static IDictionary<string, object> FullEvent(DomainEvent domainEvent)
    {
        var dto = RealtimeMapper.VisibleEvent(domainEvent) ?? RealtimeMapper.Event(domainEvent.GetType().Name);
        dto["seq"] = domainEvent.Seq;
        dto["domainType"] = domainEvent.GetType().Name;
        return dto;
    }

    private static FullStateDto FullState(GameState state)
    {
        var room = state.CurrentRoom;
        return new FullStateDto(
            state.Round,
            state.Phase.ToString().ToUpperInvariant(),
            state.RoomIndex + 1,
            [.. state.Heroes.Select(h => new FullHeroDto(
                h.Player.Value,
                RealtimeMapper.Hero(h.Class),
                RealtimeMapper.HeroState(h.Status),
                h.Hp,
                h.Shield,
                RealtimeMapper.Position(h.Position),
                RealtimeMapper.Direction(h.Facing),
                h.Embers,
                [.. h.Blessings.Select(RealtimeMapper.BlessingId)],
                [.. h.DraftOptions.Select(RealtimeMapper.BlessingId)],
                RealtimeMapper.PactId(h.Pact),
                h.SecondBody is { } second ? RealtimeMapper.Position(second) : null,
                h.HostEnemyId,
                h.Debt,
                h.DebtTo?.Value))],
            [.. state.Enemies.Select(e => new FullEnemyDto(
                e.Id,
                RealtimeMapper.EnemyKind(e.Kind),
                e.Hp,
                e.MaxHp,
                RealtimeMapper.Position(e.Position),
                RealtimeMapper.Direction(e.Facing),
                [.. e.Telegraph.Select(RealtimeMapper.Position)]))],
            [.. room.Chests.Select(c => new FullChestDto(
                c.Id,
                RealtimeMapper.Position(c.Position),
                c.Opened,
                c.OpenedBy?.Value,
                c.Loot.ToString(),
                c.HasSilverKey))]);
    }
}
