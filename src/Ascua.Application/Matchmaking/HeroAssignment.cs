using Ascua.Domain.Model;

namespace Ascua.Application.Matchmaking;

/// <summary>
/// Reparte héroes sin repetir (igual que en las salas): por orden de llegada, cada jugador recibe su
/// preferido si sigue libre; si no, o si no tiene preferencia, el primer héroe libre.
/// </summary>
public static class HeroAssignment
{
    public static IReadOnlyList<HeroClass> Assign(IReadOnlyList<HeroClass?> preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var all = Enum.GetValues<HeroClass>();
        if (preferences.Count > all.Length)
        {
            throw new ArgumentException($"Hay más jugadores ({preferences.Count}) que héroes ({all.Length}).", nameof(preferences));
        }

        var assigned = new HeroClass?[preferences.Count];
        var taken = new HashSet<HeroClass>();
        for (var i = 0; i < preferences.Count; i++)
        {
            if (preferences[i] is { } preferred && taken.Add(preferred))
            {
                assigned[i] = preferred;
            }
        }

        for (var i = 0; i < preferences.Count; i++)
        {
            if (assigned[i] is null)
            {
                var free = all.First(h => !taken.Contains(h));
                taken.Add(free);
                assigned[i] = free;
            }
        }

        return [.. assigned.Select(h => h!.Value)];
    }
}
