using System.Text.Json;
using Goulash.Application;

namespace Goulash.Api.Providers;

/// <summary>JSON protocol definitions for the two provider responses.</summary>
internal static class SupplierDiscoveryJson
{
    public static JsonDocument ParseJson(string content)
    {
        try
        {
            var trimmed = (content ?? string.Empty).Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var firstLineEnd = trimmed.IndexOf('\n');
                var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
                if (firstLineEnd >= 0 && closingFence > firstLineEnd)
                    trimmed = trimmed[(firstLineEnd + 1)..closingFence].Trim();
            }
            if (!trimmed.StartsWith('{'))
            {
                var start = trimmed.IndexOf('{');
                var end = trimmed.LastIndexOf('}');
                if (start >= 0 && end > start) trimmed = trimmed[start..(end + 1)];
            }
            return JsonDocument.Parse(trimmed, new JsonDocumentOptions { MaxDepth = 48 });
        }
        catch (JsonException exception)
        {
            _ = exception;
            throw new AiProviderException(ProviderFailureCode.InvalidResponse, "response_json");
        }
    }

    public static object BuildLeadSchema()
    {
        var lead = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                name = new { type = "string" },
                websiteUrl = new { type = new[] { "string", "null" } }
            },
            required = new[] { "name", "websiteUrl" }
        };
        return new
        {
            type = "object",
            additionalProperties = false,
            properties = new { suppliers = new { type = "array", maxItems = 20, items = lead } },
            required = new[] { "suppliers" }
        };
    }

    public static object BuildProfileSchema()
    {
        var nullableString = new { type = new[] { "string", "null" } };
        var nullableNumber = new { type = new[] { "number", "null" } };
        var price = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                amount = nullableString,
                amountMin = nullableString,
                amountMax = nullableString,
                originalText = new { type = new[] { "string", "null" } },
                currency = new { type = new[] { "string", "null" } },
                unit = new { type = new[] { "string", "null" } },
                isApproximate = new { type = "boolean" }
            },
            required = new[] { "amount", "amountMin", "amountMax", "originalText", "currency", "unit", "isApproximate" }
        };
        var minimumOrder = new
        {
            type = new[] { "object", "null" },
            additionalProperties = false,
            properties = new
            {
                amount = nullableString,
                unit = new { type = new[] { "string", "null" } },
                details = new { type = new[] { "string", "null" } }
            },
            required = new[] { "amount", "unit", "details" }
        };
        var product = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                key = new { type = new[] { "string", "null" } },
                name = new { type = new[] { "string", "null" } },
                category = new { type = new[] { "string", "null" } },
                prices = new { type = "array", items = price }
            },
            required = new[] { "key", "name", "category", "prices" }
        };
        var profile = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                name = new { type = new[] { "string", "null" } },
                websiteUrl = new { type = new[] { "string", "null" } },
                description = new { type = new[] { "string", "null" } },
                address = new { type = new[] { "string", "null" } },
                city = new { type = new[] { "string", "null" } },
                region = new { type = new[] { "string", "null" } },
                serviceRegions = new { type = "array", items = new { type = "string" } },
                contacts = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        phones = new { type = "array", items = new { type = "string" } },
                        emails = new { type = "array", items = new { type = "string" } },
                        website = new { type = new[] { "string", "null" } }
                    },
                    required = new[] { "phones", "emails", "website" }
                },
                products = new { type = "array", items = product },
                delivery = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        terms = new { type = new[] { "string", "null" } },
                        maxDays = nullableNumber
                    },
                    required = new[] { "terms", "maxDays" }
                },
                minimumOrder,
                certificates = new { type = "array", items = new { type = "string" } },
                images = new { type = "array", items = new { type = "string" } }
            },
            required = new[] { "name", "websiteUrl", "description", "address", "city", "region", "serviceRegions",
                "contacts", "products", "delivery", "minimumOrder", "certificates", "images" }
        };
        return new
        {
            type = "object",
            additionalProperties = false,
            properties = new { supplier = profile },
            required = new[] { "supplier" }
        };
    }

    public static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static string UrlKey(Uri uri)
    {
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return builder.Uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped).TrimEnd('/');
    }
}
