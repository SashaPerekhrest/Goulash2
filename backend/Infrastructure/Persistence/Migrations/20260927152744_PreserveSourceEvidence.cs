using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Goulash.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreserveSourceEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_supplier_sources_supplier_url",
                table: "supplier_sources");

            migrationBuilder.CreateIndex(
                name: "ix_supplier_sources_supplier_url",
                table: "supplier_sources",
                columns: new[] { "supplier_id", "url" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_supplier_sources_supplier_url",
                table: "supplier_sources");

            migrationBuilder.CreateIndex(
                name: "ux_supplier_sources_supplier_url",
                table: "supplier_sources",
                columns: new[] { "supplier_id", "url" },
                unique: true);
        }
    }
}
