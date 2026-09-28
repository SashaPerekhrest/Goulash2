using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Goulash.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropLegacyDiscoveryCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_discovery_runs_counts",
                table: "discovery_runs");

            migrationBuilder.DropColumn(
                name: "evidence_count",
                table: "discovery_runs");

            migrationBuilder.DropColumn(
                name: "rejected_count",
                table: "discovery_runs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_discovery_runs_counts",
                table: "discovery_runs",
                sql: "accepted_count >= 0 AND failed_profile_count >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_discovery_runs_counts",
                table: "discovery_runs");

            migrationBuilder.AddColumn<int>(
                name: "evidence_count",
                table: "discovery_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "rejected_count",
                table: "discovery_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_discovery_runs_counts",
                table: "discovery_runs",
                sql: "accepted_count >= 0 AND rejected_count >= 0 AND failed_profile_count >= 0");
        }
    }
}
