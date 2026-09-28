using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Goulash.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FixLegacySupplierIntegrityTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS supplier_facts_validate_related ON supplier_facts;
                DROP TRIGGER IF EXISTS fact_sources_validate_related ON fact_sources;
                DROP TRIGGER IF EXISTS supplier_sources_validate_related ON supplier_sources;

                CREATE OR REPLACE FUNCTION enforce_supplier_current_name_fact() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    v_name text;
                    v_fact_id uuid;
                BEGIN
                    SELECT name, current_name_fact_id INTO v_name, v_fact_id
                    FROM suppliers WHERE id = NEW.id;
                    IF NOT FOUND THEN RETURN NULL; END IF;

                    IF v_fact_id IS NULL
                       OR length(btrim(v_name)) = 0
                       OR NOT EXISTS (
                           SELECT 1 FROM supplier_facts f
                           WHERE f.id = v_fact_id
                             AND f.supplier_id = NEW.id
                             AND f.field_key = 'name'
                             AND f.is_current
                             AND f.value_json = to_jsonb(v_name)
                       ) THEN
                        RAISE EXCEPTION 'A supplier name must match its current name fact.'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_suppliers_current_name';
                    END IF;
                    RETURN NULL;
                END;
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION enforce_supplier_current_name_fact() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.current_name_fact_id IS NULL
                       OR length(btrim(NEW.name)) = 0
                       OR NOT EXISTS (
                           SELECT 1 FROM supplier_facts f
                           WHERE f.id = NEW.current_name_fact_id
                             AND f.supplier_id = NEW.id
                             AND f.field_key = 'name'
                             AND f.is_current
                             AND f.value_json = to_jsonb(NEW.name)
                       ) THEN
                        RAISE EXCEPTION 'A supplier name must match its current name fact.'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_suppliers_current_name';
                    END IF;
                    RETURN NULL;
                END;
                $$;

                CREATE CONSTRAINT TRIGGER supplier_facts_validate_related
                    AFTER INSERT OR UPDATE OR DELETE ON supplier_facts
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION enforce_related_fact_integrity();
                CREATE CONSTRAINT TRIGGER fact_sources_validate_related
                    AFTER INSERT OR UPDATE OR DELETE ON fact_sources
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION enforce_related_fact_integrity();
                CREATE CONSTRAINT TRIGGER supplier_sources_validate_related
                    AFTER UPDATE OR DELETE ON supplier_sources
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION enforce_related_fact_integrity();
                """);
        }
    }
}
