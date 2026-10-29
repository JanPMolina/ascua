using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Ascua.Api.Endpoints;

/// <summary>
/// Valida el cuerpo <typeparamref name="T"/> con sus DataAnnotations y responde 400
/// <c>urn:ascua:problem:validation</c> con los errores por campo (nombres en camelCase, como en el JSON).
/// </summary>
internal sealed class ValidationFilter<T> : IEndpointFilter where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var body = context.Arguments.OfType<T>().FirstOrDefault();
        if (body is null)
        {
            return Problems.ValidationResult(new Dictionary<string, string[]> { ["body"] = ["El cuerpo es obligatorio."] });
        }

        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(body, new ValidationContext(body), results, validateAllProperties: true))
        {
            return await next(context);
        }

        var errors = results
            .SelectMany(r => r.MemberNames.DefaultIfEmpty(string.Empty), (r, member) => (Member: JsonNamingPolicy.CamelCase.ConvertName(member), r.ErrorMessage))
            .GroupBy(e => e.Member)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage ?? "Valor inválido.").ToArray());

        return Problems.ValidationResult(errors);
    }
}

internal static class ValidationFilterExtensions
{
    public static RouteHandlerBuilder WithValidation<T>(this RouteHandlerBuilder builder) where T : class =>
        builder.AddEndpointFilter<ValidationFilter<T>>().ProducesValidationProblem();
}
