namespace Goulash.Domain;

public sealed class SupplierImage
{
    private SupplierImage() { }

    public SupplierImage(Guid supplierId, string url, Guid factId, int sortOrder)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Image URL must be an absolute HTTP or HTTPS URL.", nameof(url));
        if (sortOrder < 0) throw new ArgumentOutOfRangeException(nameof(sortOrder));

        Id = Guid.CreateVersion7();
        SupplierId = supplierId;
        Url = uri.AbsoluteUri;
        FactId = factId;
        SortOrder = sortOrder;
    }

    public Guid Id { get; private set; }
    public Guid SupplierId { get; private set; }
    public Supplier Supplier { get; private set; } = null!;
    public string Url { get; private set; } = string.Empty;
    public Guid FactId { get; private set; }
    public SupplierFact Fact { get; private set; } = null!;
    public int SortOrder { get; private set; }
}
