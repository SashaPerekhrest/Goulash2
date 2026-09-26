namespace Goulash.Domain;

public sealed class SupplierSource
{
    private SupplierSource() { }

    public SupplierSource(Guid supplierId, Uri url, string? title, string excerpt,
        SourceType type, DateTimeOffset retrievedAt)
    {
        if (supplierId == Guid.Empty) throw new ArgumentException("A supplier id is required.", nameof(supplierId));
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(url.Host))
            throw new ArgumentException("Source URL must be an absolute HTTP or HTTPS URL.", nameof(url));
        if (string.IsNullOrWhiteSpace(excerpt))
            throw new ArgumentException("A source excerpt is required to support a fact.", nameof(excerpt));
        if (retrievedAt.Offset != TimeSpan.Zero) throw new ArgumentException("Dates must use UTC.", nameof(retrievedAt));

        Id = Guid.CreateVersion7();
        SupplierId = supplierId;
        Url = url.AbsoluteUri;
        Host = url.IdnHost.ToLowerInvariant();
        Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        Excerpt = excerpt.Trim();
        Type = type;
        RetrievedAt = retrievedAt;
    }

    public Guid Id { get; private set; }
    public Guid SupplierId { get; private set; }
    public Supplier Supplier { get; private set; } = null!;
    public string Url { get; private set; } = string.Empty;
    public string Host { get; private set; } = string.Empty;
    public string? Title { get; private set; }
    public string Excerpt { get; private set; } = string.Empty;
    public SourceType Type { get; private set; }
    public DateTimeOffset RetrievedAt { get; private set; }
    public ICollection<FactSource> FactSources { get; private set; } = new List<FactSource>();
}
