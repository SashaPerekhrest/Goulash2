using System.Net;
using System.Net.Sockets;

namespace Goulash.Application;

/// <summary>URL and public-network safety helpers used when retrieving supplier websites.</summary>
public static class SupplierDiscoverySourcePolicy
{
    private const int MaxUrlLength = 2048;
    private const int MaxTitleLength = 500;
    private const int MaxExcerptLength = 2000;
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

    public static bool TryCreateSource(string? url, string? title, string? excerpt, DateTimeOffset retrievedAt,
        out SupplierDiscoverySource? source)
    {
        source = null;
        if (!TryNormalizeHttpUrl(url, out var uri) || uri.AbsoluteUri.Length > MaxUrlLength ||
            string.IsNullOrWhiteSpace(title) || title.Trim().Length > MaxTitleLength ||
            string.IsNullOrWhiteSpace(excerpt) ||
            retrievedAt.Offset != TimeSpan.Zero)
            return false;

        // Keep a bounded excerpt for storage without discarding a useful citation entirely.
        var boundedExcerpt = excerpt.Trim();
        if (boundedExcerpt.Length > MaxExcerptLength) boundedExcerpt = boundedExcerpt[..MaxExcerptLength];
        source = new SupplierDiscoverySource(uri, title.Trim(), boundedExcerpt, retrievedAt);
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

    public static bool IsPublicAddress(IPAddress address)
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
}
