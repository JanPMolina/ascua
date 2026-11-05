using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>Comandos de partida fuera de la resolución de ronda: draft de bendiciones, elección de camino y abandono.</summary>
public static class MatchRules
{
    /// <summary>Elige una de las opciones pendientes del draft (inicial o de altar).</summary>
    public static CommandResult ChooseBlessing(GameState state, PlayerId player, int optionIndex)
    {
        ArgumentNullException.ThrowIfNull(state);
        var hero = state.FindHero(player);
        if (hero is null)
        {
            return CommandResult.Fail(state, ErrorCode.NotAParticipant, "El jugador no participa en esta partida.");
        }

        if (state.Phase == MatchPhase.Finished)
        {
            return CommandResult.Fail(state, ErrorCode.MatchNotInProgress, "La partida terminó.");
        }

        if (optionIndex < 0 || optionIndex >= hero.DraftOptions.Count)
        {
            return CommandResult.Fail(state, ErrorCode.InvalidDraftChoice, "No hay esa opción de draft pendiente.");
        }

        var log = new EventLog(state.LastSeq, state.Round);
        var blessing = hero.DraftOptions[optionIndex];
        var updated = BlessingRules.Grant(hero, blessing) with { DraftOptions = [] };
        log.Emit(new BlessingChosen(player, blessing) { Audience = Audience.Only(player) });
        log.Emit(new BlessingCountChanged(player, updated.Blessings.Count));
        if (updated.Embers != hero.Embers)
        {
            log.Emit(new EmbersChanged(player, updated.Embers - hero.Embers, EmberReason.Blessing, updated.Embers) { Audience = Audience.Only(player) });
        }

        var heroes = state.Heroes.SetItem(state.Heroes.FindIndex(h => h.Player == player), updated);
        var next = StartIfDrafted(state with { Heroes = heroes }, log);
        return CommandResult.Ok(next with { LastSeq = log.LastSeq }, log.Events);
    }

    /// <summary>Elige una de las 2 misiones ofrecidas (privado). Se elige junto con la mejora inicial.</summary>
    public static CommandResult ChoosePact(GameState state, PlayerId player, int optionIndex)
    {
        ArgumentNullException.ThrowIfNull(state);
        var hero = state.FindHero(player);
        if (hero is null)
        {
            return CommandResult.Fail(state, ErrorCode.NotAParticipant, "El jugador no participa en esta partida.");
        }

        if (state.Phase == MatchPhase.Finished)
        {
            return CommandResult.Fail(state, ErrorCode.MatchNotInProgress, "La partida terminó.");
        }

        if (optionIndex < 0 || optionIndex >= hero.PactOptions.Count)
        {
            return CommandResult.Fail(state, ErrorCode.NoPactOffer, "No hay esa misión pendiente.");
        }

        var log = new EventLog(state.LastSeq, state.Round);
        var offer = hero.PactOptions[optionIndex];
        var updated = hero with { Pact = offer.Kind, PactTarget = offer.Target, PactOptions = [] };
        log.Emit(new PactChosen(player, offer.Kind) { Audience = Audience.Only(player) });
        var heroes = state.Heroes.SetItem(state.Heroes.FindIndex(h => h.Player == player), updated);
        var next = StartIfDrafted(state with { Heroes = heroes }, log);
        return CommandResult.Ok(next with { LastSeq = log.LastSeq }, log.Events);
    }

    /// <summary>La partida empieza cuando todos eligieron mejora y misión.</summary>
    private static GameState StartIfDrafted(GameState state, EventLog log)
    {
        if (state.Phase != MatchPhase.Drafting || !state.Heroes.All(h => h.DraftOptions.IsEmpty && h.PactOptions.IsEmpty))
        {
            return state;
        }

        log.Emit(new MatchStarted());
        return state with { Phase = MatchPhase.InProgress };
    }

    /// <summary>Elige la primera opción (mejora y misión) para quien no decidió a tiempo (vence el plazo del draft).</summary>
    public static CommandResult AutoChooseBlessings(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var events = new List<DomainEvent>();
        foreach (var hero in state.Heroes.Where(h => !h.DraftOptions.IsEmpty).ToList())
        {
            var result = ChooseBlessing(state, hero.Player, 0);
            state = result.State;
            events.AddRange(result.Events);
        }

        foreach (var hero in state.Heroes.Where(h => !h.PactOptions.IsEmpty).ToList())
        {
            var result = ChoosePact(state, hero.Player, 0);
            state = result.State;
            events.AddRange(result.Events);
        }

        return CommandResult.Ok(state, events);
    }

    /// <summary>Voto secreto del interludio: a quién acusa (o a nadie) y qué puerta prefiere.</summary>
    public static CommandResult CastInterludeVote(GameState state, PlayerId player, PlayerId? accused, int? door) =>
        Interludes.CastVote(state, player, accused, door);

    /// <summary>Cierra el interludio y pasa a la sala siguiente. <paramref name="absent"/>: desconectados (no se les espera).</summary>
    public static CommandResult CloseInterlude(GameState state, IReadOnlySet<PlayerId> absent, Domain.Abstractions.IRandom rng) =>
        Interludes.Close(state, absent, rng);

    /// <summary>
    /// Elige camino tras extinguirse (planteamiento/04-caminos-tras-morir.md). La elección es privada: se guarda
    /// en el héroe y se revela al resolver la ronda. Vale la primera; no se puede cambiar dentro de la misma oferta.
    /// </summary>
    public static CommandResult ChoosePath(GameState state, PlayerId player, PathKind path) => ChoosePath(state, player, path, auto: false);

