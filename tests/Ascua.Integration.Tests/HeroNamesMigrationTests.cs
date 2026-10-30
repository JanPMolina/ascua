using Ascua.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ascua.Integration.Tests;

/// <summary>
/// ADR-0008: la migración <c>HeroNamesInSpanish</c> reescribe los asientos guardados con los nombres
/// viejos aunque <c>match_players</c> sea inmutable por trigger. Corre en una base aparte, creada
/// para la prueba, porque la de la colección ya está en la última migración.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class HeroNamesMigrationTests(AscuaApiFactory factory)
{
    private const string BeforeRename = "20261030153743_AdminAudit";

    [Fact]
    public async Task Migration_renames_seats_saved_with_the_old_names_and_back()
    {
        var server = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("Postgres")!;
        var database = $"heroes_{Guid.NewGuid():N}";
        await ExecuteAsync(server, $"CREATE DATABASE {database}");
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(server) { Database = database, Pooling = false }.ConnectionString;
            await using var db = new AscuaDbContext(new DbContextOptionsBuilder<AscuaDbContext>()
                .UseNpgsql(connection)
                .UseSnakeCaseNamingConvention()
                .Options);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(BeforeRename);

            var match = Guid.NewGuid();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO matches (id, room_id, status, version, last_seq, round, state, round_duration_ms, created_at, updated_at)
                VALUES ({match}, {Guid.NewGuid()}, 'FINISHED', 1, 0, 1, CAST({"{}"} AS jsonb), 1000, now(), now())
                """);
            foreach (var (seat, hero) in new[] { (0, "Anvil"), (1, "Root") })
            {
                var user = Guid.NewGuid();
                var name = $"u{seat}_{user:N}"[..16];
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO users (id, username, normalized_username, email, normalized_email, password_hash, role, is_suspended, created_at, updated_at)
                    VALUES ({user}, {name}, {name.ToUpperInvariant()}, {name + "@example.test"}, {name.ToUpperInvariant() + "@EXAMPLE.TEST"}, 'x', 'player', false, now(), now())
                    """);
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO match_players (match_id, user_id, seat, hero, display_name, created_at)
                    VALUES ({match}, {user}, {seat}, {hero}, {name}, now())
                    """);
            }

            await migrator.MigrateAsync();
            Assert.Equal(["YUNQUE", "RAIZ"], await HeroesAsync(db, match));

            await migrator.MigrateAsync(BeforeRename);
            Assert.Equal(["Anvil", "Root"], await HeroesAsync(db, match));
        }
        finally
        {
            await ExecuteAsync(server, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        }
    }

    private static Task<List<string>> HeroesAsync(AscuaDbContext db, Guid match) =>
        db.Database.SqlQuery<string>($"SELECT hero AS \"Value\" FROM match_players WHERE match_id = {match} ORDER BY seat").ToListAsync();

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
