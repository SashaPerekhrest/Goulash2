using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Goulash.Application;
using Microsoft.Extensions.Logging;

namespace Goulash.Api.Providers;

/// <summary>Reads a bounded set of public HTML pages from the supplier site before profile extraction.</summary>
public sealed class SupplierSiteResearcher(IHttpClientFactory clients, ILogger<SupplierSiteResearcher> logger)
{
    private const int MaxPages = 8;
    private const int MaxPageBytes = 512 * 1024;
    private const int MaxTextPerPage = 7000;
    private static readonly Regex LinkRegex = new("<a\\b[^>]*href\\s*=\\s*(['\"])(.*?)\\1[^>]*>(.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TitleRegex = new("<title\\b[^>]*>(.*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ScriptStyleRegex = new("<(script|style|noscript)\\b[^>]*>.*?</\\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled);

    public async Task<IReadOnlyList<SupplierSitePage>> ReadAsync(string? websiteUrl, CancellationToken token)
    {
        if (!SupplierDiscoverySourcePolicy.TryNormalizeHttpUrl(websiteUrl, out var start)) return [];
        var pages = new List<SupplierSitePage>();
        var queued = new Queue<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        queued.Enqueue(start);

        while (queued.Count > 0 && pages.Count < MaxPages)
        {
            token.ThrowIfCancellationRequested();
            var url = queued.Dequeue();
            if (!seen.Add(url.AbsoluteUri)) continue;
            var page = await FetchAsync(start, url, token);
            if (page is null) continue;
            pages.Add(page);

            foreach (var link in page.Links
                         .Where(candidate => SameSite(start, candidate))
                         .DistinctBy(candidate => candidate.AbsoluteUri, StringComparer.OrdinalIgnoreCase))
            {
                if (seen.Contains(link.AbsoluteUri)) continue;
                queued.Enqueue(link);
                if (queued.Count + pages.Count >= MaxPages * 3) break;
            }
        }

        return pages;
    }

    private async Task<SupplierSitePage?> FetchAsync(Uri rootUrl, Uri requestedUrl, CancellationToken token)
    {
        try
        {
            var current = requestedUrl;
            for (var redirect = 0; redirect <= 4; redirect++)
            {
                if (!SameSite(rootUrl, current) ||
                    !SupplierDiscoverySourcePolicy.TryNormalizeHttpUrl(current.AbsoluteUri, out var safeUrl) ||
                    !await HasOnlyPublicAddressesAsync(safeUrl, token)) return null;

                using var request = new HttpRequestMessage(HttpMethod.Get, safeUrl);
                request.Headers.UserAgent.ParseAdd("GoulashSupplierResearch/1.0 (+public-site-profile)");
                using var response = await clients.CreateClient("SupplierSites").SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, token);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    if (response.Headers.Location is null || redirect == 4) return null;
                    current = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(safeUrl, response.Headers.Location);
                    continue;
                }
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not
                    ("text/html" or "application/xhtml+xml")) return null;
                if (response.Content.Headers.ContentLength > MaxPageBytes) return null;

                await using var stream = await response.Content.ReadAsStreamAsync(token);
                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                while (buffer.Length < MaxPageBytes)
                {
                    var read = await stream.ReadAsync(chunk.AsMemory(0,
                        Math.Min(chunk.Length, MaxPageBytes - (int)buffer.Length)), token);
                    if (read == 0) break;
                    buffer.Write(chunk, 0, read);
                }
                if (buffer.Length >= MaxPageBytes) return null;

                var encoding = ResolveEncoding(response.Content.Headers.ContentType?.CharSet);
                var html = encoding.GetString(buffer.ToArray());
                var title = TitleRegex.Match(html) is { Success: true } titleMatch
                    ? ToPlainText(titleMatch.Groups[1].Value)
                    : safeUrl.Host;
                var links = ReadRelevantLinks(html, safeUrl).ToArray();
                var text = ToPlainText(html);
                if (text.Length > MaxTextPerPage) text = text[..MaxTextPerPage];
                return string.IsNullOrWhiteSpace(text) ? null : new SupplierSitePage(safeUrl, title, text, links);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException or SocketException)
        {
            logger.LogDebug("Supplier page could not be loaded. Host={Host}, ErrorType={ErrorType}",
                requestedUrl.Host, exception.GetType().Name);
        }
        return null;
    }

    private static IEnumerable<Uri> ReadRelevantLinks(string html, Uri pageUrl)
    {
        var links = new List<(Uri Url, int Score)>();
        foreach (Match match in LinkRegex.Matches(html))
        {
            var href = WebUtility.HtmlDecode(match.Groups[2].Value).Trim();
            var label = ToPlainText(match.Groups[3].Value);
            var score = ScoreLink(href + " " + label);
            if (score == 0 || !Uri.TryCreate(pageUrl, href, out var target) ||
                !SupplierDiscoverySourcePolicy.TryNormalizeHttpUrl(target.AbsoluteUri, out var safe)) continue;
            links.Add((safe, score));
        }
        foreach (var link in links.OrderByDescending(link => link.Score))
            yield return link.Url;
    }

    private static int ScoreLink(string value)
    {
        var lower = value.ToLowerInvariant();
        var terms = new[] { "about", "company", "contact", "catalog", "product", "delivery", "price", "wholesale",
            "опт", "о-компании", "компания", "контакт", "каталог", "товар", "достав", "цена", "прайс", "сертификат", "оплат" };
        return terms.Count(lower.Contains);
    }

    private static bool SameSite(Uri root, Uri candidate)
    {
        if (string.Equals(root.IdnHost, candidate.IdnHost, StringComparison.OrdinalIgnoreCase)) return true;
        var rootDomain = SupplierDiscoverySourcePolicy.GetRegistrableDomain(root.IdnHost);
        var candidateDomain = SupplierDiscoverySourcePolicy.GetRegistrableDomain(candidate.IdnHost);
        return rootDomain is not null && string.Equals(rootDomain, candidateDomain, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> HasOnlyPublicAddressesAsync(Uri url, CancellationToken token)
    {
        if (IPAddress.TryParse(url.IdnHost, out var address)) return SupplierDiscoverySourcePolicy.IsPublicAddress(address);
        var addresses = await Dns.GetHostAddressesAsync(url.IdnHost, token);
        return addresses.Length > 0 && addresses.All(SupplierDiscoverySourcePolicy.IsPublicAddress);
    }

    private static string ToPlainText(string html)
    {
        var withoutActive = ScriptStyleRegex.Replace(html, " ");
        return WebUtility.HtmlDecode(TagRegex.Replace(withoutActive, " "))
            .Replace('\u00a0', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Aggregate(new StringBuilder(), (builder, part) => builder.Append(part).Append(' '))
            .ToString().Trim();
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        try { return string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim(' ', '"', '\'')); }
        catch (ArgumentException) { return Encoding.UTF8; }
    }
}
