using Ascua.Application.Abstractions;
using Ascua.Application.Audit;
using Ascua.Application.Users;
using Ascua.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ascua.Infrastructure.Auth;

/// <summary>
/// Crea el primer administrador sin credenciales en el repo: si <c>Ascua:Admin:BootstrapUsername</c> nombra
/// a un usuario ya registrado (con su propia contraseña), al arrancar se le promueve a <c>admin</c> y se
/// audita como acción del sistema. Idempotente; si el usuario no existe solo se avisa en el log.
/// </summary>
public static partial class AdminBootstrap
{
    public const string UsernameKey = "Ascua:Admin:BootstrapUsername";

    public static async Task BootstrapAdminIfConfiguredAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var username = services.GetRequiredService<IConfiguration>()[UsernameKey];
        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AdminBootstrap).FullName!);
        var db = provider.GetRequiredService<AscuaDbContext>();
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == User.Normalize(username), cancellationToken);
        if (user is null)
        {
            LogMissingUser(logger, username);
            return;
        }

        if (user.Role == UserRole.Admin)
        {
            return;
        }

        var now = provider.GetRequiredService<IClock>().UtcNow;
        user.PromoteToAdmin(now);
        db.AuditLog.Add(AuditEntry.Create(
            AuditActor.System, AuditActions.UserPromoted, AuditTargets.User, user.Id.ToString("D"),
            $"Bootstrap ({UsernameKey})", """{"role":"admin"}""", now));
        await db.SaveChangesAsync(cancellationToken);
        LogPromoted(logger, user.Id);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bootstrap de admin: no existe el usuario {Username}; regístrelo y reinicie")]
    private static partial void LogMissingUser(ILogger logger, string username);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bootstrap de admin: usuario {UserId} promovido a admin")]
    private static partial void LogPromoted(ILogger logger, Guid userId);
}
