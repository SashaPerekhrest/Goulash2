using System.Text.Json;

namespace Goulash.Domain;

public sealed class Supplier
{
    private Supplier() { }

    public Supplier(string name, string normalizedName, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A supplier must have a source-backed name.", nameof(name));
        if (string.IsNullOrWhiteSpace(normalizedName))
            throw new ArgumentException("A normalized supplier name is required.", nameof(normalizedName));
        EnsureUtc(now, nameof(now));

        Id = Guid.CreateVersion7();
        Name = name.Trim();
        NormalizedName = normalizedName;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string NormalizedName { get; private set; } = string.Empty;
    public string? OfficialSiteUrl { get; private set; }
    public string? OfficialDomain { get; private set; }
    public string? City { get; private set; }
    public string? Region { get; private set; }
    public bool IsFavorite { get; private set; }
    public string? Note { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? LastDiscoveredAt { get; private set; }
    public Guid? CurrentNameFactId { get; private set; }
    public SupplierFact? CurrentNameFact { get; private set; }
    public ICollection<SupplierFact> Facts { get; private set; } = new List<SupplierFact>();
    public ICollection<SupplierSource> Sources { get; private set; } = new List<SupplierSource>();
    public ICollection<SupplierProduct> Products { get; private set; } = new List<SupplierProduct>();
    public ICollection<SupplierImage> Images { get; private set; } = new List<SupplierImage>();

    public void SetCurrentNameFact(SupplierFact fact, string name)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (fact.SupplierId != Id || fact.FieldKey != "name")
            throw new InvalidOperationException("The supplier summary name must point to a current name fact.");
        if (!fact.IsCurrent || !fact.FactSources.Any(link => !string.IsNullOrWhiteSpace(link.Source.Excerpt)))
            throw new InvalidOperationException("The current name fact must be supported by a source excerpt.");
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A current name cannot be empty.", nameof(name));
        if (!string.Equals(JsonSerializer.Deserialize<string>(fact.ValueJson)?.Trim(), name.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("The supplier summary name must equal its current name fact value.");

        Name = name.Trim();
        CurrentNameFactId = fact.Id;
        CurrentNameFact = fact;
    }

    public void SetOfficialIdentity(string? siteUrl, string? domain, string? city, string? region)
    {
        OfficialSiteUrl = siteUrl;
        OfficialDomain = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim().ToLowerInvariant();
        City = NormalizeOptional(city);
        Region = NormalizeOptional(region);
    }

    public void SetNormalizedName(string normalizedName)
    {
        if (string.IsNullOrWhiteSpace(normalizedName))
            throw new ArgumentException("A normalized supplier name is required.", nameof(normalizedName));
        NormalizedName = normalizedName;
    }

    public void MarkDiscovered(DateTimeOffset at)
    {
        EnsureUtc(at, nameof(at));
        LastDiscoveredAt = at;
        UpdatedAt = at;
    }

    public void SetFavorite(bool isFavorite) => IsFavorite = isFavorite;

    public void SetNote(string? note)
    {
        if (note?.Length > 2000)
            throw new ArgumentOutOfRangeException(nameof(note), "A note cannot exceed 2000 characters.");
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void EnsureUtc(DateTimeOffset value, string parameter)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("Dates must use UTC.", parameter);
    }
}
