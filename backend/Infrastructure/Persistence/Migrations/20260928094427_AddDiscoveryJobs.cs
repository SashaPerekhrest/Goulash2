using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Goulash.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscoveryJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_discovery_runs_status",
                table: "discovery_runs");

            migrationBuilder.AddColumn<int>(
                name: "candidate_count",
                table: "discovery_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "completed_candidates",
                table: "discovery_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "result_json",
                table: "discovery_runs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stage",
                table: "discovery_runs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddCheckConstraint(
                name: "ck_discovery_runs_status",
                table: "discovery_runs",
                sql: "status IN ('queued', 'running', 'succeeded', 'failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_discovery_runs_status",
                table: "discovery_runs");

            migrationBuilder.DropColumn(
                name: "candidate_count",
                table: "discovery_runs");

            migrationBuilder.DropColumn(
                name: "completed_candidates",
                table: "discovery_runs");

            migrationBuilder.DropColumn(
                name: "result_json",
                table: "discovery_runs");

            migrationBuilder.DropColumn(
                name: "stage",
                table: "discovery_runs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_discovery_runs_status",
                table: "discovery_runs",
                sql: "status IN ('running', 'succeeded', 'failed')");
        }
    }
}
