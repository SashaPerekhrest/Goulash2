using System.Text.Json;
using Goulash.Application;
using Xunit;

namespace Goulash.Api.Tests;

public sealed class SupplierDiscoveryEvidenceTests
{
    [Fact]
    public void SearchTitleMayOmitLegalFormButMustStillNameTheBusiness()
    {
        Assert.True(SupplierDiscoveryEvidencePolicy.ContainsSupplierName("ООО Птицефабрика Рассвет",
            "Птицефабрика Рассвет — яйца оптом"));
        Assert.False(SupplierDiscoveryEvidencePolicy.ContainsSupplierName("ООО Птицефабрика Рассвет",
            "Яйца оптом от птицефабрик"));
    }

    [Fact]
    public void NumberMustAppearAsACompleteTokenInTheSearchSnippet()
    {
        using var ten = JsonDocument.Parse("10");
        using var hundred = JsonDocument.Parse("100");

        Assert.False(SupplierDiscoveryEvidencePolicy.EvidenceSupportsValue(ten.RootElement, "Цена: 100 руб."));
        Assert.True(SupplierDiscoveryEvidencePolicy.EvidenceSupportsValue(hundred.RootElement, "Цена: 100 руб."));
        Assert.True(SupplierDiscoveryEvidencePolicy.EvidenceSupportsValue(ten.RootElement, "Цена: 10 руб."));
    }

    [Fact]
    public void MarketplaceListingDoesNotConfirmAnOfficialDomain()
    {
        var listing = Evidence("https://market.example.org/suppliers/acme", "Компания Акме поставляет продукты");
        var candidate = new SupplierDiscoveryCandidate("Акме", listing,
            "https://market.example.org/", listing, []);

        Assert.Null(candidate.ConfirmedOfficialDomain);
        Assert.Equal(SupplierEvidenceStatus.External, candidate.Classify(listing));
    }

    [Fact]
    public void AnotherSubdomainCannotConfirmTheClaimedSite()
    {
        var homepage = Evidence("https://directory.example.org/", "Акме — поставщик продуктов");
        var candidate = new SupplierDiscoveryCandidate("Акме", homepage,
            "https://acme.example.org/", homepage, []);

        Assert.Null(candidate.ConfirmedOfficialDomain);
    }

    [Fact]
    public void ConfirmedSiteRootClassifiesItsSubdomainAsOfficial()
    {
        var homepage = Evidence("https://www.acme.org/", "Акме — поставщик продуктов");
        var candidate = new SupplierDiscoveryCandidate("Акме", homepage,
            "https://acme.org/", homepage, []);

        Assert.Equal("acme.org", candidate.ConfirmedOfficialDomain);
        Assert.Equal(SupplierEvidenceStatus.Official, candidate.Classify(homepage));
        Assert.Equal(SupplierEvidenceStatus.External,
            candidate.Classify(Evidence("https://market.example.net/acme", "Акме — поставщик продуктов")));
    }

    private static SupplierDiscoveryEvidence Evidence(string url, string excerpt) =>
        new(new Uri(url), "Search result", excerpt, DateTimeOffset.UtcNow);
}
