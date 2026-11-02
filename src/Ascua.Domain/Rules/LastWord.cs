using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>
/// Pasiva del Campanero, Último en hablar: si todos los demás ya decidieron y él todavía no, recibe en privado las
/// órdenes de todos y puede responder con lo que quede de reloj. Quien está desconectado no cuenta para «todos
/// decidieron» (recibirá GUARDAR). Como Renegado solo ve los tipos. Su orden se resuelve igual con las demás.
/// </summary>
public static class LastWord
{
    /// <summary>El Campanero que podría oír las órdenes en la ronda abierta (como Brasa no tiene su pasiva).</summary>
    public static Hero? Listener(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Heroes.Find(h => h is { Class: HeroClass.Campanero, IsEmber: false } && h.CanAct);
    }

    /// <summary>
    /// Órdenes que oye el Campanero, o <c>null</c> si la pasiva todavía no aplica. Con el plazo del Repique vencido,
    /// quien no decidió ya tiene GUARDAR. Fuera de eso, sin ninguna orden real no hay nada que oír.
    /// </summary>
    public static IReadOnlyList<(PlayerId Player, Order Order)>? Heard(
        GameState state, IReadOnlyDictionary<PlayerId, Order> decided, IReadOnlySet<PlayerId> connected)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(decided);
        ArgumentNullException.ThrowIfNull(connected);
        var listener = Listener(state);
        if (listener is null || decided.ContainsKey(listener.Player) || !connected.Contains(listener.Player))
        {
            return null;
        }

        var others = state.Heroes.Where(h => h.CanAct && h.Player != listener.Player).ToList();
        var closed = state.IsBellRound && state.BellClosed;
        if (others.Count == 0 || (!closed && !others.Exists(h => decided.ContainsKey(h.Player))))
        {
            return null;
        }

        if (!others.TrueForAll(h => decided.ContainsKey(h.Player) || closed || !connected.Contains(h.Player)))
        {
            return null;
        }

        return [.. others.Select(h => (h.Player, decided.GetValueOrDefault(h.Player) ?? Order.DefaultGuard))];
    }
}
