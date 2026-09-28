using Goulash.Application;
using Goulash.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Goulash.Infrastructure.Persistence;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<SupplierFact> SupplierFacts => Set<SupplierFact>();
    public DbSet<SupplierSource> SupplierSources => Set<SupplierSource>();
    public DbSet<FactSource> FactSources => Set<FactSource>();
    public DbSet<SupplierProduct> SupplierProducts => Set<SupplierProduct>();
    public DbSet<SupplierPrice> SupplierPrices => Set<SupplierPrice>();
    public DbSet<SupplierImage> SupplierImages => Set<SupplierImage>();
    public DbSet<DiscoveryRun> DiscoveryRuns => Set<DiscoveryRun>();
    public DbSet<AiProviderSetting> AiProviderSettings => Set<AiProviderSetting>();
    public DbSet<AiProviderPromptSetting> AiProviderPromptSettings => Set<AiProviderPromptSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureSuppliers(modelBuilder);
        ConfigureFacts(modelBuilder);
        ConfigureSources(modelBuilder);
        ConfigureProducts(modelBuilder);
        ConfigurePrices(modelBuilder);
        ConfigureImages(modelBuilder);
        ConfigureDiscoveryRuns(modelBuilder);
        ConfigureProviderSettings(modelBuilder);
        ConfigureProviderPrompts(modelBuilder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        NormalizeDatesToUtc();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        NormalizeDatesToUtc();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void NormalizeDatesToUtc()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            foreach (var property in entry.Properties)
            {
                if (property.CurrentValue is DateTimeOffset value)
                    property.CurrentValue = value.ToUniversalTime();
            }
        }
    }

    private static void ConfigureSuppliers(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Supplier>();
        entity.ToTable("suppliers", table =>
        {
            table.HasCheckConstraint("ck_suppliers_name_nonempty", "length(btrim(name)) > 0");
            table.HasCheckConstraint("ck_suppliers_official_domain_normalized", "official_domain IS NULL OR (official_domain = lower(btrim(official_domain)) AND official_domain <> '')");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.Id).HasColumnName("id").HasColumnType("uuid");
        entity.Property(item => item.Name).HasColumnName("name").HasMaxLength(300).IsRequired();
        entity.Property(item => item.NormalizedName).HasColumnName("normalized_name").HasMaxLength(300).IsRequired();
        entity.Property(item => item.OfficialSiteUrl).HasColumnName("official_site_url").HasMaxLength(2048);
        entity.Property(item => item.OfficialDomain).HasColumnName("official_domain").HasMaxLength(255);
        entity.Property(item => item.City).HasColumnName("city").HasMaxLength(160);
        entity.Property(item => item.Region).HasColumnName("region").HasMaxLength(160);
        entity.Property(item => item.IsFavorite).HasColumnName("is_favorite").HasDefaultValue(false);
        entity.Property(item => item.Note).HasColumnName("note").HasMaxLength(2000);
        entity.Property(item => item.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        entity.Property(item => item.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone");
        entity.Property(item => item.LastDiscoveredAt).HasColumnName("last_discovered_at").HasColumnType("timestamp with time zone");
        entity.Property(item => item.CurrentNameFactId).HasColumnName("current_name_fact_id").HasColumnType("uuid");
        // The name fact is validated by a deferred database trigger. Mapping this
        // navigation as an FK creates an insert cycle with supplier_facts.supplier_id.
        entity.Ignore(item => item.CurrentNameFact);

        entity.HasIndex(item => item.OfficialDomain).IsUnique().HasFilter("official_domain IS NOT NULL").HasDatabaseName("ux_suppliers_official_domain");
        entity.HasIndex(item => new { item.NormalizedName, item.Region }, "normalized_name_region")
            .HasDatabaseName("ix_suppliers_normalized_name_region");
        entity.HasIndex(item => new { item.NormalizedName, item.Region }, "undomained_normalized_name_region")
            .HasFilter("official_domain IS NULL")
            .HasDatabaseName("ix_suppliers_undomained_name_region");
        entity.HasIndex(item => item.CreatedAt).HasDatabaseName("ix_suppliers_created_at");
        entity.HasIndex(item => item.IsFavorite).HasDatabaseName("ix_suppliers_is_favorite");
        entity.HasIndex(item => item.City).HasDatabaseName("ix_suppliers_city");

    }

    private static void ConfigureFacts(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SupplierFact>();
        entity.ToTable("supplier_facts", table =>
        {
            table.HasCheckConstraint("ck_supplier_facts_status", "verification_status IN ('official', 'external', 'ai_generated')");
            table.HasCheckConstraint("ck_supplier_facts_value_object", "jsonb_typeof(value_json) <> 'null'");
        });
        entity.HasKey(item => item.Id);
        entity.HasAlternateKey(item => new { item.SupplierId, item.Id }).HasName("ak_supplier_facts_supplier_id_id");
        entity.Property(item => item.Id).HasColumnName("id").HasColumnType("uuid");
        entity.Property(item => item.SupplierId).HasColumnName("supplier_id").HasColumnType("uuid");
        entity.Property(item => item.FieldKey).HasColumnName("field_key").HasMaxLength(80).IsRequired();
        entity.Property(item => item.ItemKey).HasColumnName("item_key").HasMaxLength(200).IsRequired();
        entity.Property(item => item.ValueJson).HasColumnName("value_json").HasColumnType("jsonb").IsRequired();
        entity.Property(item => item.NormalizedValueJson).HasColumnName("normalized_value_json").HasColumnType("jsonb").IsRequired();
        entity.Property(item => item.Status).HasColumnName("verification_status").HasConversion(StatusConverter).HasMaxLength(16).IsRequired();
        entity.Property(item => item.ObservedAt).HasColumnName("observed_at").HasColumnType("timestamp with time zone");
        entity.Property(item => item.IsCurrent).HasColumnName("is_current").HasDefaultValue(true);

        entity.HasOne(item => item.Supplier).WithMany(item => item.Facts)
            .HasForeignKey(item => item.SupplierId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_supplier_facts_supplier");
        entity.HasIndex(item => new { item.SupplierId, item.FieldKey, item.ItemKey })
            .IsUnique().HasFilter("is_current").HasDatabaseName("ux_supplier_facts_current_item");
        entity.HasIndex(item => new { item.SupplierId, item.FieldKey, item.ObservedAt })
            .HasDatabaseName("ix_supplier_facts_history");
    }

    private static void ConfigureSources(ModelBuilder modelBuilder)
    {
        var source = modelBuilder.Entity<SupplierSource>();
        source.ToTable("supplier_sources", table =>
        {
            table.HasCheckConstraint("ck_supplier_sources_http_url", "url ~* '^https?://' AND length(btrim(host)) > 0");
            table.HasCheckConstraint("ck_supplier_sources_excerpt_nonempty", "length(btrim(excerpt)) > 0");
            table.HasCheckConstraint("ck_supplier_sources_type", "source_type IN ('official', 'external')");
        });
        source.HasKey(item => item.Id);
        source.Property(item => item.Id).HasColumnName("id").HasColumnType("uuid");
        source.Property(item => item.SupplierId).HasColumnName("supplier_id").HasColumnType("uuid");
        source.Property(item => item.Url).HasColumnName("url").HasMaxLength(2048).IsRequired();
        source.Property(item => item.Host).HasColumnName("host").HasMaxLength(255).IsRequired();
        source.Property(item => item.Title).HasColumnName("title").HasMaxLength(500);
        source.Property(item => item.Excerpt).HasColumnName("excerpt").HasMaxLength(2000).IsRequired();
        source.Property(item => item.Type).HasColumnName("source_type").HasConversion(SourceTypeConverter).HasMaxLength(16).IsRequired();
        source.Property(item => item.RetrievedAt).HasColumnName("retrieved_at").HasColumnType("timestamp with time zone");
        source.HasOne(item => item.Supplier).WithMany(item => item.Sources)
            .HasForeignKey(item => item.SupplierId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_supplier_sources_supplier");
        source.HasIndex(item => new { item.SupplierId, item.Url }).HasDatabaseName("ix_supplier_sources_supplier_url");

        var link = modelBuilder.Entity<FactSource>();
        link.ToTable("fact_sources");
        link.HasKey(item => new { item.FactId, item.SourceId });
        link.Property(item => item.FactId).HasColumnName("fact_id").HasColumnType("uuid");
        link.Property(item => item.SourceId).HasColumnName("source_id").HasColumnType("uuid");
        link.HasOne(item => item.Fact).WithMany(item => item.FactSources)
            .HasForeignKey(item => item.FactId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_fact_sources_fact");
        link.HasOne(item => item.Source).WithMany(item => item.FactSources)
            .HasForeignKey(item => item.SourceId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_fact_sources_source");
        link.HasIndex(item => item.SourceId).HasDatabaseName("ix_fact_sources_source_id");
    }

    private static void ConfigureProducts(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SupplierProduct>();
        entity.ToTable("supplier_products", table => table.HasCheckConstraint("ck_supplier_products_name_nonempty", "length(btrim(name)) > 0"));
        entity.HasKey(item => item.Id);
        entity.Property(item => item.Id).HasColumnName("id").HasColumnType("uuid");
        entity.Property(item => item.SupplierId).HasColumnName("supplier_id").HasColumnType("uuid");
        entity.Property(item => item.ItemKey).HasColumnName("item_key").HasMaxLength(200).IsRequired();
        entity.Property(item => item.Name).HasColumnName("name").HasMaxLength(300).IsRequired();
        entity.Property(item => item.Category).HasColumnName("category").HasMaxLength(160);
        entity.Property(item => item.NormalizedName).HasColumnName("normalized_name").HasMaxLength(300).IsRequired();
        entity.HasOne(item => item.Supplier).WithMany(item => item.Products)
            .HasForeignKey(item => item.SupplierId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_supplier_products_supplier");
        entity.HasIndex(item => new { item.SupplierId, item.ItemKey }).IsUnique().HasDatabaseName("ux_supplier_products_supplier_item_key");
        entity.HasIndex(item => item.Category).HasDatabaseName("ix_supplier_products_category");
        entity.HasIndex(item => item.NormalizedName).HasDatabaseName("ix_supplier_products_normalized_name");
    }

    private static void ConfigurePrices(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SupplierPrice>();
        entity.ToTable("supplier_prices", table =>
        {
            table.HasCheckConstraint("ck_supplier_prices_amount_range", "amount_min >= 0 AND amount_max >= amount_min");
            table.HasCheckConstraint("ck_supplier_prices_currency", "currency ~ '^[A-Z]{3}$'");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.Id).HasColumnName("id").HasColumnType("uuid");
        entity.Property(item => item.ProductId).HasColumnName("product_id").HasColumnType("uuid");
        entity.Property(item => item.FactId).HasColumnName("fact_id").HasColumnType("uuid");
        entity.Property(item => item.AmountMin).HasColumnName("amount_min").HasPrecision(18, 4);
        entity.Property(item => item.AmountMax).HasColumnName("amount_max").HasPrecision(18, 4);
        entity.Property(item => item.Currency).HasColumnName("currency").HasMaxLength(3).IsFixedLength().IsRequired();
        entity.Property(item => item.Unit).HasColumnName("unit").HasMaxLength(40).IsRequired();
        entity.Property(item => item.IsApproximate).HasColumnName("is_approximate").HasDefaultValue(false);
        entity.HasOne(item => item.Product).WithMany(item => item.Prices)
            .HasForeignKey(item => item.ProductId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_supplier_prices_product");
        entity.HasOne(item => item.Fact).WithMany(item => item.Prices)
            .HasForeignKey(item => item.FactId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_supplier_prices_fact");
        entity.HasIndex(item => new { item.Currency, item.Unit, item.AmountMin, item.AmountMax })
            .HasDatabaseName("ix_supplier_prices_comparison");
        entity.HasIndex(item => item.FactId).IsUnique().HasDatabaseName("ux_supplier_prices_fact");
    }

    private static void ConfigureImages(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SupplierImage>();
        entity.ToTable("supplier_images", table =>
        {
            table.HasCheckConstraint("ck_supplier_images_http_url", "url ~* '^https?://'");
            table.HasCheckConstraint("ck_supplier_images_sort_order", "sort_order >= 0");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.Id).HasColumnName("id").HasColumnType("uuid");
        entity.Property(item => item.SupplierId).HasColumnName("supplier_id").HasColumnType("uuid");
        entity.Property(item => item.Url).HasColumnName("url").HasMaxLength(2048).IsRequired();
        entity.Property(item => item.FactId).HasColumnName("fact_id").HasColumnType("uuid");
        entity.Property(item => item.SortOrder).HasColumnName("sort_order");
        entity.HasOne(item => item.Supplier).WithMany(item => item.Images)
            .HasForeignKey(item => item.SupplierId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_supplier_images_supplier");
        entity.HasOne(item => item.Fact).WithMany(item => item.Images)
            .HasForeignKey(item => item.FactId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_supplier_images_fact");
        entity.HasIndex(item => new { item.SupplierId, item.SortOrder }).HasDatabaseName("ix_supplier_images_supplier_sort");
    }

    private static void ConfigureDiscoveryRuns(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<DiscoveryRun>();
        entity.ToTable("discovery_runs", table =>
        {
            table.HasCheckConstraint("ck_discovery_runs_status", "status IN ('queued', 'running', 'succeeded', 'failed', 'cancelled')");
            table.HasCheckConstraint("ck_discovery_runs_counts", "accepted_count >= 0 AND failed_profile_count >= 0");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.Id).HasColumnName("id").HasColumnType("uuid");
        entity.Property(item => item.QueryJson).HasColumnName("query_json").HasColumnType("jsonb").IsRequired();
        entity.Property(item => item.StartedAt).HasColumnName("started_at").HasColumnType("timestamp with time zone");
        entity.Property(item => item.FinishedAt).HasColumnName("finished_at").HasColumnType("timestamp with time zone");
        entity.Property(item => item.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
        entity.Property(item => item.AcceptedCount).HasColumnName("accepted_count").HasDefaultValue(0);
        entity.Property(item => item.FailedProfileCount).HasColumnName("failed_profile_count").HasDefaultValue(0);
        entity.Property(item => item.ErrorCode).HasColumnName("error_code").HasMaxLength(80);
        entity.Property(item => item.Outcome).HasColumnName("outcome").HasMaxLength(40);
        entity.Property(item => item.Stage).HasColumnName("stage").HasMaxLength(40).IsRequired();
        entity.Property(item => item.CandidateCount).HasColumnName("candidate_count").HasDefaultValue(0);
        entity.Property(item => item.CompletedCandidates).HasColumnName("completed_candidates").HasDefaultValue(0);
        entity.Property(item => item.ResultJson).HasColumnName("result_json").HasColumnType("jsonb");
        entity.HasIndex(item => item.StartedAt).HasDatabaseName("ix_discovery_runs_started_at");
    }

    private static void ConfigureProviderSettings(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<AiProviderSetting>();
        entity.ToTable("ai_provider_settings", table =>
            table.HasCheckConstraint("ck_ai_provider_settings_singleton", $"id = '{AiProviderSetting.SingletonId}'"));
        entity.HasKey(item => item.Id);
        entity.Property(item => item.Id).HasColumnName("id").HasColumnType("uuid");
        entity.Property(item => item.ProviderId).HasColumnName("provider_id").HasMaxLength(120).IsRequired();
        entity.Property(item => item.Model).HasColumnName("model").HasMaxLength(200).IsRequired();
        entity.Property(item => item.RouteProvider).HasColumnName("route_provider").HasMaxLength(120);
        entity.Property(item => item.EncryptedApiKey).HasColumnName("encrypted_api_key").HasColumnType("bytea");
        entity.Property(item => item.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone");
    }

    private static void ConfigureProviderPrompts(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<AiProviderPromptSetting>();
        entity.ToTable("ai_provider_prompts");
        entity.HasKey(item => item.ProviderId);
        entity.Property(item => item.ProviderId).HasColumnName("provider_id").HasMaxLength(120);
        entity.Property(item => item.Prompt).HasColumnName("prompt").HasColumnType("text").IsRequired();
        entity.Property(item => item.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone");
        entity.HasData(
            new AiProviderPromptSetting("perplexity", AiProviderPromptDefaults.Perplexity.Replace("\r\n", "\n", StringComparison.Ordinal),
                new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)),
            new AiProviderPromptSetting("polza", AiProviderPromptDefaults.Polza.Replace("\r\n", "\n", StringComparison.Ordinal),
                new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    private static readonly ValueConverter<VerificationStatus, string> StatusConverter = new(
        value => value == VerificationStatus.Official ? "official" :
            value == VerificationStatus.External ? "external" : "ai_generated",
        value => value == "official" ? VerificationStatus.Official :
            value == "external" ? VerificationStatus.External : VerificationStatus.AiGenerated);

    private static readonly ValueConverter<SourceType, string> SourceTypeConverter = new(
        value => value == SourceType.Official ? "official" : "external",
        value => value == "official" ? SourceType.Official : SourceType.External);
}
