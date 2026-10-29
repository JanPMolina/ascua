using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>Comandos de partida fuera de la resolución de ronda: draft de bendiciones y abandono.</summary>
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

        var heroes = state.Heroes.SetItem(state.Heroes.FindIndex(h => h.Player == player), updated);
        var next = state with { Heroes = heroes };
        if (next.Phase == MatchPhase.Drafting && heroes.All(h => h.DraftOptions.IsEmpty))
        {
            next = next with { Phase = MatchPhase.InProgress };
            log.Emit(new MatchStarted());
        }

        return CommandResult.Ok(next with { LastSeq = log.LastSeq }, log.Events);
    }

    /// <summary>Elige la primera opción para quien no decidió a tiempo (vence el plazo del draft).</summary>
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

        return CommandResult.Ok(state, events);
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

        if (!hero.IsOnBoard)
        {
            return CommandResult.Fail(state, ErrorCode.PlayerOut, "El héroe ya no está en la partida.");
        }

        var log = new EventLog(state.LastSeq, state.Round);
        var retired = hero with { Status = HeroStatus.Retired, Embers = 0, Hp = 0, DraftOptions = [] };
        var heroes = state.Heroes.SetItem(state.Heroes.FindIndex(h => h.Player == player), retired);
        log.Emit(new HeroRetired(player));
        var next = state with { Heroes = heroes };

        if (!heroes.Any(h => h.IsOnBoard))
        {
            var outcome = new GameOutcome(OutcomeKind.Abandoned, DefeatReason.None, [], Outcomes.Scores(heroes));
            next = next with { Phase = MatchPhase.Finished, Outcome = outcome };
            log.Emit(new MatchEnded(outcome));
        }
        else if (next.Phase == MatchPhase.Drafting && heroes.All(h => h.DraftOptions.IsEmpty))
        {
            next = next with { Phase = MatchPhase.InProgress };
            log.Emit(new MatchStarted());
        }

        return CommandResult.Ok(next with { LastSeq = log.LastSeq }, log.Events);
    }
}
