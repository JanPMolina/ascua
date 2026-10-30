using System.Text.Json;
using System.Text.Json.Serialization;
using Ascua.Domain.Model;

namespace Ascua.Infrastructure.Matches;

/// <summary>
/// Nombres de héroe en lo persistido. Antes de unificarlos con el contrato el enum se llamaba
/// Anvil/Spark/Raven/Root; Redis y el jsonb de Postgres pueden tener partidas guardadas con esos
/// nombres, así que al leer se aceptan ambos (ADR-0008). Al escribir solo se usan los nuevos.
/// </summary>
public static class HeroNames
{
    private static readonly Dictionary<string, HeroClass> Legacy = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Anvil"] = HeroClass.Yunque,
        ["Spark"] = HeroClass.Chispa,
        ["Raven"] = HeroClass.Cuervo,
        ["Root"] = HeroClass.Raiz,
    };

    /// <summary>Forma de la columna <c>match_players.hero</c>: la misma del contrato (YUNQUE…).</summary>
    public static string ToColumn(HeroClass hero) => hero.ToString().ToUpperInvariant();

    public static HeroClass Parse(string value) =>
        Enum.TryParse<HeroClass>(value, ignoreCase: true, out var hero) && Enum.IsDefined(hero) ? hero
        : Legacy.TryGetValue(value, out hero) ? hero
        : throw new FormatException($"Héroe desconocido: '{value}'.");
}

/// <summary>Como <see cref="JsonStringEnumConverter"/> para <see cref="HeroClass"/>, pero lee también los nombres viejos.</summary>
public sealed class HeroClassJsonConverter : JsonConverter<HeroClass>
{
    public override HeroClass Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Se esperaba el nombre del héroe y llegó {reader.TokenType}.");
        }

        try
        {
            return HeroNames.Parse(reader.GetString()!);
        }
        catch (FormatException ex)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, HeroClass value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
