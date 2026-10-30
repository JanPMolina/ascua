using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ascua.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RealtimeMatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "matches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    room_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    last_seq = table.Column<long>(type: "bigint", nullable: false),
                    round = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "jsonb", nullable: false),
                    deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    round_duration_ms = table.Column<int>(type: "integer", nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_matches", x => x.id);
                    table.CheckConstraint("ck_matches_status", "status IN ('DRAFTING', 'IN_PROGRESS', 'FINISHED', 'ABANDONED', 'EXPIRED', 'FAULTED')");
                });

            migrationBuilder.CreateTable(
                name: "match_events",
                columns: table => new
                {
                    match_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    round = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("ux_match_events_match_id_seq", x => new { x.match_id, x.seq });
                    table.ForeignKey(
                        name: "fk_match_events_matches_match_id",
                        column: x => x.match_id,
                        principalTable: "matches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "match_players",
                columns: table => new
                {
                    match_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seat = table.Column<int>(type: "integer", nullable: false),
                    hero = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    display_name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_match_players", x => new { x.match_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_match_players_matches_match_id",
                        column: x => x.match_id,
                        principalTable: "matches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_match_players_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_match_players_user_id",
                table: "match_players",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_matches_status_updated_at",
                table: "matches",
                columns: new[] { "status", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ux_matches_room_id",
                table: "matches",
                column: "room_id",
                unique: true);

            // match_events es append-only y el resultado de una partida no cambia una vez escrito.
            migrationBuilder.Sql("""
                CREATE FUNCTION match_events_append_only() RETURNS trigger AS $$
                BEGIN
                  RAISE EXCEPTION 'match_events es append-only (partida %, seq %)', OLD.match_id, OLD.seq;
                END $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_match_events_append_only
                  BEFORE UPDATE OR DELETE ON match_events
                  FOR EACH ROW EXECUTE FUNCTION match_events_append_only();

                CREATE FUNCTION matches_result_immutable() RETURNS trigger AS $$
                BEGIN
                  IF OLD.result IS NOT NULL AND NEW.result IS DISTINCT FROM OLD.result THEN
                    RAISE EXCEPTION 'El resultado de la partida % es inmutable', OLD.id;
                  END IF;
                  RETURN NEW;
                END $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_matches_result_immutable
                  BEFORE UPDATE ON matches
                  FOR EACH ROW EXECUTE FUNCTION matches_result_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_matches_result_immutable ON matches;
                DROP FUNCTION IF EXISTS matches_result_immutable();
                DROP TRIGGER IF EXISTS trg_match_events_append_only ON match_events;
                DROP FUNCTION IF EXISTS match_events_append_only();
                """);

            migrationBuilder.DropTable(
                name: "match_events");

            migrationBuilder.DropTable(
                name: "match_players");

            migrationBuilder.DropTable(
                name: "matches");
        }
    }
}
