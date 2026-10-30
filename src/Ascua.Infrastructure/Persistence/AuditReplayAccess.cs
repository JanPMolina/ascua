using System.Text.Json;
using Ascua.Application.Abstractions;
using Ascua.Application.Audit;
using Ascua.Application.Matches;
using Ascua.Application.Users;
using Microsoft.Extensions.DependencyInjection;

namespace Ascua.Infrastructure.Persistence;

/// <summary>
/// Deja en <c>audit_log</c> cada vez que un administrador ve la reproducción completa de una partida.
/// El historial es singleton y la auditoría vive en el DbContext (scoped), así que abre su propio scope.
/// </summary>
public sealed class AuditLogReplayAccessAudit(IServiceScopeFactory scopes, IClock clock) : IReplayAccessAudit
{
    public async Task FullReplayViewedAsync(Guid adminId, Guid matchId, int fromRound, int toRound, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var metadata = JsonSerializer.Serialize(new { fromRound, toRound });
        scope.ServiceProvider.GetRequiredService<IAuditLog>().Append(AuditEntry.Create(
            new AuditActor(adminId, null, null),
            AuditActions.MatchFullReplayViewed,
            AuditTargets.Match,
            matchId.ToString("D"),
            reason: null,
            metadata,
            clock.UtcNow));
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(cancellationToken);
    }
}
