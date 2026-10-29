using Microsoft.AspNetCore.SignalR.Client;

// Bot mínimo: se conecta al hub y hace ping.
// TODO(Jan): autenticarse, unirse a una partida y enviar órdenes válidas al azar; escenarios NBomber.
var baseUrl = args.Length > 0 ? args[0] : "http://localhost:8080";

await using var connection = new HubConnectionBuilder()
    .WithUrl($"{baseUrl}/realtime/match")
    .Build();

await connection.StartAsync();
var reply = await connection.InvokeAsync<string>("Ping");
Console.WriteLine($"Hub respondió: {reply}");
