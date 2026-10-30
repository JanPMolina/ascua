using Microsoft.Net.Http.Headers;

namespace Ascua.Api.Security;

/// <summary>
/// CORS de la API REST con la misma lista blanca que el upgrade WebSocket (<c>Ascua:Cors:AllowedOrigins</c>).
/// Sin credenciales: el JWT viaja en <c>Authorization</c>, no en cookies.
/// </summary>
public static class Cors
{
    public const string PolicyName = "ascua";
    public const string AllowedOriginsKey = "Ascua:Cors:AllowedOrigins";

    public static string[] AllowedOrigins(IConfiguration configuration) =>
        configuration.GetSection(AllowedOriginsKey).Get<string[]>() ?? [];

    public static IServiceCollection AddAscuaCors(this IServiceCollection services, IConfiguration configuration) =>
        services.AddCors(options => options.AddPolicy(PolicyName, policy => policy
            .WithOrigins(AllowedOrigins(configuration))
            .WithMethods(HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete)
            .WithHeaders(HeaderNames.Authorization, HeaderNames.ContentType)
            .WithExposedHeaders(HeaderNames.Location, HeaderNames.RetryAfter)
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));
}
