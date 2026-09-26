namespace Goulash.Domain;

public sealed class SupplierPrice
{
    private SupplierPrice() { }

    public SupplierPrice(Guid productId, Guid factId, decimal amountMin, decimal amountMax,
        string currency, string unit, bool isApproximate)
    {
        if (productId == Guid.Empty || factId == Guid.Empty) throw new ArgumentException("Product and fact ids are required.");
        if (amountMin < 0 || amountMax < amountMin) throw new ArgumentOutOfRangeException(nameof(amountMin));
        if (currency.Length != 3 || currency.Any(character => !char.IsAsciiLetter(character)))
            throw new ArgumentException("Currency must be a three-letter code.", nameof(currency));
        if (string.IsNullOrWhiteSpace(unit)) throw new ArgumentException("A unit is required.", nameof(unit));

        Id = Guid.CreateVersion7();
        ProductId = productId;
        FactId = factId;
        AmountMin = amountMin;
        AmountMax = amountMax;
        Currency = currency.ToUpperInvariant();
        Unit = unit.Trim().ToLowerInvariant();
        IsApproximate = isApproximate;
    }

    public Guid Id { get; private set; }
    public Guid ProductId { get; private set; }
    public SupplierProduct Product { get; private set; } = null!;
    public Guid FactId { get; private set; }
    public SupplierFact Fact { get; private set; } = null!;
    public decimal AmountMin { get; private set; }
    public decimal AmountMax { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public string Unit { get; private set; } = string.Empty;
    public bool IsApproximate { get; private set; }
}
