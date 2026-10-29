using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Ascua.Api.Endpoints;
using Ascua.Infrastructure.Auth;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Ascua.Api.Security;

/// <summary>Sección <c>Ascua:RateLimiting</c>: ventanas fijas por política.</summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "Ascua:RateLimiting";

    /// <summary>Endpoints de <c>/api/v1/auth</c>, por IP de origen (frena fuerza bruta y registro masivo).</summary>
    public WindowLimit Auth { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) };

    /// <summary>Endpoints autenticados, por usuario (claim <c>sub</c>).</summary>
    public WindowLimit User { get; set; } = new() { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) };

    public sealed class WindowLimit
    {
        public int PermitLimit { get; set; }

        public TimeSpan Window { get; set; }
    }
}

public static class RateLimiting
{
    public const string AuthPolicy = "auth";
    public const string UserPolicy = "user";

    public static IServiceCollection AddAscuaRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimitingOptions>(configuration.GetSection(RateLimitingOptions.SectionName));

        services.AddRateLimiter(options =>
        {
            options.AddPolicy(AuthPolicy, context =>
                FixedWindow($"ip:{context.Connection.RemoteIpAddress}", Limits(context).Auth));

            options.AddPolicy(UserPolicy, context =>
            {
                var key = context.User.FindFirstValue(AscuaClaims.Subject) is { } sub
                    ? $"user:{sub}"
                    : $"ip:{context.Connection.RemoteIpAddress}";
                return FixedWindow(key, Limits(context).User);
            });

            options.OnRejected = async (context, _) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait)
                    ? Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))
                    : 60;
                context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
                await Problems.WriteAsync(context.HttpContext, Problems.RateLimited);
            };
        });

        return services;
    }

    private static RateLimitingOptions Limits(HttpContext context) =>
        context.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;

    private static RateLimitPartition<string> FixedWindow(string key, RateLimitingOptions.WindowLimit limit) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit.PermitLimit,
            Window = limit.Window,
            QueueLimit = 0,
        });
}
