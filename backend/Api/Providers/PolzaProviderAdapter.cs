using System.Text.Json;
using Goulash.Application;

namespace Goulash.Api.Providers;

/// <summary>Polza wire format: Exa web plugin and URL citations.</summary>
public sealed class PolzaProviderAdapter(IHttpClientFactory factory) : IAiSearchTransport
{
    public const string ProviderId = "polza";
    public const string DefaultModel = "openai/gpt-4o";
    private const string Endpoint = "https://polza.ai/api/v1/chat/completions";
    private readonly AiSearchHttpClient http = new(factory);

    public string Id => ProviderId;
    public string DisplayName => "Polza.ai";
    public bool SupportsWebSearch => true;
    public bool SupportsFreeformModel => true;
    public bool SupportsProviderRouting => true;
    public IReadOnlyCollection<string> SupportedModels => [DefaultModel];

    public bool SupportsModel(string model) => !string.IsNullOrWhiteSpace(model) && model.Length <= 200 &&
        model.Count(character => character == '/') == 1 &&
        model.Split('/') is [var vendor, var name] && vendor.Length > 0 && name.Length > 0 &&
        model.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '.' or '_' or ':' or '-');

    public Task<AiProviderCheckResult> CheckConnectionAsync(string model, string apiKey, CancellationToken token) =>
        CheckConnectionAsync(model, apiKey, null, token);

    public async Task<AiProviderCheckResult> CheckConnectionAsync(string model, string apiKey,
        string? routeProvider, CancellationToken token)
    {
        var response = await SearchAsync(model, apiKey, routeProvider,
            AiProviderPromptDefaults.Shared,
            "Найди одного действующего поставщика продуктов. Верни JSON-объект {\"suppliers\":[]} или запись со ссылкой на источник.",
            "поставщик продуктов питания оптом", token, SupplierDiscoveryJson.BuildLeadSchema());
        return new AiProviderCheckResult(true, response.Sources.Count > 0);
    }

    public async Task<AiSearchResponse> SearchAsync(string model, string apiKey, string? routeProvider,
        string systemPrompt, string userPrompt, string searchQuery, CancellationToken token,
        object? responseSchema = null, IReadOnlyList<string>? searchDomains = null)
    {
        if (!SupportsModel(model) || routeProvider is { Length: > 120 } || routeProvider?.Any(char.IsControl) == true ||
            routeProvider?.IndexOfAny(['@', '&', '=']) >= 0)
            throw new AiProviderException(ProviderFailureCode.UnsupportedModel);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = BuildRoutedModel(model, routeProvider),
            ["max_tokens"] = 8000,
            ["plugins"] = new[] { new { id = "web", engine = "exa", max_results = 20,
                search_prompt = BuildSearchPrompt(searchQuery, searchDomains) } },
            ["response_format"] = BuildResponseFormat(responseSchema),
            ["messages"] = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userPrompt } }
        };
        var root = await http.PostAsync("Polza", Endpoint, payload, apiKey, token);
        var (content, finishReason) = AiSearchHttpClient.ReadCompletion(root);
        return new AiSearchResponse(content, ReadCitations(root), finishReason);
    }

    private static object BuildResponseFormat(object? responseSchema) => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "supplier_discovery_response",
            strict = true,
            schema = responseSchema ?? SupplierDiscoveryJson.BuildProfileSchema()
        }
    };

    private static string BuildRoutedModel(string model, string? routeProvider) =>
        string.IsNullOrWhiteSpace(routeProvider)
            ? model
            : $"{model}@provider={routeProvider.Trim()}";

    private static string BuildSearchPrompt(string searchQuery, IReadOnlyList<string>? searchDomains) =>
        searchDomains is { Count: > 0 }
            ? $"{searchQuery} {string.Join(' ', searchDomains.Select(domain => $"site:{domain}"))}"
            : searchQuery;

    private static IReadOnlyList<SupplierDiscoverySource> ReadCitations(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0 || !choices[0].TryGetProperty("message", out var message) ||
            !message.TryGetProperty("annotations", out var annotations) || annotations.ValueKind != JsonValueKind.Array)
            return [];
        var now = DateTimeOffset.UtcNow;
        var sources = new List<SupplierDiscoverySource>();
        foreach (var annotation in annotations.EnumerateArray())
        {
            if (annotation.ValueKind != JsonValueKind.Object ||
                SupplierDiscoveryJson.ReadString(annotation, "type") != "url_citation" ||
                !annotation.TryGetProperty("url_citation", out var citation) || citation.ValueKind != JsonValueKind.Object)
                continue;
            if (SupplierDiscoverySourcePolicy.TryCreateSource(
                    SupplierDiscoveryJson.ReadString(citation, "url"),
                    SupplierDiscoveryJson.ReadString(citation, "title"),
                    SupplierDiscoveryJson.ReadString(citation, "content"), now, out var item))
                sources.Add(item!);
        }
        return sources.GroupBy(item => SupplierDiscoveryJson.UrlKey(item.Url), StringComparer.Ordinal)
            .Select(group => group.First()).ToArray();
    }
}
