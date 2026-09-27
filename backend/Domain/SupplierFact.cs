using System.Text.Json;

namespace Goulash.Domain;

public sealed class SupplierFact
{
    private SupplierFact() { }

    public SupplierFact(Guid supplierId, string fieldKey, string itemKey, JsonElement value,
        VerificationStatus status, DateTimeOffset observedAt, bool isCurrent, JsonElement? normalizedValue = null)
    {
        if (supplierId == Guid.Empty) throw new ArgumentException("A supplier id is required.", nameof(supplierId));
        if (string.IsNullOrWhiteSpace(fieldKey)) throw new ArgumentException("A fact field is required.", nameof(fieldKey));
        if (fieldKey.Length > 80) throw new ArgumentOutOfRangeException(nameof(fieldKey));
        if (itemKey is null || itemKey.Length > 200) throw new ArgumentOutOfRangeException(nameof(itemKey));
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new ArgumentException("Missing values are represented by the absence of a fact.", nameof(value));
        if (observedAt.Offset != TimeSpan.Zero) throw new ArgumentException("Dates must use UTC.", nameof(observedAt));

        Id = Guid.CreateVersion7();
        SupplierId = supplierId;
        FieldKey = fieldKey.Trim().ToLowerInvariant();
        ItemKey = itemKey.Trim();
        ValueJson = value.GetRawText();
        NormalizedValueJson = (normalizedValue ?? value).GetRawText();
        Status = status;
        ObservedAt = observedAt;
        IsCurrent = isCurrent;
    }

    public Guid Id { get; private set; }
    public Guid SupplierId { get; private set; }
    public Supplier Supplier { get; private set; } = null!;
    public string FieldKey { get; private set; } = string.Empty;
    public string ItemKey { get; private set; } = string.Empty;
    public string ValueJson { get; private set; } = "null";
    public string NormalizedValueJson { get; private set; } = "null";
    public VerificationStatus Status { get; private set; }
    public DateTimeOffset ObservedAt { get; private set; }
    public bool IsCurrent { get; private set; }
    public ICollection<FactSource> FactSources { get; private set; } = new List<FactSource>();
    public ICollection<SupplierPrice> Prices { get; private set; } = new List<SupplierPrice>();
    public ICollection<SupplierImage> Images { get; private set; } = new List<SupplierImage>();

    public void AddSource(SupplierSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SupplierId != SupplierId)
            throw new InvalidOperationException("A fact and its source must belong to the same supplier.");
        if (FactSources.Any(link => link.SourceId == source.Id)) return;
        FactSources.Add(new FactSource(Id, source.Id, this, source));
    }

    public void SelectAsCurrent() => IsCurrent = true;
    public void SelectAsAlternative() => IsCurrent = false;

    public void RefreshObservedAt(DateTimeOffset observedAt)
    {
        if (observedAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Dates must use UTC.", nameof(observedAt));
        ObservedAt = observedAt;
    }

    public void RefreshNormalizedValue(JsonElement normalizedValue)
    {
        if (normalizedValue.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new ArgumentException("A normalized fact value is required.", nameof(normalizedValue));
        NormalizedValueJson = normalizedValue.GetRawText();
    }
}
