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

    /// <summary>Extinto: fuera de la partida.</summary>
    Extinct,

    /// <summary>Retirado por abandono (venció la gracia de desconexión).</summary>
    Retired,
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

    public HeroStats Stats => HeroCatalog.For(Class);
    public int MaxHp => Stats.MaxHp + (Has(Blessing.DoubleHeart) ? 2 : 0) - (Has(Blessing.StoneFist) ? 1 : 0);
    public int Damage => Stats.Damage + (Has(Blessing.StoneFist) ? 1 : 0);
    public int BlockPercent => Math.Max(0, Stats.BlockPercent - (Has(Blessing.DoubleHeart) ? 10 : 0));
    public int VisionRadius => HeroCatalog.VisionRadius + (Has(Blessing.Lamp) ? 1 : 0);
    public bool IsActive => Status == HeroStatus.Active;
    public bool IsOnBoard => Status is HeroStatus.Active or HeroStatus.Down;
    public bool IsStealthed => StealthRounds > 0;

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
