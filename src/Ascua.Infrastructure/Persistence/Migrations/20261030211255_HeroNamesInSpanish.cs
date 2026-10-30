using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ascua.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// ADR-0008: los héroes pasan de Anvil/Spark/Raven/Root a YUNQUE/CHISPA/CUERVO/RAIZ. Solo se
    /// reescribe la columna relacional; el jsonb (estado, instantáneas y eventos, que además son
    /// append-only) conserva los nombres viejos y se lee con <c>HeroClassJsonConverter</c>.
    /// Los asientos son inmutables por trigger (MatchHistoryReplay): se suspende solo durante el cambio.
    /// </summary>
    public partial class HeroNamesInSpanish : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE match_players DISABLE TRIGGER trg_match_players_immutable;
                UPDATE match_players SET hero = CASE hero
                  WHEN 'Anvil' THEN 'YUNQUE'
                  WHEN 'Spark' THEN 'CHISPA'
                  WHEN 'Raven' THEN 'CUERVO'
                  WHEN 'Root' THEN 'RAIZ'
                  ELSE hero END;
                ALTER TABLE match_players ENABLE TRIGGER trg_match_players_immutable;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE match_players DISABLE TRIGGER trg_match_players_immutable;
                UPDATE match_players SET hero = CASE hero
                  WHEN 'YUNQUE' THEN 'Anvil'
                  WHEN 'CHISPA' THEN 'Spark'
                  WHEN 'CUERVO' THEN 'Raven'
                  WHEN 'RAIZ' THEN 'Root'
                  ELSE hero END;
                ALTER TABLE match_players ENABLE TRIGGER trg_match_players_immutable;
                """);
        }
    }
}
