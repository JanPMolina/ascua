using System.Collections.Immutable;

namespace Ascua.Domain.Model;

/// <summary>Las 12 bendiciones (sección 10 de las reglas).</summary>
public enum Blessing
{
    /// <summary>Brasa viva: 20% de quemar al atacar.</summary>
    LivingEmber,

    /// <summary>Paso ígneo: 15% de dejar fuego en la casilla anterior al moverse.</summary>
    FireStep,

    /// <summary>Puño de piedra: +1 de daño, −1 de vida máxima (−1 ascua para el Avaro).</summary>
    StoneFist,

    /// <summary>Corazón doble: +2 de vida máxima (+2 ascuas para el Avaro).</summary>
    DoubleHeart,

    /// <summary>Hoja hambrienta: cura 1 al matar.</summary>
    HungryBlade,

    /// <summary>Espejo roto: 25% de devolver 1 de daño al recibir un golpe.</summary>
    BrokenMirror,

    /// <summary>Lámpara: +1 de visión.</summary>
    Lamp,

    /// <summary>Ganzúa: los cofres sueltan +1 ascua.</summary>
    Lockpick,

    /// <summary>Último aliento: una vez, al caer queda con 1 de vida (1 ascua para el Avaro).</summary>
    LastBreath,

    /// <summary>Codicia: +1 ascua por golpe final, recibe +1 de daño.</summary>
    Greed,

    /// <summary>Escarcha: 20% de congelar al atacar (no afecta al Celador).</summary>
    Frost,

    /// <summary>Ojo de búho: ve los anuncios de ataque fuera de su visión.</summary>
    OwlEye,
}

/// <summary>Sinergias (recetas de dos bendiciones).</summary>
public enum Synergy
{
    /// <summary>Infierno = Brasa viva + Paso ígneo: su fuego dura 1 ronda más y hace +1.</summary>
    Inferno,

    /// <summary>Coraza viva = Espejo roto + Corazón doble: el reflejo pasa a 50%.</summary>
    LivingArmor,

    /// <summary>Saqueador = Codicia + Ganzúa: ve si un cofre tiene llave de plata antes de abrirlo.</summary>
    Plunderer,
}

public static class BlessingRules
{
    public static IReadOnlyList<Blessing> All { get; } = Enum.GetValues<Blessing>();

    public static ImmutableList<Synergy> SynergiesOf(IReadOnlyCollection<Blessing> blessings)
    {
        var result = ImmutableList.CreateBuilder<Synergy>();
        if (blessings.Contains(Blessing.LivingEmber) && blessings.Contains(Blessing.FireStep))
        {
            result.Add(Synergy.Inferno);
        }

        if (blessings.Contains(Blessing.BrokenMirror) && blessings.Contains(Blessing.DoubleHeart))
        {
            result.Add(Synergy.LivingArmor);
        }

        if (blessings.Contains(Blessing.Greed) && blessings.Contains(Blessing.Lockpick))
        {
            result.Add(Synergy.Plunderer);
        }

        return result.ToImmutable();
    }

    /// <summary>Añade la bendición y aplica sus efectos inmediatos sobre la vida.</summary>
    public static Hero Grant(Hero hero, Blessing blessing)
    {
        if (hero.Has(blessing))
        {
            return hero;
        }

        var updated = hero with { Blessings = hero.Blessings.Add(blessing) };
        if (hero.IsMiser)
        {
            var embers = blessing switch
            {
                Blessing.DoubleHeart => hero.Embers + 2,
                Blessing.StoneFist => Math.Max(0, hero.Embers - 1),
                _ => hero.Embers,
            };
            return updated with { Embers = embers, PeakEmbers = Math.Max(hero.PeakEmbers, embers) };
        }

        return blessing switch
        {
            Blessing.DoubleHeart => updated with { Hp = updated.Hp + 2 },
            Blessing.StoneFist => updated with { Hp = Math.Max(1, Math.Min(updated.Hp, updated.MaxHp)) },
            _ => updated,
        };
    }
}
