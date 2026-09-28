using System.Text.Json;
using Goulash.Application;

namespace Goulash.Api.Providers;

/// <summary>Perplexity Sonar wire format: web search and independent search_results.</summary>
public sealed class PerplexityProviderAdapter(IHttpClientFactory factory) : IAiSearchTransport
{
    public const string ProviderId = "perplexity";
    public const string DefaultModel = "sonar-pro";
    private const string Endpoint = "https://api.perplexity.ai/v1/sonar";
    private static readonly IReadOnlyCollection<string> ModelIds = ["sonar", "sonar-pro", "sonar-deep-research", "sonar-reasoning-pro"];
    private readonly AiSearchHttpClient http = new(factory);

    public string Id => ProviderId;
    public string DisplayName => "Perplexity";
    public bool SupportsWebSearch => true;
    public IReadOnlyCollection<string> SupportedModels => ModelIds;
    public bool SupportsModel(string model) => ModelIds.Contains(model, StringComparer.Ordinal);

    public async Task<AiProviderCheckResult> CheckConnectionAsync(string model, string apiKey, CancellationToken token)
    {
        var response = await SearchAsync(model, apiKey, null, AiProviderPromptDefaults.Shared,
            "Найди одного действующего поставщика продуктов. Верни JSON-объект {\"suppliers\":[]} или запись со ссылкой на источник.",
            "поставщик продуктов питания оптом", token, SupplierDiscoveryJson.BuildLeadSchema());
        return new AiProviderCheckResult(true, response.Sources.Count > 0);
    }

    public async Task<AiSearchResponse> SearchAsync(string model, string apiKey, string? routeProvider,
        string systemPrompt, string userPrompt, string searchQuery, CancellationToken token,
        object? responseSchema = null, IReadOnlyList<string>? searchDomains = null)
    {
        if (!SupportsModel(model) || routeProvider is not null)
            throw new AiProviderException(ProviderFailureCode.UnsupportedModel);
        var payload = new
        {
            model,
            max_tokens = 8000,
            temperature = 0,
            disable_search = false,
            search_mode = "web",
            response_format = new { type = "json_schema", json_schema = new { schema = responseSchema ?? SupplierDiscoveryJson.BuildProfileSchema() } },
            search_domain_filter = searchDomains,
            messages = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userPrompt } }
        };
        var root = await http.PostAsync("Perplexity", Endpoint, payload, apiKey, token);
        var (content, finishReason) = AiSearchHttpClient.ReadCompletion(root);
        if (!root.TryGetProperty("search_results", out var results) || results.ValueKind != JsonValueKind.Array)
            throw new AiProviderException(ProviderFailureCode.InvalidResponse, "sources_envelope");
        var now = DateTimeOffset.UtcNow;
        var sources = new List<SupplierDiscoverySource>();
        foreach (var result in results.EnumerateArray())
        {
            if (result.ValueKind != JsonValueKind.Object) continue;
            if (SupplierDiscoverySourcePolicy.TryCreateSource(
                    SupplierDiscoveryJson.ReadString(result, "url"),
                    SupplierDiscoveryJson.ReadString(result, "title"),
                    SupplierDiscoveryJson.ReadString(result, "snippet"), now, out var item))
                sources.Add(item!);
        }
        return new AiSearchResponse(content, sources, finishReason);
    }
}
