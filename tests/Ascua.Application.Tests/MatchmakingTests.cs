using Ascua.Application.Matches;
using Ascua.Application.Matchmaking;
using Ascua.Application.Rooms;
using Ascua.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ascua.Application.Tests;

public sealed class HeroAssignmentTests
{
    [Fact]
    public void Earlier_players_keep_their_preferred_hero_and_the_rest_get_free_ones()
    {
        var heroes = HeroAssignment.Assign([HeroClass.Raven, HeroClass.Raven, null, HeroClass.Anvil]);

        Assert.Equal([HeroClass.Raven, HeroClass.Spark, HeroClass.Root, HeroClass.Anvil], heroes);
        Assert.Equal(heroes.Count, heroes.Distinct().Count());
    }

    [Fact]
    public void More_players_than_heroes_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => HeroAssignment.Assign([null, null, null, null, null]));
    }
}

public sealed class MatchmakingProcessorTests
{
    private readonly FakeQueue _queue = new();
    private readonly FakeMatchLauncher _launcher = new();

    [Fact]
    public async Task Formed_group_is_launched_with_distinct_heroes_and_completed()
    {
        _queue.Groups.Enqueue(Group(HeroClass.Spark, HeroClass.Spark, null));

        var launched = await Processor().RunOnceAsync(default);

        Assert.Equal(1, launched);
        var request = Assert.Single(_launcher.Requests);
        Assert.Equal([HeroClass.Spark, HeroClass.Anvil, HeroClass.Raven], request.Seats.Select(s => s.Hero));
        Assert.Equal(request.RoomId, Assert.Single(_queue.Completed));
        Assert.Empty(_queue.Aborted);
    }

    [Fact]
    public async Task Unavailable_launcher_returns_the_group_to_the_queue_and_stops_the_pass()
    {
        _queue.Groups.Enqueue(Group(null, null));
        _queue.Groups.Enqueue(Group(null, null));
        _launcher.Failure = new MatchLaunchUnavailableException();

        var launched = await Processor().RunOnceAsync(default);

        Assert.Equal(0, launched);
        Assert.Single(_queue.Aborted);
        Assert.Single(_queue.Groups);
        Assert.Empty(_queue.Completed);
    }

    [Fact]
    public async Task Orphan_groups_are_retried_with_the_same_id()
    {
        var orphan = Group(null, null);
        _queue.Stale.Add(orphan);

        await Processor().RunOnceAsync(default);

        Assert.Equal(orphan.GroupId, Assert.Single(_launcher.Requests).RoomId);
        Assert.Equal(orphan.GroupId, Assert.Single(_queue.Completed));
    }

    private MatchmakingProcessor Processor() => new(
        _queue,
        _launcher,
        new MatchmakingPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1)),
        new LobbyPolicy(TimeSpan.FromHours(1), TimeSpan.FromHours(4)),
        new FixedClock(),
        NullLogger<MatchmakingProcessor>.Instance);

    private static MatchGroup Group(params HeroClass?[] preferences) => new(
        Guid.NewGuid(),
        [.. preferences.Select(h => new QueueTicket(Guid.NewGuid(), "u", h, DateTimeOffset.UnixEpoch))]);

    private sealed class FakeQueue : IMatchmakingQueue
    {
        public Queue<MatchGroup> Groups { get; } = new();

        public List<MatchGroup> Stale { get; } = [];

        public List<Guid> Completed { get; } = [];

        public List<Guid> Aborted { get; } = [];

        public Task<LobbyError?> EnqueueAsync(QueueTicket ticket, CancellationToken cancellationToken) => Task.FromResult<LobbyError?>(null);

        public Task<LobbyError?> LeaveAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<LobbyError?>(null);

        public Task<QueueStatus> GetStatusAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(new QueueStatus(QueueState.Idle, null, null, 0, null, null));

        public Task<MatchGroup?> TryFormGroupAsync(DateTimeOffset now, TimeSpan maxWait, Guid groupId, CancellationToken cancellationToken) =>
            Task.FromResult(Groups.TryDequeue(out var group) ? group : null);

        public Task<IReadOnlyList<MatchGroup>> GetStalePendingGroupsAsync(DateTimeOffset formedBefore, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MatchGroup>>([.. Stale]);

        public Task CompleteGroupAsync(Guid groupId, Guid matchId, TimeSpan activeMatchTtl, CancellationToken cancellationToken)
        {
            Completed.Add(groupId);
            return Task.CompletedTask;
        }

        public Task AbortGroupAsync(Guid groupId, CancellationToken cancellationToken)
        {
            Aborted.Add(groupId);
            return Task.CompletedTask;
        }

        public Task<int> ExpireAsync(DateTimeOffset enqueuedBefore, CancellationToken cancellationToken) => Task.FromResult(0);
    }
}
