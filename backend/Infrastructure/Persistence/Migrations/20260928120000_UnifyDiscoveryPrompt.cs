using Goulash.Application;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Goulash.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260928120000_UnifyDiscoveryPrompt")]
public sealed class UnifyDiscoveryPrompt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        static string Sql(string value) => value.Replace("'", "''", StringComparison.Ordinal);
        var shared = Sql(AiProviderPromptDefaults.Shared);
        var oldPerplexity = Sql(AiProviderPromptDefaults.Perplexity);
        var oldPolza = Sql(AiProviderPromptDefaults.Polza);
        migrationBuilder.Sql($"""
            INSERT INTO ai_provider_prompts (provider_id, prompt, updated_at)
            SELECT 'discovery',
                   CASE WHEN p.prompt IS NULL OR p.prompt IN ('{oldPerplexity}', '{oldPolza}')
                        THEN '{shared}' ELSE p.prompt END,
                   now()
            FROM (SELECT 1) AS seed
            LEFT JOIN ai_provider_settings AS s ON s.id = '00000000-0000-7000-8000-000000000001'
            LEFT JOIN ai_provider_prompts AS p ON p.provider_id = s.provider_id
            ON CONFLICT (provider_id) DO NOTHING;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DELETE FROM ai_provider_prompts WHERE provider_id = 'discovery';");
}
