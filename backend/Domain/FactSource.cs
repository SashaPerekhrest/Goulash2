namespace Goulash.Domain;

public sealed class FactSource
{
    private FactSource() { }

    internal FactSource(Guid factId, Guid sourceId, SupplierFact fact, SupplierSource source)
    {
        FactId = factId;
        SourceId = sourceId;
        Fact = fact;
        Source = source;
    }

    public Guid FactId { get; private set; }
    public Guid SourceId { get; private set; }
    public SupplierFact Fact { get; private set; } = null!;
    public SupplierSource Source { get; private set; } = null!;
}
