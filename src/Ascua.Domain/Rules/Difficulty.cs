using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>
/// Dificultad según la cantidad de jugadores, de 2 a 5 (07-version-4.md §3 y §9): monstruos por sala, su vida, vida del
/// Celador e invocaciones del Tótem. Los números salen de la simulación (docs/balance.md).
/// </summary>
public static class Difficulty
{
    private static readonly EnemyKind[][] RoomEnemies =
    [
        [EnemyKind.Scavenger, EnemyKind.Scavenger, EnemyKind.Moth],
        [EnemyKind.Slime, EnemyKind.Hopper, EnemyKind.Scavenger],
        [EnemyKind.Crossbowman, EnemyKind.Slime, EnemyKind.Moth],
        [EnemyKind.Totem, EnemyKind.Crossbowman],
        [EnemyKind.Warden],
    ];

    /// <summary>Monstruo que se suma a cada sala con 5 jugadores.</summary>
    private static readonly EnemyKind[] FifthPlayerExtra = [EnemyKind.Scavenger, EnemyKind.Scavenger, EnemyKind.Scavenger, EnemyKind.Scavenger];

    /// <summary>Vida del Celador por jugador, indexada por cantidad de jugadores (2 a 5).</summary>
    public static int WardenHpPerPlayer(int playerCount) => Clamp(playerCount) switch
    {
        2 => 9,
        3 => 12,
        4 => 15,
        _ => 15,
    };

    /// <summary>Tope de invocaciones de cada Tótem.</summary>
    public static int TotemMaxSummons(int playerCount) => Clamp(playerCount) >= 5 ? 4 : EnemyCatalog.TotemMaxSummons;

    /// <summary>Monstruos con los que arranca la sala <paramref name="index"/>, antes del tipo de sala.</summary>
    public static IEnumerable<EnemyKind> Monsters(int index, int playerCount)
    {
        var kinds = RoomEnemies[index];
        return Clamp(playerCount) switch
        {
            // Con 2 jugadores la sala trae un monstruo menos: los mismos enemigos tardarían el doble en caer.
            2 => kinds.Take(2),
            5 when index < FifthPlayerExtra.Length => kinds.Append(FifthPlayerExtra[index]),
            _ => kinds,
        };
    }

    /// <summary>Vida base de un monstruo (sin maldiciones) en una partida de tantos jugadores.</summary>
    public static int MonsterHp(EnemyKind kind, int playerCount) => EnemyCatalog.For(kind).MaxHp;

    private static int Clamp(int playerCount) => Math.Clamp(playerCount, MatchFactory.MinPlayers, MatchFactory.MaxPlayers);
}
