using System.Text.Json;
using System.Text.Json.Serialization;
using Ascua.Domain.Events;

namespace Ascua.Domain.Tests.Support;

/// <summary>Serialización para comparar estados y buscar fugas (enums como texto).</summary>
internal static class Json
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    public static string Of(object value) => JsonSerializer.Serialize(value, value.GetType(), Options);

    public static string Of(IEnumerable<DomainEvent> events) => string.Join("\n", events.Select(Of));
}
