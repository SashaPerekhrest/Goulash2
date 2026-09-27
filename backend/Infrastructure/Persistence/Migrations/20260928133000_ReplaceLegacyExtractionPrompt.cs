using Goulash.Application;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Goulash.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260928133000_ReplaceLegacyExtractionPrompt")]
public sealed class ReplaceLegacyExtractionPrompt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var shared = AiProviderPromptDefaults.Shared.Replace("'", "''", StringComparison.Ordinal);
        // This saved prompt belongs to the old extraction-only workflow. Its original remains in the
        // provider-specific row; only the new shared discovery prompt is reset.
        migrationBuilder.Sql($"""
            UPDATE ai_provider_prompts
               SET prompt = '{shared}', updated_at = now()
             WHERE provider_id = 'discovery'
               AND prompt LIKE 'You extract supplier records from the provided web search fragments.%'
               AND prompt LIKE '%use a separate request to determine the city and region%';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) { }
}
