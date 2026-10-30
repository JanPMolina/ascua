using System.Text.Json;
using System.Text.Json.Serialization;
using Ascua.Api.Endpoints;
using Ascua.Api.Realtime;
using Ascua.Api.Security;
using Ascua.Application.Abstractions;
using Ascua.Domain.Abstractions;
using Ascua.Infrastructure;
using Ascua.Infrastructure.Auth;
using Ascua.Infrastructure.Lobby;
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
// Enums como texto en MAYÚSCULAS (HeroClass, estados de sala y cola), igual que en el contrato AsyncAPI.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false)));
// Redis (estado caliente, backplane, tickets, límites) es obligatorio: el tiempo real y el lobby viven ahí.
builder.Services.AddAscuaRedis();
// El lobby deja un IMatchLauncher "no disponible" con TryAdd; AddAscuaRealtime lo reemplaza por el real.
builder.Services.AddAscuaLobby(builder.Configuration);

builder.Services.AddSingleton<IRandom, SecureRandom>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddAscuaInfrastructure(builder.Configuration);
builder.Services.AddAscuaAuthentication();
builder.Services.AddAscuaRateLimiting(builder.Configuration);
builder.Services.AddAscuaRealtime(builder.Environment);

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
await app.Services.BootstrapAdminIfConfiguredAsync();

if (app.Configuration.GetValue<bool>("Ascua:TrustForwardedHeaders"))
{
    app.UseForwardedHeaders();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseDefaultFiles();
app.UseStaticFiles();

// Antes de autenticar: una versión de protocolo no soportada no debe gastar el ticket.
app.UseWebSockets(RealtimeHandshake.WebSocketOptions(app.Configuration));
app.UseRealtimeProtocolVersion();
app.UseAuthentication();
app.UseAuthorization();
app.UseRealtimeConnectionLimit();
app.UseRateLimiter();

app.MapOpenApi();
app.MapScalarApiReference("/docs");

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.MapGet("/api/v1/version", () => new { name = "ascua", version = typeof(Program).Assembly.GetName().Version?.ToString() })
    .WithName("GetVersion");

app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapRoomEndpoints();
app.MapMatchmakingEndpoints();
app.MapAdminEndpoints();

app.MapAscuaRealtime();

await app.RunAsync();

/// <summary>Expuesto para WebApplicationFactory en las pruebas de integración.</summary>
public partial class Program;
