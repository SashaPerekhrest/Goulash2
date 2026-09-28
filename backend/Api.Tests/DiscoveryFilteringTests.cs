using System.Collections.Concurrent;
using Goulash.Api.Providers;
using Goulash.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Goulash.Api.Tests;

public sealed class DiscoveryFilteringTests
{
    [Fact]
    public async Task FiltersAreSentToLeadRequestAndProfileIsReturnedWithoutPostFiltering()
    {
        var transport = new FakeTransport(
            """{"suppliers":[{"name":"Acme","websiteUrl":"https://acme.example/"}]}""",
            """{"supplier":{"name":"Acme","websiteUrl":"https://acme.example/","city":"Other city","products":[{"key":"p1","name":"Berry","category":"Frozen","prices":[{"amount":12,"currency":"RUB","unit":"kg","isApproximate":false}]}]}}""");
        var service = CreateService();
        var filters = new SupplierDiscoveryFilters(City: "Yekaterinburg", Product: "raspberry",
            Price: new SupplierPriceRange(10, 15, "RUB", "kg"), MaxDeliveryDays: 2);

        var result = await service.DiscoverAsync(transport, "vendor/model", "key", null, "search prompt",
            "berry supplier", filters, 20, CancellationToken.None);

        var calls = transport.Calls.ToArray();
        Assert.Single(result.Candidates);
        Assert.Equal("Other city", Assert.Single(result.Candidates[0].Facts, fact => fact.FieldKey == "city").Value.GetString());
        Assert.Equal(2, calls.Length);
        Assert.Contains("Yekaterinburg", calls[0].UserPrompt);
        Assert.Contains("raspberry", calls[0].SearchQuery);
        Assert.DoesNotContain("Yekaterinburg", calls[1].UserPrompt);
        Assert.DoesNotContain("MaxDeliveryDays", calls[1].UserPrompt);
        Assert.Contains("WEBSITE PAGES", calls[1].UserPrompt);
    }

    [Fact]
    public async Task EmptyLeadListStopsAfterTheFirstRequest()
    {
        var transport = new FakeTransport("""{"suppliers":[]}""");
        var result = await CreateService().DiscoverAsync(transport, "vendor/model", "key", null, "prompt",
            "supplier", new SupplierDiscoveryFilters(), 20, CancellationToken.None);

        Assert.Empty(result.Candidates);
        Assert.Equal("no_candidates", result.Outcome);
        Assert.Single(transport.Calls);
    }

    [Fact]
    public async Task EachLeadGetsOneProfileRequestAndAProfileWithoutOptionalFieldsIsKept()
    {
        var transport = new FakeTransport(
            """{"suppliers":[{"name":"Acme","websiteUrl":null}]}""",
            """{"supplier":{"name":"Acme"}}""");
        var result = await CreateService().DiscoverAsync(transport, "vendor/model", "key", null, "prompt",
            "supplier", new SupplierDiscoveryFilters(), 20, CancellationToken.None);

        Assert.Single(result.Candidates);
        Assert.Empty(result.Candidates[0].Facts);
        Assert.Equal("complete", result.Outcome);
        Assert.Equal(2, transport.Calls.Count);
    }

    [Fact]
    public async Task ProfileRetriesUpToFiveTimesBeforeReturningItsSuccessfulResult()
    {
        var transport = new FakeTransport(
            """{"suppliers":[{"name":"Acme","websiteUrl":null}]}""",
            new AiProviderException(ProviderFailureCode.Unavailable),
            new AiProviderException(ProviderFailureCode.Timeout),
            new AiProviderException(ProviderFailureCode.InvalidResponse),
            new AiProviderException(ProviderFailureCode.Unavailable),
            """{"supplier":{"name":"Acme"}}""");

        var result = await CreateService().DiscoverAsync(transport, "vendor/model", "key", null, "prompt",
            "supplier", new SupplierDiscoveryFilters(), 20, CancellationToken.None);

        Assert.Single(result.Candidates);
        Assert.Equal(0, result.FailedProfileCount);
        Assert.Equal("complete", result.Outcome);
        Assert.Equal(6, transport.Calls.Count);
    }

