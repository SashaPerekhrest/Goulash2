namespace Goulash.Domain;

public sealed class SupplierProduct
{
    private SupplierProduct() { }

    public SupplierProduct(Guid supplierId, string itemKey, string name, string normalizedName, string? category)
    {
        Id = Guid.CreateVersion7();
        SupplierId = supplierId;
        ItemKey = string.IsNullOrWhiteSpace(itemKey) ? throw new ArgumentException("An item key is required.", nameof(itemKey)) : itemKey.Trim();
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A product name is required.", nameof(name)) : name.Trim();
        NormalizedName = string.IsNullOrWhiteSpace(normalizedName) ? throw new ArgumentException("A normalized product name is required.", nameof(normalizedName)) : normalizedName.Trim();
        Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
    }

    public Guid Id { get; private set; }
    public Guid SupplierId { get; private set; }
    public Supplier Supplier { get; private set; } = null!;
    public string ItemKey { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Category { get; private set; }
    public string NormalizedName { get; private set; } = string.Empty;
    public ICollection<SupplierPrice> Prices { get; private set; } = new List<SupplierPrice>();
}
