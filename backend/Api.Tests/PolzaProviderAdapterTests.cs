using System.Net;
using System.Text;
using System.Text.Json;
using Goulash.Api.Providers;
using Xunit;

namespace Goulash.Api.Tests;

public sealed class PolzaProviderAdapterTests
{
    [Fact]
    public async Task SearchPassesExactStrictProfileSchemaAndUsesExaWebSearch()
    {
        var handler = new RecordingHandler();
        var adapter = new PolzaProviderAdapter(new StaticHttpClientFactory(new HttpClient(handler)));
        var schema = SupplierDiscoveryJson.BuildProfileSchema();

        await adapter.SearchAsync("vendor/model", "test-key", null, "system", "return JSON", "berry supplier",
            CancellationToken.None, schema);

        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var root = body.RootElement;
        var format = root.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("supplier_discovery_response", format.GetProperty("json_schema").GetProperty("name").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        using var expectedSchema = JsonDocument.Parse(JsonSerializer.Serialize(schema,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(expectedSchema.RootElement.GetRawText(),
            format.GetProperty("json_schema").GetProperty("schema").GetRawText());
        Assert.Equal("web", root.GetProperty("plugins")[0].GetProperty("id").GetString());
        Assert.Equal("exa", root.GetProperty("plugins")[0].GetProperty("engine").GetString());
        Assert.Equal("berry supplier", root.GetProperty("plugins")[0].GetProperty("search_prompt").GetString());
    }

    [Fact]
    public async Task LeadSearchPassesExactLeadSchema()
    {
        var handler = new RecordingHandler();
        var adapter = new PolzaProviderAdapter(new StaticHttpClientFactory(new HttpClient(handler)));
        var schema = SupplierDiscoveryJson.BuildLeadSchema();

        await adapter.SearchAsync("vendor/model", "test-key", null, "system", "lead JSON", "berry supplier",
            CancellationToken.None, schema);

        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var expectedSchema = JsonSerializer.Serialize(schema, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var actualSchema = body.RootElement.GetProperty("response_format").GetProperty("json_schema")
            .GetProperty("schema").GetRawText();
        Assert.Equal(expectedSchema, actualSchema);
    }

    [Fact]
    public async Task ProfileSearchRestrictsWebPluginToSupplierDomain()
    {
        var handler = new RecordingHandler();
        var adapter = new PolzaProviderAdapter(new StaticHttpClientFactory(new HttpClient(handler)));

        await adapter.SearchAsync("vendor/model", "test-key", null, "system", "profile JSON", "Acme",
            CancellationToken.None, searchDomains: ["acme.com"]);

        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Contains("site:acme.com", body.RootElement.GetProperty("plugins")[0]
            .GetProperty("search_prompt").GetString());
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"suppliers\\\":[]}\"}}]}",
                    Encoding.UTF8, "application/json")
            };
        }
    }
}
