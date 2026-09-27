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
            "поставщик продуктов питания оптом", token);
        return new AiProviderCheckResult(true, response.Evidence.Count > 0);
    }

    public async Task<AiSearchResponse> SearchAsync(string model, string apiKey, string? routeProvider,
        string systemPrompt, string userPrompt, string searchQuery, CancellationToken token)
    {
        if (!SupportsModel(model) || routeProvider is { Length: > 120 } || routeProvider?.Any(char.IsControl) == true)
            throw new AiProviderException(ProviderFailureCode.UnsupportedModel);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = 8000,
            ["plugins"] = new[] { new { id = "web", engine = "exa", max_results = 20, search_prompt = searchQuery } },
            ["messages"] = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userPrompt } }
        };
        if (routeProvider is not null) payload["provider"] = new { only = new[] { routeProvider } };
        var root = await http.PostAsync("Polza", Endpoint, payload, apiKey, token);
        var (content, finishReason) = AiSearchHttpClient.ReadCompletion(root);
        return new AiSearchResponse(content, ReadCitations(root), finishReason);
    }

    private static IReadOnlyList<SupplierDiscoveryEvidence> ReadCitations(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0 || !choices[0].TryGetProperty("message", out var message) ||
            !message.TryGetProperty("annotations", out var annotations) || annotations.ValueKind != JsonValueKind.Array)
            return [];
        var now = DateTimeOffset.UtcNow;
        var evidence = new List<SupplierDiscoveryEvidence>();
        foreach (var annotation in annotations.EnumerateArray())
        {
            if (annotation.ValueKind != JsonValueKind.Object ||
                SupplierDiscoveryResponseParser.ReadString(annotation, "type") != "url_citation" ||
                !annotation.TryGetProperty("url_citation", out var citation) || citation.ValueKind != JsonValueKind.Object)
                continue;
            if (SupplierDiscoveryEvidencePolicy.TryCreateEvidence(
                    SupplierDiscoveryResponseParser.ReadString(citation, "url"),
                    SupplierDiscoveryResponseParser.ReadString(citation, "title"),
                    SupplierDiscoveryResponseParser.ReadString(citation, "content"), now, out var item))
                evidence.Add(item!);
        }
        return evidence.GroupBy(item => SupplierDiscoveryResponseParser.UrlKey(item.Url), StringComparer.Ordinal)
            .Select(group => group.First()).ToArray();
    }
}
