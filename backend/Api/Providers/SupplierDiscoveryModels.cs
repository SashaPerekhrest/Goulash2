using System.Text.Json;

namespace Goulash.Api.Providers;

public sealed record SupplierLeadList(IReadOnlyList<SupplierLead>? Suppliers);
public sealed record SupplierLead(string? Name, string? WebsiteUrl);

public sealed class SupplierProfileDraft
{
    public string? Name { get; init; }
    public string? WebsiteUrl { get; init; }
    public string? Description { get; init; }
    public string? Address { get; init; }
    public string? City { get; init; }
    public string? Region { get; init; }
    public JsonElement? ServiceRegions { get; init; }
    public SupplierContactsDraft? Contacts { get; init; }
    public IReadOnlyList<SupplierProductDraft>? Products { get; init; }
    public SupplierDeliveryDraft? Delivery { get; init; }
    public JsonElement? MinimumOrder { get; init; }
    public JsonElement? Certificates { get; init; }
    public JsonElement? Images { get; init; }
}

public sealed class SupplierContactsDraft
{
    public JsonElement? Phones { get; init; }
    public JsonElement? Emails { get; init; }
    public string? Website { get; init; }
}

public sealed class SupplierProductDraft
{
    public string? Key { get; init; }
    public string? Name { get; init; }
    public string? Category { get; init; }
    public IReadOnlyList<JsonElement>? Prices { get; init; }
}

public sealed class SupplierDeliveryDraft
{
    public string? Terms { get; init; }
    public JsonElement? MaxDays { get; init; }
}

public sealed record SupplierSitePage(Uri Url, string Title, string Text, IReadOnlyList<Uri> Links);
