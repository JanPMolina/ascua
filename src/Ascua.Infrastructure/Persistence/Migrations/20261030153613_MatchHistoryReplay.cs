using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ascua.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MatchHistoryReplay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_match_players_user_id",
                table: "match_players");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "match_players",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.CreateTable(
                name: "match_states",
                columns: table => new
                {
                    match_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    round = table.Column<int>(type: "integer", nullable: false),
                    from_seq = table.Column<long>(type: "bigint", nullable: false),
                    last_seq = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_match_states", x => new { x.match_id, x.version });
                    table.ForeignKey(
                        name: "fk_match_states_matches_match_id",
                        column: x => x.match_id,
                        principalTable: "matches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_match_players_user_id_created_at",
                table: "match_players",
                columns: new[] { "user_id", "created_at", "match_id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_match_states_match_id_round",
                table: "match_states",
                columns: new[] { "match_id", "round" });

            // Partidas anteriores a esta migración: la fecha de la partida en sus asientos y una sola
            // instantánea (la última) que agrupa todos sus eventos, para que también se puedan reproducir.
            migrationBuilder.Sql("""
                UPDATE match_players p SET created_at = m.created_at FROM matches m WHERE m.id = p.match_id;

                INSERT INTO match_states (match_id, version, round, from_seq, last_seq, state, created_at)
                SELECT id, version, round, 0, last_seq, state, updated_at FROM matches;
                """);

            // Inmutabilidad: una partida terminada (cualquier estado que no sea DRAFTING/IN_PROGRESS) no se
            // reescribe ni se borra; los asientos no cambian y match_states es append-only como match_events.
            migrationBuilder.Sql("""
                CREATE FUNCTION matches_final_immutable() RETURNS trigger AS $$
                BEGIN
                  IF OLD.status NOT IN ('DRAFTING', 'IN_PROGRESS') THEN
                    RAISE EXCEPTION 'La partida % ya terminó (%) y es inmutable', OLD.id, OLD.status;
                  END IF;
                  IF TG_OP = 'DELETE' THEN
                    RETURN OLD;
                  END IF;
                  RETURN NEW;
                END $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_matches_final_immutable
                  BEFORE UPDATE OR DELETE ON matches
                  FOR EACH ROW EXECUTE FUNCTION matches_final_immutable();

                CREATE FUNCTION match_players_immutable() RETURNS trigger AS $$
                BEGIN
                  RAISE EXCEPTION 'Los asientos de la partida % no cambian', OLD.match_id;
                END $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_match_players_immutable
                  BEFORE UPDATE ON match_players
                  FOR EACH ROW EXECUTE FUNCTION match_players_immutable();

                CREATE FUNCTION match_states_append_only() RETURNS trigger AS $$
                BEGIN
                  RAISE EXCEPTION 'match_states es append-only (partida %, versión %)', OLD.match_id, OLD.version;
                END $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_match_states_append_only
                  BEFORE UPDATE OR DELETE ON match_states
                  FOR EACH ROW EXECUTE FUNCTION match_states_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_match_states_append_only ON match_states;
                DROP FUNCTION IF EXISTS match_states_append_only();
                DROP TRIGGER IF EXISTS trg_match_players_immutable ON match_players;
                DROP FUNCTION IF EXISTS match_players_immutable();
                DROP TRIGGER IF EXISTS trg_matches_final_immutable ON matches;
                DROP FUNCTION IF EXISTS matches_final_immutable();
                """);

            migrationBuilder.DropTable(
                name: "match_states");

            migrationBuilder.DropIndex(
                name: "ix_match_players_user_id_created_at",
                table: "match_players");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "match_players");

            migrationBuilder.CreateIndex(
                name: "ix_match_players_user_id",
                table: "match_players",
                column: "user_id");
        }
    }
}
