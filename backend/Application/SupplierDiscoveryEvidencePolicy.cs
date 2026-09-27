using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Goulash.Application;

/// <summary>Validation helpers for web evidence returned by a provider. Model prose is never accepted as evidence.</summary>
public static partial class SupplierDiscoveryEvidencePolicy
{
    private const int MaxUrlLength = 2048;
    private const int MaxTitleLength = 500;
    private const int MaxExcerptLength = 2000;
    private const int MaxValueLength = 8000;
    private const int MaxFactsPerCandidate = 100;

    private static readonly HashSet<string> AllowedFactFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "description", "address", "city", "region", "category", "phone", "email", "website",
        "delivery_terms", "delivery_days", "minimum_order", "certificate", "service_region",
        "product_name", "product_category", "product_price", "price", "image"
    };

    private static readonly HashSet<string> CommonSecondLevelPublicSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ac", "co", "com", "edu", "gov", "go", "mil", "net", "ne", "nom", "org", "or", "sch", "school"
    };

    // Private suffixes are boundaries too: tenants on these shared hosting domains are separate sites.
    private static readonly HashSet<string> PrivateSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "appspot.com", "blogspot.com", "cloudfront.net", "github.io", "gitlab.io", "herokuapp.com",
        "netlify.app", "pages.dev", "vercel.app", "web.app", "workers.dev", "azurewebsites.net",
        "firebaseapp.com", "onrender.com", "fly.dev", "surge.sh"
    };

    public static bool TryCreateEvidence(string? url, string? title, string? excerpt, DateTimeOffset retrievedAt,
        out SupplierDiscoveryEvidence? evidence)
    {
        evidence = null;
        if (!TryNormalizeHttpUrl(url, out var uri) || uri.AbsoluteUri.Length > MaxUrlLength ||
            string.IsNullOrWhiteSpace(title) || title.Trim().Length > MaxTitleLength ||
            string.IsNullOrWhiteSpace(excerpt) || excerpt.Trim().Length > MaxExcerptLength ||
            retrievedAt.Offset != TimeSpan.Zero)
            return false;

        evidence = new SupplierDiscoveryEvidence(uri, title.Trim(), excerpt.Trim(), retrievedAt);
        return true;
    }

    public static bool TryNormalizeHttpUrl(string? rawUrl, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(rawUrl) || rawUrl.Length > MaxUrlLength ||
            !Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo) || IsLocalOrServiceHost(parsed))
            return false;

        uri = parsed;
        return true;
    }

    public static bool IsAllowedFactField(string? fieldKey) =>
        !string.IsNullOrWhiteSpace(fieldKey) && fieldKey.Length <= 80 && AllowedFactFields.Contains(fieldKey.Trim());

    public static bool IsSupportedFactValue(JsonElement value) =>
        value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) &&
        value.GetRawText().Length <= MaxValueLength;

    public static bool EvidenceSupportsValue(JsonElement value, string excerpt)
    {
        if (string.IsNullOrWhiteSpace(excerpt)) return false;
        var normalizedExcerpt = NormalizeSearchText(excerpt);
        var compactExcerpt = normalizedExcerpt.Replace(" ", string.Empty, StringComparison.Ordinal);
        return SupportsScalar(value, normalizedExcerpt, compactExcerpt, excerpt);
    }

    public static bool IsValidSupplierName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 300 &&
        NormalizeSearchText(name).Length >= 3;

    /// <summary>
    /// Resolves an official domain only when a provider search result on the claimed site independently names the company.
    /// The returned value is the registrable domain, so confirmed subdomains are grouped under one company site.
    /// </summary>
    public static string? ResolveOfficialDomain(SupplierDiscoveryCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.WebsiteUrl) ||
            !TryNormalizeHttpUrl(candidate.WebsiteUrl, out var website) ||
            candidate.WebsiteEvidence is null ||
            !IsSiteRoot(website) || !IsSiteRoot(candidate.WebsiteEvidence.Url) ||
            !SameSiteHost(website.IdnHost, candidate.WebsiteEvidence.Url.IdnHost) ||
            !ContainsSupplierName(candidate.Name, candidate.WebsiteEvidence.Excerpt))
            return null;

        var websiteDomain = GetRegistrableDomain(website.IdnHost);
        var evidenceDomain = GetRegistrableDomain(candidate.WebsiteEvidence.Url.IdnHost);
        return websiteDomain is not null && string.Equals(websiteDomain, evidenceDomain, StringComparison.Ordinal)
            ? websiteDomain
            : null;
    }

    public static string? GetRegistrableDomain(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || IPAddress.TryParse(host.Trim('[', ']'), out _)) return null;
        var labels = host.TrimEnd('.').ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2 || labels.Any(label => label.Length == 0)) return null;

        var hostName = string.Join('.', labels);
        var suffixLabels = 1;
        foreach (var privateSuffix in PrivateSuffixes)
        {
            if (hostName.Equals(privateSuffix, StringComparison.OrdinalIgnoreCase)) return null;
            if (hostName.EndsWith('.' + privateSuffix, StringComparison.OrdinalIgnoreCase))
                suffixLabels = Math.Max(suffixLabels, privateSuffix.Count(character => character == '.') + 1);
        }

        // The Public Suffix List's common delegated namespaces under country-code TLDs include co.uk,
        // com.ru, gov.au, etc. Treat these as public suffixes instead of grouping every tenant together.
        if (labels.Length >= 3 && labels[^1].Length == 2 && CommonSecondLevelPublicSuffixes.Contains(labels[^2]))
            suffixLabels = Math.Max(suffixLabels, 2);

        if (labels.Length <= suffixLabels) return null;
        return string.Join('.', labels.Skip(labels.Length - suffixLabels - 1));
    }

    public static bool IsEvidenceFromOfficialDomain(SupplierDiscoveryEvidence evidence, string? officialDomain)
    {
        if (officialDomain is null) return false;
        var evidenceDomain = GetRegistrableDomain(evidence.Url.IdnHost);
        return string.Equals(evidenceDomain, officialDomain, StringComparison.Ordinal);
    }

    private static bool IsSiteRoot(Uri url) =>
        url.AbsolutePath == "/" && string.IsNullOrEmpty(url.Query);

    private static bool SameSiteHost(string left, string right) =>
        string.Equals(left.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? left[4..] : left,
            right.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? right[4..] : right,
            StringComparison.OrdinalIgnoreCase);

    public static bool ContainsSupplierName(string supplierName, string text)
    {
        var name = NormalizeSearchText(supplierName);
        return name.Length >= 3 && (" " + NormalizeSearchText(text) + " ")
            .Contains(" " + name + " ", StringComparison.Ordinal);
    }

    public static string NormalizeSearchText(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        return NonAlphaNumericRegex().Replace(normalized, " ").Trim();
    }

    private static bool SupportsScalar(JsonElement value, string excerpt, string compactExcerpt, string rawExcerpt)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return SupportsString(value.GetString() ?? string.Empty, excerpt, compactExcerpt);
            case JsonValueKind.Number:
                return SupportsNumber(value.GetRawText(), rawExcerpt);
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Name is "isApproximate" or "is_approximate") continue;
                    if (!SupportsScalar(property.Value, excerpt, compactExcerpt, rawExcerpt)) return false;
                }
                return value.EnumerateObject().Any(property => property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number);
            case JsonValueKind.Array:
                return value.GetArrayLength() > 0 && value.EnumerateArray().All(item =>
                    item.ValueKind == JsonValueKind.True || item.ValueKind == JsonValueKind.False ||
                    SupportsScalar(item, excerpt, compactExcerpt, rawExcerpt));
            case JsonValueKind.True:
            case JsonValueKind.False:
                return true;
            default:
                return false;
        }
    }

    private static bool SupportsString(string rawValue, string excerpt, string compactExcerpt)
    {
        var value = NormalizeSearchText(rawValue);
        if (value.Length >= 3 && excerpt.Contains(value, StringComparison.Ordinal)) return true;

        var alias = rawValue.Trim().ToLowerInvariant() switch
        {
            "rub" => new[] { "руб", "рублей", "рубля", "₽" },
            "usd" => new[] { "доллар", "долларов", "$" },
            "eur" => new[] { "евро", "€" },
            "kg" => new[] { "кг", "килограмм", "kg" },
            "g" => new[] { "г", "грамм", "g" },
            "l" => new[] { "л", "литр", "l" },
            "piece" or "pcs" => new[] { "шт", "штук", "piece", "pcs" },
            "box" => new[] { "короб", "ящик", "box" },
            _ => Array.Empty<string>()
        };
        return alias.Any(item => NormalizeSearchText(item) is { Length: > 0 } normalized &&
            (excerpt.Contains(normalized, StringComparison.Ordinal) || compactExcerpt.Contains(normalized.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal)));
    }

    private static bool SupportsNumber(string rawValue, string rawExcerpt)
    {
        if (!decimal.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) return false;
        return NumberTokenRegex().Matches(rawExcerpt).Select(match => ParseNumberToken(match.Value))
            .Any(value => value == amount);
    }

    private static decimal? ParseNumberToken(string raw)
    {
        var token = WhitespaceRegex().Replace(raw, string.Empty);
        var possibleValues = new HashSet<decimal>();
        if (token.Contains(',') && token.Contains('.'))
        {
            var decimalSeparator = Math.Max(token.LastIndexOf(','), token.LastIndexOf('.'));
            var groupingSeparator = token[decimalSeparator] == ',' ? '.' : ',';
            var canonical = token.Replace(groupingSeparator.ToString(), string.Empty, StringComparison.Ordinal)
                .Replace(token[decimalSeparator], '.');
            if (decimal.TryParse(canonical, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                possibleValues.Add(parsed);
        }
        else
        {
            var separatorIndex = Math.Max(token.LastIndexOf(','), token.LastIndexOf('.'));
            if (separatorIndex < 0)
            {
                if (decimal.TryParse(token, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                    possibleValues.Add(parsed);
            }
            else
            {
                var separator = token[separatorIndex];
                var trailingDigits = token.Length - separatorIndex - 1;
                if (decimal.TryParse(token.Replace(separator, '.'), NumberStyles.Number,
                        CultureInfo.InvariantCulture, out var decimalValue))
                    possibleValues.Add(decimalValue);
                if (trailingDigits == 3 &&
                    decimal.TryParse(token.Replace(separator.ToString(), string.Empty, StringComparison.Ordinal),
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var groupedValue))
                    possibleValues.Add(groupedValue);
            }
        }

        return possibleValues.Count == 1 ? possibleValues.Single() : null;
    }

    private static bool IsLocalOrServiceHost(Uri uri)
    {
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(host.Trim('[', ']'), out var address)) return !IsPublicAddress(address);

        if (!host.Contains('.', StringComparison.Ordinal) || host is "metadata" or "metadata.google.internal" or
            "instance-data" or "host.docker.internal" or "gateway.docker.internal")
            return true;

        return host.EndsWith(".localhost", StringComparison.Ordinal) ||
               host.EndsWith(".local", StringComparison.Ordinal) ||
               host.EndsWith(".internal", StringComparison.Ordinal) ||
               host.EndsWith(".lan", StringComparison.Ordinal) ||
               host.EndsWith(".corp", StringComparison.Ordinal) ||
               host.EndsWith(".intranet", StringComparison.Ordinal) ||
               host.EndsWith(".home.arpa", StringComparison.Ordinal) ||
               host.EndsWith(".test", StringComparison.Ordinal) ||
               host.EndsWith(".example", StringComparison.Ordinal) ||
               host.EndsWith(".invalid", StringComparison.Ordinal) ||
               host.EndsWith(".onion", StringComparison.Ordinal) ||
               host.EndsWith(".svc", StringComparison.Ordinal) ||
               host.EndsWith(".cluster.local", StringComparison.Ordinal) ||
               host.EndsWith(".docker.internal", StringComparison.Ordinal) ||
               host.Equals("localtest.me", StringComparison.Ordinal) ||
               host.EndsWith(".localtest.me", StringComparison.Ordinal) ||
               host.Equals("lvh.me", StringComparison.Ordinal) || host.EndsWith(".lvh.me", StringComparison.Ordinal) ||
               host.Equals("vcap.me", StringComparison.Ordinal) || host.EndsWith(".vcap.me", StringComparison.Ordinal) ||
               host.EndsWith(".nip.io", StringComparison.Ordinal) || host.EndsWith(".sslip.io", StringComparison.Ordinal) ||
               host.EndsWith(".xip.io", StringComparison.Ordinal) || host.EndsWith(".local.gd", StringComparison.Ordinal);
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            return false;

        if (address.IsIPv4MappedToIPv6)
            return IsPublicAddress(address.MapToIPv4());

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var a = bytes[0];
            var b = bytes[1];
            if (a == 0 || a == 10 || a == 127 || a >= 224 || (a == 169 && b == 254) ||
                (a == 172 && b is >= 16 and <= 31) || (a == 192 && b == 168) ||
                (a == 100 && b is >= 64 and <= 127) || (a == 198 && b is 18 or 19) ||
                (a == 192 && b == 0) || (a == 192 && b == 88 && bytes[2] == 99) ||
                (a == 192 && b == 0 && bytes[2] == 2) || (a == 198 && b == 51 && bytes[2] == 100) ||
                (a == 203 && b == 0 && bytes[2] == 113))
                return false;
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Globally routable unicast allocations currently live in 2000::/3.
            if ((bytes[0] & 0xE0) != 0x20) return false;
            if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8) return false;
            return true;
        }

        return false;
    }

    [GeneratedRegex("[^\\p{L}\\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex("(?<!\\d)\\d+(?:[ \\u00A0\\u202F]\\d{3})*(?:[.,]\\d+)?(?!\\d)", RegexOptions.CultureInvariant)]
    private static partial Regex NumberTokenRegex();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
