using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Goulash.Application;

namespace Goulash.Api.Providers;

/// <summary>Shared bounded HTTP transport for web-enabled chat completions.</summary>
internal sealed class AiSearchHttpClient(IHttpClientFactory factory)
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<JsonElement> PostAsync(string clientName, string endpoint, object payload, string apiKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new AiProviderException(ProviderFailureCode.NotConfigured);
        var client = factory.CreateClient(clientName);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (IsTransient(response.StatusCode) && attempt == 0)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(250);
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(retryAfter.TotalMilliseconds, 0, 1000)), cancellationToken);
                    continue;
                }
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
                    throw new AiProviderException(ProviderFailureCode.UnsupportedModel);
                if (!response.IsSuccessStatusCode) throw new AiProviderException(ProviderFailureCode.Unavailable);
                if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                    throw new AiProviderException(ProviderFailureCode.InvalidResponse, "transport_body");
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                while (true)
                {
                    var read = await stream.ReadAsync(chunk, cancellationToken);
                    if (read == 0) break;
                    if (buffer.Length + read > MaxResponseBytes)
                        throw new AiProviderException(ProviderFailureCode.InvalidResponse, "transport_json");
                    await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
                }
                try
                {
                    using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 48 });
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        throw new AiProviderException(ProviderFailureCode.InvalidResponse);
                    return document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    throw new AiProviderException(ProviderFailureCode.InvalidResponse, "transport_json");
                }
            }
            catch (AiProviderException) { throw; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                throw new AiProviderException(ProviderFailureCode.Unavailable);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException && attempt == 0)
            {
                await Task.Delay(250, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                throw new AiProviderException(ProviderFailureCode.Unavailable);
            }
        }
        throw new AiProviderException(ProviderFailureCode.Unavailable);
    }

    public static (string Content, string? FinishReason) ReadCompletion(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0 || choices[0].ValueKind != JsonValueKind.Object ||
            !choices[0].TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            throw new AiProviderException(ProviderFailureCode.InvalidResponse, "completion_envelope");
        return (SupplierDiscoveryResponseParser.ReadString(message, "content") ?? string.Empty,
            SupplierDiscoveryResponseParser.ReadString(choices[0], "finish_reason"));
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout ||
        (int)status >= 500;
}
