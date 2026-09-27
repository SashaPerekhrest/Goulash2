using System.Net;
using System.Text.Json;
using Goulash.Api.Providers;
using Goulash.Application;
using Xunit;

namespace Goulash.Api.Tests;

public sealed class DiscoveryFilteringTests
{
    [Fact]
    public async Task EmptyQueryAndFiltersAreRejectedBeforeCallingProvider()
    {
        var adapter = new PerplexityProviderAdapter(new StaticHttpClientFactory("{}"), 5);

        await Assert.ThrowsAsync<ArgumentException>(() => adapter.DiscoverAsync(
            "sonar", "test-key", "  ", new SupplierDiscoveryFilters(), 5, CancellationToken.None));
    }

    [Fact]
    public async Task MissingProviderKeyIsReportedWithoutCallingProvider()
    {
        var adapter = new PerplexityProviderAdapter(new StaticHttpClientFactory("{}"), 5);

        var error = await Assert.ThrowsAsync<AiProviderException>(() => adapter.DiscoverAsync(
            "sonar", "", "food suppliers", new SupplierDiscoveryFilters(), 5, CancellationToken.None));

        Assert.Equal(ProviderFailureCode.NotConfigured, error.Code);
    }

    [Fact]
    public async Task InvalidSupplierAndFactRecordsAreCountedAndSkipped()
    {
        const string sourceUrl = "https://suppliers.example.com/list";
        const string content = """
            {"suppliers":[
              {"name":"Supplier Alpha","nameSourceUrl":"https://suppliers.example.com/list","facts":[
                {"fieldKey":"city","itemKey":"city","value":"Moscow","sourceUrl":"https://suppliers.example.com/list"},
                null
              ]},
              {"name":"","nameSourceUrl":"https://suppliers.example.com/list","facts":[]},
              null
            ]}
            """;
        var adapter = new PerplexityProviderAdapter(
            new StaticHttpClientFactory(CreateProviderResponse(content, sourceUrl,
                "Supplier Alpha is a food supplier in Moscow.")), 5);

        var result = await adapter.DiscoverAsync(
            "sonar", "test-key", "food suppliers", new SupplierDiscoveryFilters(), 5, CancellationToken.None);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Supplier Alpha", candidate.Name);
        Assert.Single(candidate.Facts);
        Assert.Equal(2, result.RejectedRecordCount);
        Assert.Equal(1, result.RejectedFactCount);
    }

    [Fact]
    public async Task InvalidModelJsonIsReportedAsInvalidProviderResponse()
    {
        const string sourceUrl = "https://suppliers.example.com/list";
        var adapter = new PerplexityProviderAdapter(
            new StaticHttpClientFactory(CreateProviderResponse("not-json", sourceUrl, "Supplier Alpha")), 5);

        var error = await Assert.ThrowsAsync<AiProviderException>(() => adapter.DiscoverAsync(
            "sonar", "test-key", "food suppliers", new SupplierDiscoveryFilters(), 5, CancellationToken.None));

        Assert.Equal(ProviderFailureCode.InvalidResponse, error.Code);
    }

    [Fact]
    public async Task ProviderTimeoutIsReportedWithoutLeakingHttpException()
    {
        var adapter = new PerplexityProviderAdapter(new HandlerHttpClientFactory(new DelayedResponseHandler()), 1);

        var error = await Assert.ThrowsAsync<AiProviderException>(() => adapter.DiscoverAsync(
            "sonar", "test-key", "food suppliers", new SupplierDiscoveryFilters(), 5, CancellationToken.None));

        Assert.Equal(ProviderFailureCode.Timeout, error.Code);
    }

    [Fact]
    public async Task ProviderKeepsLaterCandidatesForServerSideFiltering()
    {
        const string sourceUrl = "https://suppliers.example.com/list";
        var names = new[]
        {
            "Supplier Alpha", "Supplier Bravo", "Supplier Charlie", "Supplier Delta",
            "Supplier Echo", "Supplier Foxtrot"
        };
        var suppliers = names.Select((name, index) => new
        {
            name,
            nameSourceUrl = sourceUrl,
            facts = index == 5
                ? new object[] { new { fieldKey = "city", itemKey = "city", value = "Moscow", sourceUrl } }
                : Array.Empty<object>()
        }).ToArray();
        var response = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = JsonSerializer.Serialize(new { suppliers }) } } },
            search_results = new[]
            {
                new { url = sourceUrl, title = "Supplier directory",
                    snippet = string.Join(", ", names) + " operate in Moscow." }
            }
        });
        var adapter = new PerplexityProviderAdapter(new StaticHttpClientFactory(response), 5);
        var filters = new SupplierDiscoveryFilters(City: "Moscow");

        var result = await adapter.DiscoverAsync("sonar", "test-key", string.Empty, filters, 5, CancellationToken.None);

        Assert.Equal(6, result.Candidates.Count);
        Assert.Single(result.Candidates, candidate => SupplierDiscoveryMatcher.Matches(candidate, filters));
        Assert.Equal("Supplier Foxtrot", result.Candidates.Last().Name);
    }

    [Fact]
    public void AmbiguousCommaNumberDoesNotPassNumericFilter()
    {
        using var value = JsonDocument.Parse("\"1,000\"");

        Assert.False(SupplierDiscoveryMatcher.TryDecimal(value.RootElement, out _));
    }

    [Fact]
    public void UnambiguousDecimalStringStillPasses()
    {
        using var value = JsonDocument.Parse("\"1.25\"");

        Assert.True(SupplierDiscoveryMatcher.TryDecimal(value.RootElement, out var amount));
        Assert.Equal(1.25m, amount);
    }

    private sealed class StaticHttpClientFactory(string response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StaticResponseHandler(response));
    }

    private static string CreateProviderResponse(string content, string sourceUrl, string snippet) =>
        JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } },
            search_results = new[] { new { url = sourceUrl, title = "Supplier directory", snippet } }
        });

    private sealed class HandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelayedResponseHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class StaticResponseHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response)
            });
    }
}
