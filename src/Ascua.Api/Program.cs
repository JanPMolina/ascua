using Ascua.Api.Realtime;
using Ascua.Application.Abstractions;
using Ascua.Domain.Abstractions;
using Ascua.Infrastructure;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
// TODO(Jan): checks de Redis y Postgres con la etiqueta "ready".

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

// Exportador a Application Insights solo si Azure entrega la cadena de conexión.
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddOpenTelemetry().UseAzureMonitor();
}

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapOpenApi();
app.MapScalarApiReference("/docs");

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.MapGet("/api/v1/version", () => new { name = "ascua", version = typeof(Program).Assembly.GetName().Version?.ToString() })
    .WithName("GetVersion");

app.MapHub<MatchHub>("/realtime/match");

await app.RunAsync();

/// <summary>Expuesto para WebApplicationFactory en las pruebas de integración.</summary>
public partial class Program;
