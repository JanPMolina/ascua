namespace Ascua.Domain.Model;

/// <summary>Identificador opaco del jugador (lo asigna la capa de aplicación).</summary>
public readonly record struct PlayerId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Referencia a una entidad del tablero: un héroe o un enemigo.</summary>
public readonly record struct EntityRef(PlayerId? Hero, int? EnemyId)
{
    public static EntityRef ForHero(PlayerId player) => new(player, null);

    public static EntityRef ForEnemy(int enemyId) => new(null, enemyId);
}
