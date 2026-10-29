using Microsoft.AspNetCore.SignalR;

namespace Ascua.Api.Realtime;

/// <summary>
/// Hub de partidas. Contrato documentado en contracts/asyncapi.yaml.
/// TODO(Jan): autenticación por ticket de un solo uso, SubmitOrder, GetSnapshot.
/// </summary>
public sealed class MatchHub : Hub
{
    public Task<string> Ping() => Task.FromResult("pong");
}
