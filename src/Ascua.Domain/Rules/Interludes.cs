using System.Collections.Immutable;
using Ascua.Domain.Abstractions;
using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>
/// El interludio entre salas (06-version-3.md §3 y §4): El Juicio por voto secreto y la votación de puertas. Los votos
/// viven en <see cref="GameState.Interlude"/> y nunca salen de ahí; al cerrar solo se publica el recuento.
/// </summary>
public static class Interludes
{
    /// <summary>Votan héroes activos, derribados y Brasas. No votan Renegados, extintos ni retirados.</summary>
    public static IEnumerable<Hero> Voters(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Heroes.Where(h => h.Status is HeroStatus.Active or HeroStatus.Down or HeroStatus.EmberSpirit);
    }

    /// <summary>A quién puede acusar un votante: cualquier otro jugador no retirado.</summary>
    public static IEnumerable<Hero> Candidates(GameState state, PlayerId voter)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Heroes.Where(h => h.Player != voter && h.Status != HeroStatus.Retired);
    }

    internal static void Open(RoundContext ctx)
    {
        var ended = ctx.RoomIndex;
        var next = ended + 1;
        ImmutableList<RoomType> doors = [];
        if (next < ctx.Rooms.Count - 1)
        {
            var available = RoomTypes.Doors.Where(t => t != ctx.Room.Type).ToList();
            var picked = ImmutableList.CreateBuilder<RoomType>();
            for (var i = 0; i < 2; i++)
            {
                var index = ctx.Rng.Next(available.Count);
                picked.Add(available[index]);
                available.RemoveAt(index);
            }

            doors = picked.ToImmutable();
        }

        ctx.OpeningInterlude = new InterludeState(ended, doors);
        ctx.Emit(new InterludeOpened(ended, doors));
    }

    public static CommandResult CastVote(GameState state, PlayerId player, PlayerId? accused, int? door)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.FindHero(player) is null)
        {
            return CommandResult.Fail(state, ErrorCode.NotAParticipant, "El jugador no participa en esta partida.");
        }

        if (state is not { Phase: MatchPhase.Interlude, Interlude: { } interlude })
        {
            return CommandResult.Fail(state, ErrorCode.VoteClosed, "No hay un interludio abierto.");
        }

        if (!Voters(state).Any(h => h.Player == player))
        {
            return CommandResult.Fail(state, ErrorCode.InvalidVote, "En este interludio no votas.");
        }

        if (interlude.Ballots.Exists(b => b.Voter == player))
        {
            return CommandResult.Fail(state, ErrorCode.VoteClosed, "Ya votaste.");
        }

        if (accused is { } target && !Candidates(state, player).Any(h => h.Player == target))
        {
            return CommandResult.Fail(state, ErrorCode.InvalidVote, "No puedes acusar a ese jugador.");
        }

        if (door is { } chosen && (chosen < 0 || chosen >= interlude.Doors.Count))
        {
            return CommandResult.Fail(state, ErrorCode.InvalidVote, "Esa puerta no existe.");
        }

        var log = new EventLog(state.LastSeq, state.Round);
        log.Emit(new VoteCast(player, accused, door) { Audience = Audience.Only(player) });
        var next = state with
        {
            Interlude = interlude with { Ballots = interlude.Ballots.Add(new InterludeBallot(player, accused, door)) },
            LastSeq = log.LastSeq,
        };
        return CommandResult.Ok(next, log.Events);
    }

    /// <summary>
    /// Cierra: El Juicio, la puerta y la entrada a la sala siguiente. Quien no votó vota «nadie»; quien además está en
    /// <paramref name="absent"/> no cuenta como votante.
    /// </summary>
    public static CommandResult Close(GameState state, IReadOnlySet<PlayerId> absent, IRandom rng)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(absent);
        ArgumentNullException.ThrowIfNull(rng);
        if (state is not { Phase: MatchPhase.Interlude, Interlude: { } interlude })
        {
            return CommandResult.Fail(state, ErrorCode.VoteClosed, "No hay un interludio abierto.");
        }

        var ctx = new RoundContext(state, rng);
        var counted = Voters(state)
            .Where(v => !absent.Contains(v.Player) || interlude.Ballots.Exists(b => b.Voter == v.Player))
            .Select(v => v.Player)
            .ToList();
        var ballots = interlude.Ballots.Where(b => counted.Contains(b.Voter)).ToList();

        var accusations = Judge(ctx, interlude, counted, ballots);
        var noAccusation = counted.Count(v => !ballots.Exists(b => b.Voter == v && b.Accused is not null));
        var (doors, chosen) = PickDoor(interlude, ballots, rng);
        var verdict = new InterludeVerdict(interlude.EndedRoom, accusations, noAccusation, doors, chosen);
        ctx.Emit(new InterludeClosed(verdict));

        var nextIndex = interlude.EndedRoom + 1;
        if (chosen is { } type)
        {
            var enemyId = ctx.NextEnemyId;
            var chestId = Math.Max(ctx.NextChestId, ctx.Rooms.SelectMany(r => r.Chests).Select(c => c.Id + 1).DefaultIfEmpty(1).Max());
            ctx.Rooms[nextIndex] = MatchFactory.GenerateRoom(nextIndex, type, state.Heroes.Count, state.Curses, rng, ref enemyId, ref chestId);
            ctx.NextEnemyId = enemyId;
            ctx.NextChestId = chestId;
        }

        ctx.Rooms[nextIndex] = ctx.Rooms[nextIndex] with { EnteredRound = state.Round };
        Cleanup.EnterNextRoom(ctx);

        var next = Resolver.Freeze(ctx) with
        {
            Phase = MatchPhase.InProgress,
            Interlude = null,
            LastVerdict = verdict,
        };
        return CommandResult.Ok(next, ctx.Events);
    }

    private static ImmutableList<AccusationTally> Judge(RoundContext ctx, InterludeState interlude, List<PlayerId> counted, List<InterludeBallot> ballots)
    {
        var result = ImmutableList.CreateBuilder<AccusationTally>();
        var accused = ballots.Where(b => b.Accused is not null).Select(b => b.Accused!.Value).Distinct().OrderBy(p => ctx.Hero(p).Seat).ToList();
        foreach (var player in accused)
        {
            var accusers = ballots.Where(b => b.Accused == player).Select(b => b.Voter).ToList();
            var others = counted.Count(v => v != player);
            var convicted = others > 0 && accusers.Count * 2 > others;
            if (!convicted)
            {
                result.Add(new AccusationTally(player, accusers.Count, others, false, null, 0));
                continue;
            }

            var betrayals = ctx.Betrayals.Where(b => b.Traitor == player && b.Room == interlude.EndedRoom && !b.Judged).ToList();
            if (betrayals.Count > 0)
            {
                result.Add(Convict(ctx, player, accusers, others, betrayals));
            }
            else
            {
                foreach (var accuser in accusers)
                {
                    LoseGold(ctx, accuser, TrialRules.WrongAccusationPenalty, EmberReason.TrialFine);
                }

                result.Add(new AccusationTally(player, accusers.Count, others, true, false, 0) { AccuserFine = TrialRules.WrongAccusationPenalty });
            }
        }

        return result.ToImmutable();
    }

    private static AccusationTally Convict(RoundContext ctx, PlayerId player, List<PlayerId> accusers, int others, List<Betrayal> betrayals)
    {
        foreach (var betrayal in betrayals)
        {
            ctx.Betrayals[ctx.Betrayals.IndexOf(betrayal)] = betrayal with { Judged = true };
        }

        var hero = ctx.Hero(player);
        ctx.Update(hero with { Progress = hero.Progress with { Convictions = hero.Progress.Convictions + 1 } });
        var lost = LoseGold(ctx, player, TrialRules.GuiltyPenalty, EmberReason.TrialPenalty);
        foreach (var accuser in accusers)
        {
            var voter = ctx.Hero(accuser);
            ctx.Update(voter with { Progress = voter.Progress with { CorrectAccusations = voter.Progress.CorrectAccusations + 1 } });
        }

        return new AccusationTally(player, accusers.Count, others, true, true, lost) { Exposed = [.. Outcomes.Summarize(betrayals)] };
    }

    /// <summary>Pierde oro: primero el que tiene y luego el guardado. El Avaro nunca baja de 1 por el Juicio.</summary>
    private static int LoseGold(RoundContext ctx, PlayerId player, int amount, EmberReason reason)
    {
        var hero = ctx.Hero(player);
        var floor = hero.IsMiser ? TrialRules.MiserFloor : 0;
        var fromEmbers = Math.Min(amount, Math.Max(0, hero.Embers - floor));
        var fromSaved = Math.Min(amount - fromEmbers, Math.Max(0, hero.SavedEmbers - floor));
        var lost = fromEmbers + fromSaved;
        if (lost == 0)
        {
            return 0;
        }

        var embers = hero.Embers - fromEmbers;
        ctx.Update(hero with
        {
            Embers = embers,
            SavedEmbers = hero.SavedEmbers - fromSaved,
            Progress = hero.Progress with { TrialGoldLost = hero.Progress.TrialGoldLost + lost },
        });
        ctx.Emit(new EmbersChanged(player, -lost, reason, embers) { Audience = Audience.Only(player) });
        return lost;
    }

    /// <summary>Gana la mayoría; en empate o sin votos, al azar. Sin puertas (la siguiente es la del Celador), no hay nada que elegir.</summary>
    private static (ImmutableList<DoorTally> Doors, RoomType? Chosen) PickDoor(InterludeState interlude, List<InterludeBallot> ballots, IRandom rng)
    {
        if (interlude.Doors.IsEmpty)
        {
            return ([], null);
        }

        var tallies = interlude.Doors.Select((type, index) => new DoorTally(type, ballots.Count(b => b.Door == index))).ToImmutableList();
        var best = tallies.Max(t => t.Votes);
        var top = tallies.Where(t => t.Votes == best).ToList();
        return (tallies, top[top.Count == 1 ? 0 : rng.Next(top.Count)].Type);
    }
}

