using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Goulash.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistNormalizedFactValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "normalized_value_json",
                table: "supplier_facts",
                type: "jsonb",
                nullable: true);

            migrationBuilder.Sql("UPDATE supplier_facts SET normalized_value_json = value_json;");

            migrationBuilder.AlterColumn<string>(
                name: "normalized_value_json",
                table: "supplier_facts",
                type: "jsonb",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "normalized_value_json",
                table: "supplier_facts");
        }
    }
}
