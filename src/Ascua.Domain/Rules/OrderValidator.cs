using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

public static class OrderValidator
{
    /// <summary>
    /// Valida una orden para la ronda <paramref name="round"/>. Devuelve <c>null</c> si es aceptable
    /// o un error tipado; nunca lanza excepciones por culpa del cliente.
    /// </summary>
    public static DomainError? Validate(GameState state, PlayerId player, int round, Order? order)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Phase != MatchPhase.InProgress)
        {
            return new DomainError(ErrorCode.MatchNotInProgress, "La partida no está en curso.");
        }

        var hero = state.FindHero(player);
        if (hero is null)
        {
            return new DomainError(ErrorCode.NotAParticipant, "El jugador no participa en esta partida.");
        }

        if (round != state.Round)
        {
            return new DomainError(ErrorCode.StaleRound, $"La ronda abierta es la {state.Round}.");
        }

        if (hero.Status == HeroStatus.Down)
        {
            return new DomainError(ErrorCode.PlayerDown, "El héroe está derribado y no puede actuar.");
        }

        if (!hero.CanAct)
        {
            return new DomainError(ErrorCode.PlayerOut, "El héroe ya no está en la partida.");
        }

        if (order is null
            || !Enum.IsDefined(order.Type)
            || (order.Direction is { } direction && !Enum.IsDefined(direction))
            || !order.IsWellFormed())
        {
            return new DomainError(ErrorCode.InvalidOrder, "La orden está mal formada.");
        }

        if (order.Step is { } step && !Enum.IsDefined(step))
        {
            return new DomainError(ErrorCode.InvalidOrder, "El paso está mal formado.");
        }

        if (order.Type == OrderType.Sabotage && !hero.CanSabotage(state.RoomIndex))
        {
            return new DomainError(ErrorCode.InvalidOrder, "No puedes sabotear ahora: una vez por sala y solo como héroe activo fuera de un huésped.");
        }

        if (order.Type == OrderType.Push && !hero.CanPush)
        {
            return new DomainError(ErrorCode.InvalidOrder, "No puedes empujar ahora.");
        }

        if (order.Type == OrderType.Push && hero.PushCooldown > 0)
        {
            return new DomainError(ErrorCode.SkillOnCooldown, $"Podrás empujar en {hero.PushCooldown} rondas.");
        }

        if (order.Type == OrderType.Skill && hero.SkillCooldown > 0)
        {
            return new DomainError(ErrorCode.SkillOnCooldown, $"La habilidad estará lista en {hero.SkillCooldown} rondas.");
        }

        if (hero.IsEmber && order.Type == OrderType.Attack && hero.HinderCooldown > 0)
        {
            return new DomainError(ErrorCode.SkillOnCooldown, $"Estorbo estará listo en {hero.HinderCooldown} rondas.");
        }

        return null;
    }
}