    /// <summary>Venció el plazo de 15 s: quien no eligió toma el Camino de la Amistad.</summary>
    public static CommandResult AutoChoosePaths(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var events = new List<DomainEvent>();
        foreach (var hero in state.Heroes.Where(h => h.IsChoosingPath).ToList())
        {
            var result = ChoosePath(state, hero.Player, PathKind.Friendship, auto: true);
            state = result.State;
            events.AddRange(result.Events);
        }

        return CommandResult.Ok(state, events);
    }

    /// <summary>Venció el plazo reducido del Repique: ya no se aceptan órdenes de los demás en esta ronda.</summary>
    public static CommandResult CloseBell(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Phase != MatchPhase.InProgress || !state.IsBellRound || state.BellClosed)
        {
            return CommandResult.Ok(state, []);
        }

        var log = new EventLog(state.LastSeq, state.Round);
        log.Emit(new BellExpired(state.BellRinger!.Value));
        return CommandResult.Ok(state with { BellClosed = true, LastSeq = log.LastSeq }, log.Events);
    }

    /// <summary>
    /// Venció la gracia de desconexión: el héroe se retira y pierde todas sus ascuas.
    /// Si no queda ningún humano activo, la partida termina como abandonada.
    /// </summary>
    public static CommandResult Retire(GameState state, PlayerId player)
    {
        ArgumentNullException.ThrowIfNull(state);
        var hero = state.FindHero(player);
        if (hero is null)
        {
            return CommandResult.Fail(state, ErrorCode.NotAParticipant, "El jugador no participa en esta partida.");
        }

        if (state.Phase == MatchPhase.Finished)
        {
            return CommandResult.Fail(state, ErrorCode.MatchNotInProgress, "La partida terminó.");
        }

        if (hero.Status == HeroStatus.Retired)
        {
            return CommandResult.Fail(state, ErrorCode.PlayerOut, "El héroe ya no está en la partida.");
        }

        var log = new EventLog(state.LastSeq, state.Round);
        var retired = hero with
        {
            Status = HeroStatus.Retired,
            Embers = 0,
            SavedEmbers = 0,
            Echoes = 0,
            Hp = 0,
            DraftOptions = [],
            PactOptions = [],
            PathOfferPending = false,
            PathChoice = null,
            AwaitingSpawn = false,
            BeaconActive = false,
            HostEnemyId = null,
            SecondBody = null,
            EchoQueue = [],
            History = [],
            Shouting = false,
            Debt = 0,
            DebtTo = null,
        };
        var heroes = state.Heroes.SetItem(state.Heroes.FindIndex(h => h.Player == player), retired);
        log.Emit(new HeroRetired(player));

        // Si se retira la Usurera, nadie le debe nada.
        foreach (var debtor in heroes.Where(h => h.DebtTo == player).ToList())
        {
            log.Emit(new DebtChanged(debtor.Player, player, -debtor.Debt, 0, DebtReason.Cancelled) { Audience = Audience.Only(debtor.Player) });
            heroes = heroes.Replace(debtor, debtor with { Debt = 0, DebtTo = null });
        }

        var next = state with { Heroes = heroes };
        if (next.Interlude is { } interlude)
        {
            next = next with { Interlude = interlude with { Ballots = interlude.Ballots.RemoveAll(b => b.Voter == player) } };
        }

        if (next.BellRinger == player)
        {
            next = next with { BellRound = 0, BellRinger = null, BellClosed = false };
        }

        if (!heroes.Any(h => h.IsOnBoard))
        {
            var outcome = new GameOutcome(OutcomeKind.Abandoned, DefeatReason.None, [], Outcomes.Scores(heroes))
            {
                Betrayals = Outcomes.Summarize(next.Betrayals),
                Awards = Awards.Compute(heroes),
            };
            next = next with { Phase = MatchPhase.Finished, Outcome = outcome, Interlude = null };
            log.Emit(new MatchEnded(outcome));
        }
        else
        {
            next = StartIfDrafted(next, log);
        }

        return CommandResult.Ok(next with { LastSeq = log.LastSeq }, log.Events);
    }

    private static CommandResult ChoosePath(GameState state, PlayerId player, PathKind path, bool auto)
    {
        ArgumentNullException.ThrowIfNull(state);
        var hero = state.FindHero(player);
        if (hero is null)
        {
            return CommandResult.Fail(state, ErrorCode.NotAParticipant, "El jugador no participa en esta partida.");
        }

        if (state.Phase is not (MatchPhase.InProgress or MatchPhase.Interlude))
        {
            return CommandResult.Fail(state, ErrorCode.MatchNotInProgress, "La partida no está en curso.");
        }

        if (!Enum.IsDefined(path))
        {
            return CommandResult.Fail(state, ErrorCode.InvalidPathChoice, "Ese camino no existe.");
        }

        if (!hero.IsChoosingPath)
        {
            return CommandResult.Fail(state, ErrorCode.NoPathOffer, "No hay una elección de camino abierta.");
        }

        var log = new EventLog(state.LastSeq, state.Round);
        log.Emit(new PathSelected(player, path, auto) { Audience = Audience.Only(player) });
        var heroes = state.Heroes.SetItem(state.Heroes.FindIndex(h => h.Player == player), hero with { PathChoice = path });
        return CommandResult.Ok(state with { Heroes = heroes, LastSeq = log.LastSeq }, log.Events);
    }
}
