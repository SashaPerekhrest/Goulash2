using System.Text.Json;
using Goulash.Application;
using Microsoft.Extensions.Logging;

namespace Goulash.Api.Providers;

/// <summary>One provider-independent search, parse and evidence validation pipeline.</summary>
public sealed class SupplierDiscoveryService(ILogger<SupplierDiscoveryService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SupplierDiscoveryResult> DiscoverAsync(IAiSearchTransport transport, string model,
        string apiKey, string? routeProvider, string basePrompt, string query, SupplierDiscoveryFilters filters,
        int limit, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(filters);
        if (string.IsNullOrWhiteSpace(query) && !HasSubstantiveFilter(filters))
            throw new ArgumentException("A query or substantive filter is required.", nameof(query));
        if (query?.Length > 500) throw new ArgumentOutOfRangeException(nameof(query));
        limit = Math.Clamp(limit, 1, 5);
        var userPrompt = BuildUserPrompt(query ?? string.Empty, filters, limit);
        var searchQuery = BuildSearchQuery(query ?? string.Empty, filters);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await transport.SearchAsync(model, apiKey, routeProvider, basePrompt, userPrompt, searchQuery, token);
            if (response.FinishReason is "length" or "content_filter")
            {
                logger.LogWarning("Discovery output incomplete: provider={Provider}, reason={Reason}, attempt={Attempt}",
                    transport.Id, response.FinishReason, attempt + 1);
                if (attempt == 0) continue;
                throw new AiProviderException(ProviderFailureCode.InvalidResponse);
            }
            if (response.Evidence.Count == 0)
            {
                logger.LogWarning("Discovery has no usable citations: provider={Provider}, attempt={Attempt}", transport.Id, attempt + 1);
                if (attempt == 0) continue;
                return new SupplierDiscoveryResult([], 0, 0, 0, "no_sources");
            }
            try
            {
                var parsed = SupplierDiscoveryResponseParser.Parse(response.Content, response.Evidence);
                var outcome = parsed.Candidates.Count > 0 ? "candidates" :
                    parsed.RejectedRecordCount > 0 ? "invalid_candidates" : "model_empty";
                logger.LogInformation("Discovery parsed: provider={Provider}, sources={Sources}, candidates={Candidates}, rejectedRecords={RejectedRecords}, rejectedFacts={RejectedFacts}, outcome={Outcome}",
                    transport.Id, response.Evidence.Count, parsed.Candidates.Count, parsed.RejectedRecordCount,
                    parsed.RejectedFactCount, outcome);
                if (parsed.RejectedRecordCount > 0)
                    logger.LogInformation("Discovery rejection reasons: provider={Provider}, counts={Counts}",
                        transport.Id, JsonSerializer.Serialize(parsed.RejectedReasons, JsonOptions));
                return parsed with { EvidenceCount = response.Evidence.Count, Outcome = outcome };
            }
            catch (AiProviderException exception) when (exception.Code == ProviderFailureCode.InvalidResponse && attempt == 0)
            {
                logger.LogWarning("Discovery returned invalid JSON: provider={Provider}; retrying once", transport.Id);
            }
        }
        throw new AiProviderException(ProviderFailureCode.InvalidResponse);
    }

    private static string BuildUserPrompt(string query, SupplierDiscoveryFilters filters, int limit) => $$"""
        Найди через веб-поиск 10–12 разных реальных компаний, продающих товар по запросу {{JsonSerializer.Serialize(query)}}.
        Фильтры пользователя: {{JsonSerializer.Serialize(filters, JsonOptions)}}.
        Перечисли компании, названия которых явно видны в найденных страницах. Если есть хотя бы одна такая компания,
        не возвращай пустой список. Сначала укажи название каждой компании; дополнительные факты необязательны.
        Верни только JSON без Markdown: {"suppliers":[{"name":"Название компании","facts":[]}]}.
        Для фактов используй элементы {"fieldKey":"product_name","itemKey":"товар","value":"значение"}.
        Возможные fieldKey: product_name, category, city, region, address, phone, email, website, price,
        delivery_terms, service_region, description. Указывай факт только если его значение есть в найденном источнике.
        URL источника можно указать в nameSourceUrl и sourceUrl, но при сомнении пропусти: сервер свяжет запись с цитатой.
        Сервер проверит источники и оставит не более {{limit}} поставщиков.
        """;

    private static string BuildSearchQuery(string query, SupplierDiscoveryFilters filters)
    {
        var terms = new[] { query.Trim(), filters.Product, filters.Category, filters.City, filters.Region };
        if (terms.All(string.IsNullOrWhiteSpace)) return "поставщики оптом";
        return string.Join(' ', terms.Where(term => !string.IsNullOrWhiteSpace(term)));
    }

    private static bool HasSubstantiveFilter(SupplierDiscoveryFilters filters) =>
        !string.IsNullOrWhiteSpace(filters.City) || !string.IsNullOrWhiteSpace(filters.Region) ||
        !string.IsNullOrWhiteSpace(filters.Category) || !string.IsNullOrWhiteSpace(filters.Product) ||
        filters.Price?.Min.HasValue == true || filters.Price?.Max.HasValue == true || filters.MaxDeliveryDays.HasValue ||
        filters.MinMinimumOrder is not null || filters.MaxMinimumOrder is not null;
}
