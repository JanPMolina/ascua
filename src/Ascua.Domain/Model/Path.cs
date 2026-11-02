namespace Ascua.Domain.Model;

/// <summary>Caminos tras la extinción (planteamiento/04-caminos-tras-morir.md).</summary>
public enum PathKind
{
    /// <summary>Camino de la Amistad: Brasa errante que ayuda al grupo y renace al bajar de piso.</summary>
    Friendship,

    /// <summary>Camino de la Venganza: Renegado del lado de la mazmorra.</summary>
    Vengeance,
}

/// <summary>Habilidades de la Brasa errante. Deriva (MOVER) se informa como un movimiento.</summary>
public enum EmberAbility
{
    /// <summary>Avivar (HABILIDAD): +1 de escudo al primer aliado a ≤2 casillas en esa dirección.</summary>
    Kindle,

    /// <summary>Reavivar (INTERACTUAR): levanta a un aliado caído adyacente con 1 de vida.</summary>
    Rekindle,

    /// <summary>Estorbo (ATACAR): retrasa una ronda el ataque anunciado de un enemigo a ≤2 casillas.</summary>
    Hinder,

    /// <summary>Faro (GUARDAR): ilumina un radio de 2 y comparte esa visión con el grupo.</summary>
    Beacon,

    /// <summary>Calidez (pasiva): cura 1 a un aliado adyacente al terminar la ronda, cada 3 rondas por aliado.</summary>
    Warmth,
}

/// <summary>Por qué una Brasa ganó ecos.</summary>
public enum EchoReason
{
    /// <summary>El escudo de Avivar absorbió daño (+1 por punto).</summary>
    ShieldAbsorbed,

    /// <summary>Reavivar levantó a un aliado (+3).</summary>
    Rekindled,

    /// <summary>Estorbo retrasó un ataque que iba a golpear a un héroe (+1).</summary>
    AttackHindered,

    /// <summary>Un aliado dio un golpe final dentro del Faro (+1).</summary>
    BeaconKill,

    /// <summary>La Calidez curó a un aliado (+1).</summary>
    Warmth,
}

/// <summary>Números y fórmulas de los dos caminos. Puras: las comparten el motor, las vistas y las pruebas.</summary>
public static class PathRules
{
    public const int EmberVisionRadius = 4;
    public const int DriftDistance = 2;
    public const int KindleRange = 2;
    public const int KindleCooldown = 1;
    public const int MaxEmberShield = 2;
    public const int RekindleHp = 1;
    public const int RekindleEchoes = 3;
    public const int HinderRange = 2;
    public const int HinderCooldown = 2;
    public const int BeaconRadius = 2;
    public const int WarmthHeal = 1;
    public const int WarmthEvery = 3;

    public const int RebirthBasePercent = 25;
    public const int RebirthPercentPerEcho = 8;
    public const int RebirthMaxPercent = 85;

    public const int RenegadeHpPercent = 60;
    public const int RenegadeFinalHpPercent = 40;
    public const int RenegadeMaxDamage = 1;
    public const int RenegadeCooldownFactor = 2;
    public const int RenegadeFinalSkillLock = 3;
    public const int RenegadeSpawnDelay = 2;
    public const int RenegadeMinDistance = 4;
    public const int RenegadeDownReward = 2;
    public const int RenegadeDefeatReward = 2;
    public const int WardenHpPerRenegade = 6;
    public const int WardenMinHp = 12;

    /// <summary>Porcentaje de las ascuas guardadas que recupera la Brasa: 25% + 8% por eco, tope 85%.</summary>
    public static int RebirthPercent(int echoes) =>
        Math.Min(RebirthMaxPercent, RebirthBasePercent + (RebirthPercentPerEcho * Math.Max(0, echoes)));

    /// <summary>⌈guardadas × (25% + 8% × ecos)⌉, con tope de 85% de las guardadas.</summary>
    public static int RebirthEmbers(int saved, int echoes) => CeilPercent(saved, RebirthPercent(echoes));

    /// <summary>Lo que recupera la Brasa de este héroe: el Avaro vuelve con al menos 2 (sus ascuas son su vida).</summary>
    public static int RebirthEmbers(Hero hero, int echoes)
    {
        ArgumentNullException.ThrowIfNull(hero);
        var embers = RebirthEmbers(hero.SavedEmbers, echoes);
        return hero.IsMiser ? Math.Max(HeroRules.MiserMinReturnEmbers, embers) : embers;
    }

    /// <summary>⌈vidaMáxima / 2⌉.</summary>
    public static int RebirthHp(int maxHp) => (maxHp + 1) / 2;

    /// <summary>⌈guardadas / 2⌉: lo que conserva el Renegado.</summary>
    public static int RenegadeEmbers(int saved) => (saved + 1) / 2;

    /// <summary>Lo que conserva el Renegado de este héroe: el Avaro vuelve con al menos 2.</summary>
    public static int RenegadeEmbers(Hero hero)
    {
        ArgumentNullException.ThrowIfNull(hero);
        var embers = RenegadeEmbers(hero.SavedEmbers);
        return hero.IsMiser ? Math.Max(HeroRules.MiserMinReturnEmbers, embers) : embers;
    }

    /// <summary>60% de la vida máxima (40% en la sala del Celador), redondeando hacia arriba.</summary>
    public static int RenegadeHp(int maxHp, bool wardenRoom) =>
        Math.Max(1, CeilPercent(maxHp, wardenRoom ? RenegadeFinalHpPercent : RenegadeHpPercent));

    private static int CeilPercent(int value, int percent) => ((Math.Max(0, value) * percent) + 99) / 100;
}