/// <summary>Títulos de la pantalla final (06-version-3.md §10): solo los ganados por alguien; los empates los comparten.</summary>
public static class Awards
{
    public static ImmutableList<Award> Compute(IReadOnlyCollection<Hero> heroes)
    {
        ArgumentNullException.ThrowIfNull(heroes);
        var awards = ImmutableList.CreateBuilder<Award>();
        void Add(AwardKind kind, Func<PactProgress, int> value)
        {
            var best = heroes.Max(h => value(h.Progress));
            if (best > 0)
            {
                awards.AddRange(heroes.Where(h => value(h.Progress) == best).OrderBy(h => h.Seat).Select(h => new Award(kind, h.Player, best)));
            }
        }

        Add(AwardKind.MostBetrayals, p => p.Betrayals);
        Add(AwardKind.MostWardenDamage, p => p.WardenDamage);
        Add(AwardKind.MostRevives, p => p.Revives);
        Add(AwardKind.MostTrialGoldLost, p => p.TrialGoldLost);
        Add(AwardKind.RichestDeath, p => p.GoldAtDeath);
        Add(AwardKind.MostKills, p => p.Kills);
        Add(AwardKind.MostChests, p => p.ChestsOpened);
        Add(AwardKind.MostPushes, p => p.Pushes);
        return awards.ToImmutable();
    }
}
