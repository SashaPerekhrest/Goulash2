using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Goulash.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_provider_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    encrypted_api_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_provider_settings", x => x.id);
                    table.CheckConstraint("ck_ai_provider_settings_singleton", "id = '00000000-0000-7000-8000-000000000001'");
                });

            migrationBuilder.CreateTable(
                name: "discovery_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    query_json = table.Column<string>(type: "jsonb", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    accepted_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    rejected_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    error_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discovery_runs", x => x.id);
                    table.CheckConstraint("ck_discovery_runs_counts", "accepted_count >= 0 AND rejected_count >= 0");
                    table.CheckConstraint("ck_discovery_runs_status", "status IN ('running', 'succeeded', 'failed')");
                });

            migrationBuilder.CreateTable(
                name: "fact_sources",
                columns: table => new
                {
                    fact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fact_sources", x => new { x.fact_id, x.source_id });
                });

            migrationBuilder.CreateTable(
                name: "supplier_facts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    item_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    value_json = table.Column<string>(type: "jsonb", nullable: false),
                    verification_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_facts", x => x.id);
                    table.UniqueConstraint("ak_supplier_facts_supplier_id_id", x => new { x.supplier_id, x.id });
                    table.CheckConstraint("ck_supplier_facts_status", "verification_status IN ('official', 'external')");
                    table.CheckConstraint("ck_supplier_facts_value_object", "jsonb_typeof(value_json) <> 'null'");
                });

            migrationBuilder.CreateTable(
                name: "suppliers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    official_site_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    official_domain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    city = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    region = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    is_favorite = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_discovered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    current_name_fact_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_suppliers", x => x.id);
                    table.CheckConstraint("ck_suppliers_name_nonempty", "length(btrim(name)) > 0");
                    table.CheckConstraint("ck_suppliers_official_domain_normalized", "official_domain IS NULL OR (official_domain = lower(btrim(official_domain)) AND official_domain <> '')");
                    table.ForeignKey(
                        name: "fk_suppliers_current_name_fact",
                        columns: x => new { x.id, x.current_name_fact_id },
                        principalTable: "supplier_facts",
                        principalColumns: new[] { "supplier_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_images",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    fact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_images", x => x.id);
                    table.CheckConstraint("ck_supplier_images_http_url", "url ~* '^https?://'");
                    table.CheckConstraint("ck_supplier_images_sort_order", "sort_order >= 0");
                    table.ForeignKey(
                        name: "fk_supplier_images_fact",
                        column: x => x.fact_id,
                        principalTable: "supplier_facts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supplier_images_supplier",
                        column: x => x.supplier_id,
                        principalTable: "suppliers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "supplier_products",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    category = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    normalized_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_products", x => x.id);
                    table.CheckConstraint("ck_supplier_products_name_nonempty", "length(btrim(name)) > 0");
                    table.ForeignKey(
                        name: "fk_supplier_products_supplier",
                        column: x => x.supplier_id,
                        principalTable: "suppliers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "supplier_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    excerpt = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    source_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    retrieved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_sources", x => x.id);
                    table.CheckConstraint("ck_supplier_sources_excerpt_nonempty", "length(btrim(excerpt)) > 0");
                    table.CheckConstraint("ck_supplier_sources_http_url", "url ~* '^https?://' AND length(btrim(host)) > 0");
                    table.CheckConstraint("ck_supplier_sources_type", "source_type IN ('official', 'external')");
                    table.ForeignKey(
                        name: "fk_supplier_sources_supplier",
                        column: x => x.supplier_id,
                        principalTable: "suppliers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "supplier_prices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_min = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    amount_max = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    unit = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    is_approximate = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_prices", x => x.id);
                    table.CheckConstraint("ck_supplier_prices_amount_range", "amount_min >= 0 AND amount_max >= amount_min");
                    table.CheckConstraint("ck_supplier_prices_currency", "currency ~ '^[A-Z]{3}$'");
                    table.ForeignKey(
                        name: "fk_supplier_prices_fact",
                        column: x => x.fact_id,
                        principalTable: "supplier_facts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supplier_prices_product",
                        column: x => x.product_id,
                        principalTable: "supplier_products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_discovery_runs_started_at",
                table: "discovery_runs",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_fact_sources_source_id",
                table: "fact_sources",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "ix_supplier_facts_history",
                table: "supplier_facts",
                columns: new[] { "supplier_id", "field_key", "observed_at" });

            migrationBuilder.CreateIndex(
                name: "ux_supplier_facts_current_item",
                table: "supplier_facts",
                columns: new[] { "supplier_id", "field_key", "item_key" },
                unique: true,
                filter: "is_current");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_images_fact_id",
                table: "supplier_images",
                column: "fact_id");

            migrationBuilder.CreateIndex(
                name: "ix_supplier_images_supplier_sort",
                table: "supplier_images",
                columns: new[] { "supplier_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_prices_comparison",
                table: "supplier_prices",
                columns: new[] { "currency", "unit", "amount_min", "amount_max" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_prices_product_id",
                table: "supplier_prices",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ux_supplier_prices_fact",
                table: "supplier_prices",
                column: "fact_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_supplier_products_category",
                table: "supplier_products",
                column: "category");

            migrationBuilder.CreateIndex(
                name: "ix_supplier_products_normalized_name",
                table: "supplier_products",
                column: "normalized_name");

            migrationBuilder.CreateIndex(
                name: "ux_supplier_products_supplier_item_key",
                table: "supplier_products",
                columns: new[] { "supplier_id", "item_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_supplier_sources_supplier_url",
                table: "supplier_sources",
                columns: new[] { "supplier_id", "url" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_suppliers_city",
                table: "suppliers",
                column: "city");

            migrationBuilder.CreateIndex(
                name: "ix_suppliers_created_at",
                table: "suppliers",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_suppliers_id_current_name_fact_id",
                table: "suppliers",
                columns: new[] { "id", "current_name_fact_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_suppliers_is_favorite",
                table: "suppliers",
                column: "is_favorite");

            migrationBuilder.CreateIndex(
                name: "ix_suppliers_normalized_name_region",
                table: "suppliers",
                columns: new[] { "normalized_name", "region" });

            migrationBuilder.CreateIndex(
                name: "ix_suppliers_undomained_name_region",
                table: "suppliers",
                columns: new[] { "normalized_name", "region" },
                filter: "official_domain IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_suppliers_official_domain",
                table: "suppliers",
                column: "official_domain",
                unique: true,
                filter: "official_domain IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_fact_sources_fact",
                table: "fact_sources",
                column: "fact_id",
                principalTable: "supplier_facts",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_fact_sources_source",
                table: "fact_sources",
                column: "source_id",
                principalTable: "supplier_sources",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_supplier_facts_supplier",
                table: "supplier_facts",
                column: "supplier_id",
                principalTable: "suppliers",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.Sql("""
                CREATE FUNCTION enforce_fact_has_source() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE target_fact_id uuid;
                BEGIN
                    IF TG_TABLE_NAME = 'supplier_facts' THEN
                        target_fact_id := NEW.id;
                    ELSE
                        target_fact_id := COALESCE(NEW.fact_id, OLD.fact_id);
                    END IF;

                    IF EXISTS (SELECT 1 FROM supplier_facts WHERE id = target_fact_id)
                       AND NOT EXISTS (
                           SELECT 1
                           FROM supplier_facts f
                           JOIN fact_sources fs ON fs.fact_id = f.id
                           JOIN supplier_sources s ON s.id = fs.source_id
                           WHERE f.id = target_fact_id
                             AND f.supplier_id = s.supplier_id
                             AND length(btrim(s.excerpt)) > 0
                             AND ((f.verification_status = 'official' AND s.source_type = 'official')
                               OR (f.verification_status = 'external' AND s.source_type = 'external'))
                       ) THEN
                        RAISE EXCEPTION 'A supplier fact must have a matching source with an excerpt.'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_supplier_facts_has_source';
                    END IF;
                    RETURN NULL;
                END;
                $$;

                CREATE CONSTRAINT TRIGGER supplier_facts_require_source
                    AFTER INSERT OR UPDATE ON supplier_facts
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION enforce_fact_has_source();

                CREATE CONSTRAINT TRIGGER fact_sources_require_source
                    AFTER INSERT OR UPDATE OR DELETE ON fact_sources
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION enforce_fact_has_source();

                CREATE FUNCTION enforce_supplier_current_name_fact() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.current_name_fact_id IS NULL
                       OR length(btrim(NEW.name)) = 0
                       OR NOT EXISTS (
                           SELECT 1
                           FROM supplier_facts f
                           JOIN fact_sources fs ON fs.fact_id = f.id
                           JOIN supplier_sources s ON s.id = fs.source_id
                           WHERE f.id = NEW.current_name_fact_id
                             AND f.supplier_id = NEW.id
                             AND f.field_key = 'name'
                             AND f.is_current
                             AND f.value_json = to_jsonb(NEW.name)
                             AND s.supplier_id = f.supplier_id
                             AND length(btrim(s.excerpt)) > 0
                             AND ((f.verification_status = 'official' AND s.source_type = 'official')
                               OR (f.verification_status = 'external' AND s.source_type = 'external'))
                       ) THEN
                        RAISE EXCEPTION 'A supplier name must match its current sourced name fact.'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_suppliers_current_sourced_name';
                    END IF;
                    RETURN NULL;
                END;
                $$;

                CREATE CONSTRAINT TRIGGER supplier_requires_current_name_fact
                    AFTER INSERT OR UPDATE ON suppliers
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION enforce_supplier_current_name_fact();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS supplier_requires_current_name_fact ON suppliers;
                DROP TRIGGER IF EXISTS fact_sources_require_source ON fact_sources;
                DROP TRIGGER IF EXISTS supplier_facts_require_source ON supplier_facts;
                DROP FUNCTION IF EXISTS enforce_supplier_current_name_fact();
                DROP FUNCTION IF EXISTS enforce_fact_has_source();
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_suppliers_current_name_fact",
                table: "suppliers");

            migrationBuilder.DropTable(
                name: "ai_provider_settings");

            migrationBuilder.DropTable(
                name: "discovery_runs");

            migrationBuilder.DropTable(
                name: "fact_sources");

            migrationBuilder.DropTable(
                name: "supplier_images");

            migrationBuilder.DropTable(
                name: "supplier_prices");

            migrationBuilder.DropTable(
                name: "supplier_sources");

            migrationBuilder.DropTable(
                name: "supplier_products");

            migrationBuilder.DropTable(
                name: "supplier_facts");

            migrationBuilder.DropTable(
                name: "suppliers");
        }
    }
}
