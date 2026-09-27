using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Goulash.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260928130000_AddDiscoveryDiagnostics")]
public sealed class AddDiscoveryDiagnostics : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "outcome", table: "discovery_runs",
            type: "character varying(40)", maxLength: 40, nullable: true);
        migrationBuilder.AddColumn<int>(name: "evidence_count", table: "discovery_runs",
            type: "integer", nullable: false, defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "outcome", table: "discovery_runs");
        migrationBuilder.DropColumn(name: "evidence_count", table: "discovery_runs");
    }
}
