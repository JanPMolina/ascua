using System.Text.Json;
using System.Text.Json.Serialization;
using Ascua.Application.Matches;
using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Infrastructure.Matches;

/// <summary>
/// Serialización interna (Redis y jsonb de Postgres) de la verdad completa. No es el formato del
/// contrato: lo que sale a los clientes pasa siempre por el proyector de vistas.
/// </summary>
public static class MatchJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },

        // Las propiedades calculadas (Hero.MaxHp, GameState.CurrentRoom…) no se guardan.
        IgnoreReadOnlyProperties = true,
    };

    public static string Serialize(MatchSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Options);

    public static MatchSnapshot DeserializeSnapshot(string json) =>
        JsonSerializer.Deserialize<MatchSnapshot>(json, Options) ?? throw new InvalidOperationException("Instantánea vacía.");

    public static string Serialize(GameState state) => JsonSerializer.Serialize(state, Options);

    public static GameState DeserializeState(string json) =>
        JsonSerializer.Deserialize<GameState>(json, Options) ?? throw new InvalidOperationException("Estado vacío.");

    /// <summary>Payload de un evento con su tipo concreto (el nombre del tipo va en la columna <c>type</c>).</summary>
    public static string Serialize(DomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), Options);
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
