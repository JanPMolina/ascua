using Ascua.Api.Endpoints;
using Ascua.Api.Realtime;
using Ascua.Api.Security;
using Ascua.Application.Abstractions;
using Ascua.Domain.Abstractions;
using Ascua.Infrastructure;
using Ascua.Infrastructure.Persistence;
using Ascua.Infrastructure.Redis;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = Problems.Customize);
// Un JSON mal formado responde 400 (Problem Details de validación) también en Development, en vez de lanzar.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<BearerSecurityTransformer>();
    options.AddOperationTransformer<BearerSecurityTransformer>();
});
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AscuaDbContext>("postgres", tags: ["ready"]);
// Redis (estado caliente, backplane, tickets, límites). Las pruebas sin Redis no lo registran.
if (RedisConnection.IsConfigured(builder.Configuration))
{
    builder.Services.AddAscuaRedis();
}

builder.Services
    .AddSignalR(options =>
    {
        options.MaximumReceiveMessageSize = 4 * 1024;
        options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
    })
    .AddMessagePackProtocol();

builder.Services.AddSingleton<IRandom, SecureRandom>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddAscuaInfrastructure(builder.Configuration);
builder.Services.AddAscuaAuthentication();
builder.Services.AddAscuaRateLimiting(builder.Configuration);

// Detrás del ingress de Container Apps la IP real llega en X-Forwarded-For; sin esto el rate
// limiting por IP vería siempre la del proxy. Solo se confía en el último salto (el ingress).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Exportador a Application Insights solo si Azure entrega la cadena de conexión.
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddOpenTelemetry().UseAzureMonitor();
}

var app = builder.Build();

await app.Services.MigrateDatabaseIfConfiguredAsync();

if (app.Configuration.GetValue<bool>("Ascua:TrustForwardedHeaders"))
{
    app.UseForwardedHeaders();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapOpenApi();
app.MapScalarApiReference("/docs");

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.MapGet("/api/v1/version", () => new { name = "ascua", version = typeof(Program).Assembly.GetName().Version?.ToString() })
    .WithName("GetVersion");

app.MapAuthEndpoints();
app.MapUserEndpoints();

app.MapHub<MatchHub>("/realtime/match");

await app.RunAsync();

/// <summary>Expuesto para WebApplicationFactory en las pruebas de integración.</summary>
public partial class Program;
