using System.Collections.Immutable;

namespace Ascua.Domain.Model;

/// <summary>Los 4 héroes, con sus nombres del juego (en el cable viajan como YUNQUE, CHISPA, CUERVO y RAIZ).</summary>
public enum HeroClass
{
    /// <summary>Yunque: tanque, 25% de bloqueo frontal; habilidad Muralla.</summary>
    Yunque,

    /// <summary>Chispa: daño a distancia, acumula cargas; habilidad Descarga.</summary>
    Chispa,

    /// <summary>Cuervo: pícaro, daño ×2 por la espalda; habilidad Paso sombrío.</summary>
    Cuervo,

    /// <summary>Raíz: soporte, sus objetos de curación curan +1; habilidad Brote.</summary>
    Raiz,
}

public enum HeroStatus
{
    Active,

    /// <summary>Caído: vida 0, no actúa; puede ser levantado.</summary>
    Down,

    /// <summary>Extinto: fuera del tablero; elige camino (planteamiento/04-caminos-tras-morir.md).</summary>
    Extinct,

    /// <summary>Retirado por abandono (venció la gracia de desconexión).</summary>
    Retired,

    /// <summary>Brasa errante (Camino de la Amistad): espíritu intangible que ayuda al grupo y gana ecos.</summary>
    EmberSpirit,

    /// <summary>Renegado (Camino de la Venganza): vuelve con su héroe del lado de la mazmorra.</summary>
    Renegade,
}

public sealed record HeroStats(int MaxHp, int Range, int Damage, int Initiative, int SkillCooldown, int BlockPercent);

public static class HeroCatalog
{
    public const int VisionRadius = 3;
    public const int MaxCharges = 3;
    public const int ReviveHp = 2;
    public const int RoundsUntilExtinct = 3;

    public static HeroStats For(HeroClass heroClass) => heroClass switch
    {
        HeroClass.Yunque => new(MaxHp: 6, Range: 1, Damage: 1, Initiative: 1, SkillCooldown: 3, BlockPercent: 25),
        HeroClass.Chispa => new(MaxHp: 4, Range: 3, Damage: 1, Initiative: 3, SkillCooldown: 0, BlockPercent: 0),
        HeroClass.Cuervo => new(MaxHp: 4, Range: 1, Damage: 1, Initiative: 4, SkillCooldown: 4, BlockPercent: 0),
        HeroClass.Raiz => new(MaxHp: 5, Range: 1, Damage: 1, Initiative: 2, SkillCooldown: 3, BlockPercent: 0),
        _ => throw new ArgumentOutOfRangeException(nameof(heroClass), heroClass, null),
    };
}

public sealed record Hero
{
    public required PlayerId Player { get; init; }
    public required int Seat { get; init; }
    public required HeroClass Class { get; init; }
    public Position Position { get; init; }
    public Direction Facing { get; init; } = Direction.North;
    public int Hp { get; init; }
    public int Shield { get; init; }
    public int Embers { get; init; }
    public HeroStatus Status { get; init; }
    public int DownRounds { get; init; }
    public int SkillCooldown { get; init; }
    public int Charges { get; init; }
    public int StealthRounds { get; init; }
    public bool Frozen { get; init; }
    public int BurnRounds { get; init; }
    public int BurnDamage { get; init; }
    public int SilverKeys { get; init; }
    public bool LastBreathUsed { get; init; }
    public ImmutableList<Blessing> Blessings { get; init; } = [];
    public ImmutableList<Blessing> DraftOptions { get; init; } = [];
    public PactKind Pact { get; init; }
    public PactProgress Progress { get; init; } = new();

    /// <summary>Ascuas apartadas al extinguirse; cada camino decide cuántas recupera (privado).</summary>
    public int SavedEmbers { get; init; }

    /// <summary>Ecos ganados como Brasa errante (privado). Suben el porcentaje de ascuas al renacer.</summary>
    public int Echoes { get; init; }

    /// <summary>Veces que se extinguió; identifica cada oferta de camino.</summary>
    public int Extinctions { get; init; }

    /// <summary>Extinto con una elección de camino abierta (se revela al resolver la ronda siguiente).</summary>
    public bool PathOfferPending { get; init; }

    /// <summary>Camino elegido y aún no revelado (privado hasta que se resuelve la ronda).</summary>
    public PathKind? PathChoice { get; init; }

    /// <summary>Brasa: enfriamiento de Estorbo (el de Avivar es <see cref="SkillCooldown"/>).</summary>
    public int HinderCooldown { get; init; }

