using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Goulash.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSingleActiveDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_discovery_runs_status",
                table: "discovery_runs");

            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT id, row_number() OVER (ORDER BY started_at DESC, id DESC) AS position
                    FROM discovery_runs
                    WHERE status IN ('queued', 'running')
                )
                UPDATE discovery_runs AS run
                SET status = 'cancelled', stage = 'cancelled', outcome = 'cancelled',
                    finished_at = COALESCE(run.finished_at, now())
                FROM ranked
                WHERE run.id = ranked.id AND ranked.position > 1;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_discovery_runs_status",
                table: "discovery_runs",
                sql: "status IN ('queued', 'running', 'succeeded', 'failed', 'cancelled')");

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_discovery_runs_single_active
                ON discovery_runs ((true))
                WHERE status IN ('queued', 'running');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_discovery_runs_single_active;");

            migrationBuilder.DropCheckConstraint(
                name: "ck_discovery_runs_status",
                table: "discovery_runs");

            migrationBuilder.Sql("UPDATE discovery_runs SET status = 'failed', stage = 'failed', error_code = 'MIGRATION_ROLLBACK' WHERE status = 'cancelled';");

            migrationBuilder.AddCheckConstraint(
                name: "ck_discovery_runs_status",
                table: "discovery_runs",
                sql: "status IN ('queued', 'running', 'succeeded', 'failed')");

        }
    }
}
