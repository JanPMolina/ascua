using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ascua.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_matches_status",
                table: "matches");

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    target_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: false),
                    ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_matches_status",
                table: "matches",
                sql: "status IN ('DRAFTING', 'IN_PROGRESS', 'FINISHED', 'ABANDONED', 'EXPIRED', 'FAULTED', 'TERMINATED')");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_actor_id",
                table: "audit_log",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_created_at",
                table: "audit_log",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_target",
                table: "audit_log",
                columns: new[] { "target_type", "target_id" });

            // audit_log es append-only: ni la app ni nadie con el rol de la app puede cambiar ni borrar registros.
            migrationBuilder.Sql("""
                CREATE FUNCTION audit_log_append_only() RETURNS trigger AS $$
                BEGIN
                  RAISE EXCEPTION 'audit_log es append-only (% rechazado)', TG_OP;
                END $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_audit_log_append_only
                  BEFORE UPDATE OR DELETE ON audit_log
                  FOR EACH ROW EXECUTE FUNCTION audit_log_append_only();

                CREATE TRIGGER trg_audit_log_no_truncate
                  BEFORE TRUNCATE ON audit_log
                  FOR EACH STATEMENT EXECUTE FUNCTION audit_log_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_audit_log_no_truncate ON audit_log;
                DROP TRIGGER IF EXISTS trg_audit_log_append_only ON audit_log;
                DROP FUNCTION IF EXISTS audit_log_append_only();
                """);

            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropCheckConstraint(
                name: "ck_matches_status",
                table: "matches");

            migrationBuilder.AddCheckConstraint(
                name: "ck_matches_status",
                table: "matches",
                sql: "status IN ('DRAFTING', 'IN_PROGRESS', 'FINISHED', 'ABANDONED', 'EXPIRED', 'FAULTED')");
        }
    }
}
