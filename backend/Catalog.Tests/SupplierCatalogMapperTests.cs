using System.Text.Json;
using Goulash.Api.Providers;
using Goulash.Application;
using Goulash.Domain;
using Xunit;

namespace Goulash.Catalog.Tests;

public sealed class SupplierCatalogMapperTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DetailsIncludeImageFactsAndCompanyRegion()
    {
        var supplier = CreateSupplier();
        AddFact(supplier, "region", "region", "Свердловская область", VerificationStatus.External);
        AddFact(supplier, "image", "photo-1", "https://supplier.example/photo.jpg", VerificationStatus.Official);

        var details = SupplierCatalogMapper.ToDetails(supplier);

        Assert.Equal("Свердловская область", details.Region.Value);
        Assert.Equal(FactStatus.External, details.Region.Status);
        Assert.Single(details.Region.Sources);
        var image = Assert.Single(details.Images);
        Assert.Equal("https://supplier.example/photo.jpg", image.Value);
        Assert.Equal(FactStatus.Official, image.Status);
        Assert.Single(image.Sources);
    }

    [Fact]
    public void ProductCategoryUsesTheMatchingItemKey()
    {
        var supplier = CreateSupplier();
        supplier.Products.Add(new SupplierProduct(supplier.Id, "a", "Малина", "малина", null));
        supplier.Products.Add(new SupplierProduct(supplier.Id, "b", "Сыр", "сыр", null));
        AddFact(supplier, "product_name", "a", "Малина", VerificationStatus.Official);
        AddFact(supplier, "product_name", "b", "Сыр", VerificationStatus.Official);
        AddFact(supplier, "category", "a", "Ягоды", VerificationStatus.Official, ObservedAt);
        AddFact(supplier, "category", "b", "Молочные продукты", VerificationStatus.Official,
            ObservedAt.AddMinutes(1));

        var details = SupplierCatalogMapper.ToDetails(supplier);

        Assert.Equal("Ягоды", details.Products.Single(product => product.Name.Value == "Малина").Category.Value);
        Assert.Equal("Молочные продукты", details.Products.Single(product => product.Name.Value == "Сыр").Category.Value);
    }

    [Fact]
    public void HiddenExternalContactDoesNotFlagCardAsUnconfirmed()
    {
        var supplier = CreateSupplier();
        AddFact(supplier, "phone", "main", "+7 900 000-00-01", VerificationStatus.Official);
        AddFact(supplier, "phone", "secondary", "+7 900 000-00-02", VerificationStatus.External);

        var card = SupplierCatalogMapper.ToCard(supplier);

        Assert.Equal("+7 900 000-00-01", card.ContactPreview);
        Assert.False(card.HasUnconfirmedData);
    }

    private static Supplier CreateSupplier()
    {
        var supplier = new Supplier("Пример", SupplierIdentity.NormalizeName("Пример"), ObservedAt);
        var name = AddFact(supplier, "name", "name", "Пример", VerificationStatus.Official);
        supplier.SetCurrentNameFact(name, "Пример");
        return supplier;
    }

    private static SupplierFact AddFact(Supplier supplier, string field, string itemKey, string value,
        VerificationStatus status, DateTimeOffset? observedAt = null)
    {
        var observed = observedAt ?? ObservedAt;
        var json = JsonSerializer.SerializeToElement(value);
        var fact = new SupplierFact(supplier.Id, field, itemKey, json, status, observed, true,
            SupplierFactNormalizer.Normalize(field, json));
        var sourceType = status == VerificationStatus.Official ? SourceType.Official : SourceType.External;
        var source = new SupplierSource(supplier.Id, new Uri($"https://supplier.example/{field}/{itemKey}"),
            "Страница поставщика", value, sourceType, observed);
        fact.AddSource(source);
        supplier.Facts.Add(fact);
        supplier.Sources.Add(source);
        return fact;
    }
}
