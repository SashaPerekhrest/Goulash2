using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Goulash.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiProviderRoute : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "route_provider",
                table: "ai_provider_settings",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "route_provider",
                table: "ai_provider_settings");
        }
    }
}
