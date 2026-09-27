using System.Net;
using System.Text;
using System.Text.Json;
using Goulash.Api.Providers;
using Goulash.Application;
using Xunit;

namespace Goulash.Api.Tests;

public sealed class PolzaProviderAdapterTests
{
    [Theory]
    [InlineData("openai/gpt-4o", true)]
    [InlineData("vendor/model-v2.1", true)]
    [InlineData("gpt-4o", false)]
    [InlineData("vendor/", false)]
    [InlineData("vendor/model/extra", false)]
    [InlineData("https://example.com/model", false)]
    [InlineData("vendor/model with spaces", false)]
    public void SupportsOnlySafePolzaModelIds(string model, bool expected)
    {
        var adapter = CreateAdapter(new RecordingHandler());
        Assert.Equal(expected, adapter.SupportsModel(model));
    }

    [Fact]
    public async Task DiscoveryKeepsSearchAndJsonRequestsSeparateAndPassesOptionalRoute()
    {
        const string sourceUrl = "https://alphafoods.com/about";
        const string excerpt = "Supplier Alpha is a food supplier based in Yekaterinburg.";
        var candidate = new
        {
            name = "Supplier Alpha",
            nameSourceUrl = sourceUrl,
            websiteUrl = (string?)null,
            websiteSourceUrl = (string?)null,
            facts = Array.Empty<object>()
        };
        var searchResponse = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = "Found Supplier Alpha.",
                        annotations = new[]
                        {
                            new
                            {
                                type = "url_citation",
                                url_citation = new { url = sourceUrl, title = "About Alpha Foods", content = excerpt }
                            }
                        }
                    }
                }
            }
        });
        var extractionResponse = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = JsonSerializer.Serialize(new { suppliers = new[] { candidate } }) } } }
        });
        var handler = new RecordingHandler(searchResponse, extractionResponse);
        var adapter = CreateAdapter(handler);

        const string customPrompt = "Prefer suppliers with a warehouse in the selected city.";
        var result = await adapter.DiscoverAsync("openai/gpt-4o", "test-key", "OpenAI", customPrompt,
            "food suppliers in Yekaterinburg", new SupplierDiscoveryFilters(City: "Yekaterinburg"), 5,
            CancellationToken.None);

        Assert.Single(result.Candidates);
        Assert.Equal("Supplier Alpha", result.Candidates[0].Name);
        Assert.Equal(2, handler.RequestBodies.Count);
        using var search = JsonDocument.Parse(handler.RequestBodies[0]);
        using var extraction = JsonDocument.Parse(handler.RequestBodies[1]);
        var searchRoot = search.RootElement;
        Assert.Equal("exa", searchRoot.GetProperty("plugins")[0].GetProperty("engine").GetString());
        Assert.False(searchRoot.TryGetProperty("response_format", out _));
        Assert.Equal("OpenAI", searchRoot.GetProperty("provider").GetProperty("only")[0].GetString());
        var extractionRoot = extraction.RootElement;
        Assert.True(extractionRoot.TryGetProperty("response_format", out _));
        Assert.False(extractionRoot.TryGetProperty("plugins", out _));
        Assert.Equal("OpenAI", extractionRoot.GetProperty("provider").GetProperty("only")[0].GetString());
        Assert.Equal(customPrompt, extractionRoot.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Contains(sourceUrl, extractionRoot.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    private static PolzaProviderAdapter CreateAdapter(RecordingHandler handler) =>
        new(new StaticHttpClientFactory(new HttpClient(handler)), timeoutSeconds: 5);

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(params string[] responseBodies) : HttpMessageHandler
    {
        private readonly Queue<string> _responseBodies = new(responseBodies);
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (_responseBodies.Count == 0)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBodies.Dequeue(), Encoding.UTF8, "application/json")
            };
        }
    }
}