    [Fact]
    public async Task ProfileThatExhaustsFiveAttemptsReturnsLeadDataWithoutDiscardingOtherCompletedWork()
    {
        var transport = new ProfileRetryTransport();
        var result = await CreateService().DiscoverAsync(transport, "vendor/model", "key", null, "prompt",
            "supplier", new SupplierDiscoveryFilters(), 20, CancellationToken.None);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal("Good", result.Candidates[0].Name);
        Assert.Equal("Unavailable", result.Candidates[1].Name);
        Assert.Empty(result.Candidates[1].Facts);
        Assert.Equal(1, result.FailedProfileCount);
        Assert.Equal("partial", result.Outcome);
        Assert.Equal(5, transport.FailedProfileAttempts);
    }

    private static SupplierDiscoveryService CreateService() => new(
        new SupplierSiteResearcher(new EmptyHttpClientFactory(), NullLogger<SupplierSiteResearcher>.Instance),
        NullLogger<SupplierDiscoveryService>.Instance);

    private sealed class FakeTransport(params object[] responses) : IAiSearchTransport
    {
        private readonly ConcurrentQueue<object> _responses = new(responses);
        public ConcurrentQueue<SearchCall> Calls { get; } = new();
        public string Id => "fake";
        public string DisplayName => "Fake";
        public bool SupportsWebSearch => true;

        public Task<AiProviderCheckResult> CheckConnectionAsync(string model, string apiKey, CancellationToken token) =>
            Task.FromResult(new AiProviderCheckResult(true, true));

        public Task<AiSearchResponse> SearchAsync(string model, string apiKey, string? routeProvider, string systemPrompt,
            string userPrompt, string searchQuery, CancellationToken cancellationToken, object? responseSchema = null,
            IReadOnlyList<string>? searchDomains = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Enqueue(new SearchCall(systemPrompt, userPrompt, searchQuery, responseSchema));
            if (!_responses.TryDequeue(out var response))
                throw new InvalidOperationException("Unexpected provider call.");
            return response switch
            {
                string content => Task.FromResult(new AiSearchResponse(content, [])),
                AiProviderException exception => Task.FromException<AiSearchResponse>(exception),
                _ => throw new InvalidOperationException("Unsupported fake response.")
            };
        }
    }

    private sealed class ProfileRetryTransport : IAiSearchTransport
    {
        public int FailedProfileAttempts { get; private set; }
        public string Id => "retry-fake";
        public string DisplayName => "Retry fake";
        public bool SupportsWebSearch => true;

        public Task<AiProviderCheckResult> CheckConnectionAsync(string model, string apiKey, CancellationToken token) =>
            Task.FromResult(new AiProviderCheckResult(true, true));

        public Task<AiSearchResponse> SearchAsync(string model, string apiKey, string? routeProvider, string systemPrompt,
            string userPrompt, string searchQuery, CancellationToken cancellationToken, object? responseSchema = null,
            IReadOnlyList<string>? searchDomains = null)
        {
            if (userPrompt.Contains("Find up to", StringComparison.Ordinal))
                return Task.FromResult(new AiSearchResponse("""
                    {"suppliers":[{"name":"Good","websiteUrl":null},{"name":"Unavailable","websiteUrl":null}]}
                    """, []));
            if (userPrompt.Contains("Supplier: \"Unavailable\"", StringComparison.Ordinal))
            {
                FailedProfileAttempts++;
                return Task.FromException<AiSearchResponse>(new AiProviderException(ProviderFailureCode.Unavailable));
            }
            return Task.FromResult(new AiSearchResponse("""{"supplier":{"name":"Good"}}""", []));
        }
    }

    private sealed record SearchCall(string SystemPrompt, string UserPrompt, string SearchQuery, object? Schema);

    private sealed class EmptyHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NeverCalledHandler());
    }

    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The test lead URL should not be fetched.");
    }
}