    /// <summary>Rondas que faltan para que la Calidez de una Brasa pueda volver a curar a este héroe.</summary>
    public int WarmthCooldown { get; init; }

    /// <summary>Brasas que dieron cada punto de escudo de Avivar (uno por punto; se consumen primero).</summary>
    public ImmutableList<PlayerId> EmberShieldBy { get; init; } = [];

    /// <summary>Brasa: usó Faro esta ronda (ilumina un radio de 2 para el grupo hasta la próxima resolución).</summary>
    public bool BeaconActive { get; init; }

    /// <summary>Renegado que todavía no reapareció (espera la sala siguiente o su ronda en la del Celador).</summary>
    public bool AwaitingSpawn { get; init; }

    /// <summary>Renegado en la sala del Celador: ronda en la que reaparece (0 = al entrar a la sala siguiente).</summary>
    public int SpawnRound { get; init; }

    /// <summary>Héroes a los que este Renegado ya dio su primer golpe (lo absorbió el escudo de aviso).</summary>
    public ImmutableList<PlayerId> WarnedHeroes { get; init; } = [];

    public HeroStats Stats => HeroCatalog.For(Class);
    public int MaxHp => Stats.MaxHp + (Has(Blessing.DoubleHeart) ? 2 : 0) - (Has(Blessing.StoneFist) ? 1 : 0);

    /// <summary>Daño básico. El Renegado hace como máximo 1 por golpe (sin Puño de piedra).</summary>
    public int Damage => IsRenegade ? PathRules.RenegadeMaxDamage : Stats.Damage + (Has(Blessing.StoneFist) ? 1 : 0);

    public int BlockPercent => Math.Max(0, Stats.BlockPercent - (Has(Blessing.DoubleHeart) ? 10 : 0));

    public int VisionRadius =>
        (IsEmber ? PathRules.EmberVisionRadius : HeroCatalog.VisionRadius) + (Has(Blessing.Lamp) ? 1 : 0);

    /// <summary>Enfriamiento que deja la habilidad al usarse (×2 para el Renegado).</summary>
    public int SkillCooldownOnUse => Stats.SkillCooldown * (IsRenegade ? PathRules.RenegadeCooldownFactor : 1);

    /// <summary>Cargas máximas de Chispa (el Renegado tiene 2).</summary>
    public int MaxCharges => IsRenegade ? PathRules.RenegadeMaxCharges : HeroCatalog.MaxCharges;

    public bool IsActive => Status == HeroStatus.Active;
    public bool IsEmber => Status == HeroStatus.EmberSpirit;
    public bool IsRenegade => Status == HeroStatus.Renegade;

    /// <summary>Renegado ya presente en la sala: tiene cuerpo, bloquea, ataca y puede ser derrotado.</summary>
    public bool IsPresentRenegade => IsRenegade && !AwaitingSpawn;

    /// <summary>Tiene cuerpo en el tablero: héroes activos o caídos y Renegados presentes (la Brasa es intangible).</summary>
    public bool IsOnBoard => Status is HeroStatus.Active or HeroStatus.Down || IsPresentRenegade;

    /// <summary>Da órdenes en la ronda: héroe activo, Brasa errante o Renegado presente.</summary>
    public bool CanAct => IsActive || IsEmber || IsPresentRenegade;

    /// <summary>Puede recibir daño y curación: héroe activo o Renegado presente.</summary>
    public bool IsVulnerable => IsActive || IsPresentRenegade;

    /// <summary>Extinto con la elección de camino todavía abierta (sin elegir).</summary>
    public bool IsChoosingPath => PathOfferPending && PathChoice is null;

    public bool IsStealthed => StealthRounds > 0;

    /// <summary>Mismo bando: el grupo (héroes, caídos, extintos y Brasas) o la mazmorra (Renegados).</summary>
    public bool IsSameFaction(Hero other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return IsRenegade == other.IsRenegade;
    }

    public bool Has(Blessing blessing) => Blessings.Contains(blessing);

    public bool HasSynergy(Synergy synergy) => BlessingRules.SynergiesOf(Blessings).Contains(synergy);

    public static Hero Create(PlayerId player, int seat, HeroClass heroClass, Position position) => new()
    {
        Player = player,
        Seat = seat,
        Class = heroClass,
        Position = position,
        Hp = HeroCatalog.For(heroClass).MaxHp,
    };
}
