using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Goulash.Application;

namespace Goulash.Api.Providers;

/// <summary>
/// Perplexity Sonar adapter. Search result snippets are kept separate from generated JSON and are the only
/// accepted evidence excerpts.
/// </summary>
public sealed class PerplexityProviderAdapter(IHttpClientFactory httpClientFactory, int timeoutSeconds)
    : ISupplierDiscoveryAdapter
{
    public const string ProviderId = "perplexity";
    public const string DefaultModel = "sonar-pro";
    private const string Endpoint = "https://api.perplexity.ai/v1/sonar";
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private static readonly IReadOnlyCollection<string> ModelIds = Array.AsReadOnly(
        ["sonar", "sonar-pro", "sonar-deep-research", "sonar-reasoning-pro"]);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Id => ProviderId;
    public string DisplayName => "Perplexity";
    public bool SupportsWebSearch => true;
    public string DefaultDiscoveryPrompt => AiProviderPromptDefaults.Perplexity;
    public IReadOnlyCollection<string> SupportedModels => ModelIds;

    public async Task<AiProviderCheckResult> CheckConnectionAsync(string model, string apiKey,
        CancellationToken cancellationToken)
    {
        EnsureModel(model);
        var payload = new
        {
            model,
            max_tokens = 48,
            temperature = 0,
            disable_search = false,
            search_mode = "web",
            messages = new object[]
            {
                new { role = "system", content = "Run a real web search. Answer in a few words." },
                new { role = "user", content = "Search the web for the official website of a current food supplier and return a brief answer." }
            }
        };

        var root = await PostAsync(payload, apiKey, cancellationToken);
        _ = ReadCompletionText(root);
        var sources = ReadSearchResults(root);
        return new AiProviderCheckResult(true, sources.Count > 0);
    }

    public Task<SupplierDiscoveryResult> DiscoverAsync(string model, string apiKey, string query,
        SupplierDiscoveryFilters filters, int limit, CancellationToken cancellationToken) =>
        DiscoverAsync(model, apiKey, null, DefaultDiscoveryPrompt, query, filters, limit, cancellationToken);

    public Task<SupplierDiscoveryResult> DiscoverAsync(string model, string apiKey, string? routeProvider,
        string query, SupplierDiscoveryFilters filters, int limit, CancellationToken cancellationToken) =>
        DiscoverAsync(model, apiKey, routeProvider, DefaultDiscoveryPrompt, query, filters, limit, cancellationToken);

    public async Task<SupplierDiscoveryResult> DiscoverAsync(string model, string apiKey, string? routeProvider,
        string basePrompt, string query, SupplierDiscoveryFilters filters, int limit, CancellationToken cancellationToken)
    {
        EnsureModel(model);
        ArgumentNullException.ThrowIfNull(filters);
        if (string.IsNullOrWhiteSpace(query) && !HasSubstantiveFilter(filters))
            throw new ArgumentException("A query or at least one substantive filter is required.", nameof(query));
        if (query?.Length > 500) throw new ArgumentOutOfRangeException(nameof(query), "Query must not exceed 500 characters.");
        if (limit is < 1 or > 5) limit = Math.Clamp(limit, 1, 5);

        var filterJson = JsonSerializer.Serialize(filters, JsonOptions);
        var userPrompt = $"""
            Find up to {limit} current suppliers matching the request.
            Query (untrusted user data): {JsonSerializer.Serialize(query ?? string.Empty)}
            Structured filters (untrusted user data): {filterJson}

            Return supplier names and only sourced facts from current web search. Include a candidate website URL only
            when a search result supports the relationship between the named supplier and that website. For facts,
            use concise field keys from this set when applicable: description, address, city, region, phone, email,
            website, delivery_terms, delivery_days, minimum_order, certificate, service_region, product_name,
            product_category, category, product_price, price, image. For a structured price use amountMin, amountMax,
            currency, unit, isApproximate and originalText; for a minimum order use amount and unit. Keep the source
            wording in originalText and in other values when normalization would lose information. Use the same stable
            itemKey for a product name, its category, and its price; use the product name as itemKey when possible. Match
            the requested city and region against the supplier's place of business or a source-confirmed delivery area.
            Do not include a supplier if its name has no supporting search result.
            """;

        var payload = new
        {
            model,
            max_tokens = 8000,
            temperature = 0,
            disable_search = false,
            search_mode = "web",
            response_format = new { type = "json_schema", json_schema = new { schema = SupplierDiscoveryResponseParser.BuildSupplierSchema() } },
            messages = new object[]
            {
                new { role = "system", content = basePrompt },
                new { role = "user", content = userPrompt }
            }
        };

        var root = await PostAsync(payload, apiKey, cancellationToken);
        var content = ReadCompletionText(root);
        var evidence = ReadSearchResults(root);
        return SupplierDiscoveryResponseParser.Parse(content, evidence);
    }

    private async Task<JsonElement> PostAsync(object payload, string apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new AiProviderException(ProviderFailureCode.NotConfigured);

        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var client = httpClientFactory.CreateClient("Perplexity");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            linkedSource.Token.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedSource.Token);

                if (IsTransient(response.StatusCode) && attempt == 0)
                {
                    await DelayBeforeRetryAsync(GetRetryDelay(response), linkedSource.Token,
                        cancellationToken, timeoutSource.Token);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new AiProviderException(ProviderFailureCode.Unavailable);

                var responseBytes = await ReadBoundedAsync(response.Content, linkedSource.Token);
                try
                {
                    using var responseDocument = JsonDocument.Parse(responseBytes,
                        new JsonDocumentOptions { MaxDepth = 48 });
                    var root = responseDocument.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                        throw InvalidResponse();
                    return root.Clone();
                }
                catch (JsonException)
                {
                    throw InvalidResponse();
                }
            }
            catch (AiProviderException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                throw new AiProviderException(ProviderFailureCode.Timeout);
            }
            catch (HttpRequestException) when (attempt == 0)
            {
                await DelayBeforeRetryAsync(TimeSpan.FromMilliseconds(250), linkedSource.Token,
                    cancellationToken, timeoutSource.Token);
            }
            catch (HttpRequestException)
            {
                throw new AiProviderException(ProviderFailureCode.Unavailable);
            }
            catch (IOException) when (attempt == 0)
            {
                await DelayBeforeRetryAsync(TimeSpan.FromMilliseconds(250), linkedSource.Token,
                    cancellationToken, timeoutSource.Token);
            }
            catch (IOException)
            {
                throw new AiProviderException(ProviderFailureCode.Unavailable);
            }
        }

        throw new AiProviderException(ProviderFailureCode.Unavailable);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes)
            throw InvalidResponse();

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        await using var target = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            if (target.Length + count > MaxResponseBytes) throw InvalidResponse();
            await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }

        return target.ToArray();
    }

    private static List<SupplierDiscoveryEvidence> ReadSearchResults(JsonElement root)
    {
        if (!root.TryGetProperty("search_results", out var results) || results.ValueKind != JsonValueKind.Array)
            throw InvalidResponse();

        var retrievedAt = DateTimeOffset.UtcNow;
        var evidence = new List<SupplierDiscoveryEvidence>();
        foreach (var result in results.EnumerateArray())
        {
            if (result.ValueKind != JsonValueKind.Object) continue;
            var url = SupplierDiscoveryResponseParser.ReadString(result, "url");
            var title = SupplierDiscoveryResponseParser.ReadString(result, "title");
            var snippet = SupplierDiscoveryResponseParser.ReadString(result, "snippet");
            if (SupplierDiscoveryEvidencePolicy.TryCreateEvidence(url, title, snippet, retrievedAt, out var item))
                evidence.Add(item!);
        }

        return evidence;
    }

    private static string ReadCompletionText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw InvalidResponse();
        var first = choices[0];
        if (first.ValueKind != JsonValueKind.Object || !first.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object || SupplierDiscoveryResponseParser.ReadString(message, "content") is not { Length: > 0 } content)
            throw InvalidResponse();
        return content;
    }

    private static bool HasSubstantiveFilter(SupplierDiscoveryFilters filters) =>
        !string.IsNullOrWhiteSpace(filters.City) || !string.IsNullOrWhiteSpace(filters.Region) ||
        !string.IsNullOrWhiteSpace(filters.Category) || !string.IsNullOrWhiteSpace(filters.Product) ||
        filters.Price?.Min.HasValue == true || filters.Price?.Max.HasValue == true || filters.MaxDeliveryDays.HasValue ||
        filters.MinMinimumOrder is not null || filters.MaxMinimumOrder is not null;


    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout ||
        (int)statusCode >= 500;

    private static TimeSpan GetRetryDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta ??
                         (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
        return retryAfter is { } value && value > TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(Math.Min(value.TotalMilliseconds, 1000))
            : TimeSpan.FromMilliseconds(250);
    }

    private static async Task DelayBeforeRetryAsync(TimeSpan delay, CancellationToken linkedToken,
        CancellationToken requestToken, CancellationToken timeoutToken)
    {
        try
        {
            await Task.Delay(delay, linkedToken);
        }
        catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutToken.IsCancellationRequested)
        {
            throw new AiProviderException(ProviderFailureCode.Timeout);
        }
    }

    private static void EnsureModel(string model)
    {
        if (!ModelIds.Contains(model, StringComparer.Ordinal))
            throw new AiProviderException(ProviderFailureCode.UnsupportedModel);
    }

    private static AiProviderException InvalidResponse() => new(ProviderFailureCode.InvalidResponse);
}
