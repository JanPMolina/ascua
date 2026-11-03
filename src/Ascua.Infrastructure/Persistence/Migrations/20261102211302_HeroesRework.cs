using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ascua.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Los nueve héroes (planteamiento/05-heroes.md) reemplazan a Yunque, Chispa, Cuervo y Raíz, que dejan de existir.
    /// Una partida guardada con ellos ya no se puede leer, y solo hay datos de prueba: se borran. Partidas, estados y
    /// eventos son inmutables por trigger; se suspenden solo durante el cambio, dentro de la transacción de la migración.
    /// Además, cada partida guarda el plazo reducido del Repique para que sobreviva a un reinicio. No hay vuelta atrás
    /// para lo borrado: <c>Down</c> solo quita la columna.
    /// </summary>
    public partial class HeroesRework : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "bell_duration_ms",
                table: "matches",
                type: "integer",
                nullable: false,
                defaultValue: 8000);

            migrationBuilder.Sql("""
                ALTER TABLE matches DISABLE TRIGGER trg_matches_final_immutable;
                ALTER TABLE match_states DISABLE TRIGGER trg_match_states_append_only;
                ALTER TABLE match_events DISABLE TRIGGER trg_match_events_append_only;

                CREATE TEMP TABLE old_hero_matches ON COMMIT DROP AS
                  SELECT DISTINCT match_id FROM match_players
                  WHERE hero IN ('YUNQUE', 'CHISPA', 'CUERVO', 'RAIZ', 'Anvil', 'Spark', 'Raven', 'Root');

                DELETE FROM match_states WHERE match_id IN (SELECT match_id FROM old_hero_matches);
                DELETE FROM match_events WHERE match_id IN (SELECT match_id FROM old_hero_matches);
                DELETE FROM match_players WHERE match_id IN (SELECT match_id FROM old_hero_matches);
                DELETE FROM matches WHERE id IN (SELECT match_id FROM old_hero_matches);

                ALTER TABLE match_events ENABLE TRIGGER trg_match_events_append_only;
                ALTER TABLE match_states ENABLE TRIGGER trg_match_states_append_only;
                ALTER TABLE matches ENABLE TRIGGER trg_matches_final_immutable;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bell_duration_ms",
                table: "matches");
        }
    }
}
