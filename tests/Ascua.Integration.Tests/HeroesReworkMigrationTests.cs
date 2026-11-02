using System.Runtime.CompilerServices;
using Ascua.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ascua.Integration.Tests;

/// <summary>
/// ADR-0008: la migración <c>HeroesRework</c> borra las partidas guardadas con héroes que ya no existen aunque estén
/// protegidas por triggers de inmutabilidad, y los deja activos otra vez. Corre en una base aparte, creada para la
/// prueba, porque la de la colección ya está en la última migración.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class HeroesReworkMigrationTests(AscuaApiFactory factory)
{
    private const string BeforeRework = "20261030211255_HeroNamesInSpanish";

    [Fact]
    public async Task Migration_deletes_matches_with_old_heroes_and_keeps_the_immutability_triggers_active()
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
            await migrator.MigrateAsync(BeforeRework);

            var oldMatch = await InsertMatchAsync(db, "YUNQUE", "RAIZ");
            var englishMatch = await InsertMatchAsync(db, "Anvil", "Root");
            var keptMatch = await InsertMatchAsync(db, "ERRATA", "HILVAN");

            await migrator.MigrateAsync();

            Assert.Equal(0, await CountAsync(db, "matches", "id", oldMatch));
            Assert.Equal(0, await CountAsync(db, "match_players", "match_id", oldMatch));
            Assert.Equal(0, await CountAsync(db, "match_events", "match_id", oldMatch));
            Assert.Equal(0, await CountAsync(db, "match_states", "match_id", oldMatch));
            Assert.Equal(0, await CountAsync(db, "matches", "id", englishMatch));
            Assert.Equal(1, await CountAsync(db, "matches", "id", keptMatch));
            Assert.Equal(2, await CountAsync(db, "match_players", "match_id", keptMatch));
            Assert.Equal(8000, await db.Database.SqlQuery<int>($"SELECT bell_duration_ms AS \"Value\" FROM matches WHERE id = {keptMatch}").SingleAsync());

            // Los triggers siguen activos: una partida terminada y su historia no se pueden borrar.
            await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlAsync($"DELETE FROM match_events WHERE match_id = {keptMatch}"));
            await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlAsync($"DELETE FROM match_states WHERE match_id = {keptMatch}"));
            await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlAsync($"DELETE FROM matches WHERE id = {keptMatch}"));
        }
        finally
        {
            await ExecuteAsync(server, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        }
    }

    private static async Task<Guid> InsertMatchAsync(AscuaDbContext db, params string[] heroes)
    {
        var match = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO matches (id, room_id, status, version, last_seq, round, state, round_duration_ms, created_at, updated_at)
            VALUES ({match}, {Guid.NewGuid()}, 'IN_PROGRESS', 1, 1, 1, CAST({"{}"} AS jsonb), 1000, now(), now())
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO match_events (match_id, seq, round, type, payload, created_at)
            VALUES ({match}, 1, 1, 'MatchCreated', CAST({"{}"} AS jsonb), now())
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO match_states (match_id, version, round, from_seq, last_seq, state, created_at)
            VALUES ({match}, 1, 0, 0, 1, CAST({"{}"} AS jsonb), now())
            """);
        for (var seat = 0; seat < heroes.Length; seat++)
        {
            var user = Guid.NewGuid();
            var name = $"u{seat}_{user:N}"[..16];
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO users (id, username, normalized_username, email, normalized_email, password_hash, role, is_suspended, created_at, updated_at)
                VALUES ({user}, {name}, {name.ToUpperInvariant()}, {name + "@example.test"}, {name.ToUpperInvariant() + "@EXAMPLE.TEST"}, 'x', 'player', false, now(), now())
                """);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO match_players (match_id, user_id, seat, hero, display_name, created_at)
                VALUES ({match}, {user}, {seat}, {heroes[seat]}, {name}, now())
                """);
        }

        // Terminada: la inmutabilidad la protege igual que a una partida real.
        await db.Database.ExecuteSqlAsync($"UPDATE matches SET status = 'FINISHED' WHERE id = {match}");
        return match;
    }

    private static Task<int> CountAsync(AscuaDbContext db, string table, string column, Guid match) =>
        db.Database.SqlQuery<int>(FormattableStringFactory.Create($"SELECT count(*)::int AS \"Value\" FROM {table} WHERE {column} = {{0}}", match)).SingleAsync();

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
