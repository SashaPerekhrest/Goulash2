using Goulash.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Goulash.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260927000000_RepairFactIntegrity")]
public sealed class RepairFactIntegrity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey("fk_suppliers_current_name_fact", "suppliers");
        migrationBuilder.DropIndex("IX_suppliers_id_current_name_fact_id", "suppliers");

        // Keep referential integrity, but permit inserting the supplier and its
        // name fact in either order within one transaction.
        migrationBuilder.Sql("""
            ALTER TABLE suppliers ADD CONSTRAINT fk_suppliers_current_name_fact
                FOREIGN KEY (id, current_name_fact_id)
                REFERENCES supplier_facts (supplier_id, id)
                DEFERRABLE INITIALLY DEFERRED;

            DROP TRIGGER supplier_requires_current_name_fact ON suppliers;
            DROP FUNCTION enforce_supplier_current_name_fact();

            CREATE FUNCTION validate_current_supplier_name(p_supplier_id uuid) RETURNS void
            LANGUAGE plpgsql AS $$
            DECLARE v_name text;
                    v_fact_id uuid;
            BEGIN
                SELECT name, current_name_fact_id INTO v_name, v_fact_id
                FROM suppliers WHERE id = p_supplier_id;
                IF NOT FOUND THEN RETURN; END IF;

                IF v_fact_id IS NULL OR NOT EXISTS (
                    SELECT 1 FROM supplier_facts f
                    JOIN fact_sources fs ON fs.fact_id = f.id
                    JOIN supplier_sources src ON src.id = fs.source_id
                    WHERE f.id = v_fact_id
                      AND f.supplier_id = p_supplier_id
                      AND f.field_key = 'name'
                      AND f.is_current
                      AND f.value_json = to_jsonb(v_name)
                      AND src.supplier_id = p_supplier_id
                      AND length(btrim(src.excerpt)) > 0
                      AND ((f.verification_status = 'official' AND src.source_type = 'official')
                        OR (f.verification_status = 'external' AND src.source_type = 'external'))
                ) THEN
                    RAISE EXCEPTION 'A supplier name must match its current sourced name fact.'
                        USING ERRCODE = '23514', CONSTRAINT = 'ck_suppliers_current_sourced_name';
                END IF;
            END;
            $$;

            CREATE FUNCTION enforce_supplier_current_name_fact() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
                -- Fetch the final row at commit, rather than the INSERT event's
                -- old NEW record (which may have a temporarily null fact id).
                PERFORM validate_current_supplier_name(NEW.id);
                RETURN NULL;
            END;
            $$;

            CREATE CONSTRAINT TRIGGER supplier_requires_current_name_fact
                AFTER INSERT OR UPDATE ON suppliers
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION enforce_supplier_current_name_fact();

            CREATE FUNCTION validate_fact_evidence(p_fact_id uuid) RETURNS void
            LANGUAGE plpgsql AS $$
            DECLARE v_supplier_id uuid;
                    v_status text;
            BEGIN
                SELECT supplier_id, verification_status INTO v_supplier_id, v_status
                FROM supplier_facts WHERE id = p_fact_id;
                IF NOT FOUND THEN RETURN; END IF;

                IF EXISTS (
                    SELECT 1 FROM fact_sources fs
                    JOIN supplier_sources src ON src.id = fs.source_id
                    WHERE fs.fact_id = p_fact_id AND src.supplier_id <> v_supplier_id
                ) THEN
                    RAISE EXCEPTION 'A fact and every linked source must belong to the same supplier.'
                        USING ERRCODE = '23514', CONSTRAINT = 'ck_fact_sources_same_supplier';
                END IF;

                IF NOT EXISTS (
                    SELECT 1 FROM fact_sources fs
                    JOIN supplier_sources src ON src.id = fs.source_id
                    WHERE fs.fact_id = p_fact_id
                      AND src.supplier_id = v_supplier_id
                      AND length(btrim(src.excerpt)) > 0
                      AND src.source_type = v_status
                ) THEN
                    RAISE EXCEPTION 'A supplier fact must have a matching source with an excerpt.'
                        USING ERRCODE = '23514', CONSTRAINT = 'ck_supplier_facts_has_source';
                END IF;

                PERFORM validate_current_supplier_name(v_supplier_id);
            END;
            $$;

            CREATE FUNCTION enforce_related_fact_integrity() RETURNS trigger
            LANGUAGE plpgsql AS $$
            DECLARE v_fact_id uuid;
            BEGIN
                IF TG_TABLE_NAME = 'supplier_facts' THEN
                    IF TG_OP <> 'INSERT' THEN
                        PERFORM validate_fact_evidence(OLD.id);
                        PERFORM validate_current_supplier_name(OLD.supplier_id);
                    END IF;
                    IF TG_OP <> 'DELETE' THEN
                        PERFORM validate_fact_evidence(NEW.id);
                    END IF;
                ELSIF TG_TABLE_NAME = 'fact_sources' THEN
                    IF TG_OP <> 'INSERT' THEN
                        PERFORM validate_fact_evidence(OLD.fact_id);
                    END IF;
                    IF TG_OP <> 'DELETE' THEN
                        PERFORM validate_fact_evidence(NEW.fact_id);
                    END IF;
                ELSIF TG_TABLE_NAME = 'supplier_sources' THEN
                    FOR v_fact_id IN
                        SELECT fact_id FROM fact_sources
                        WHERE source_id = CASE WHEN TG_OP = 'DELETE' THEN OLD.id ELSE NEW.id END
                    LOOP
                        PERFORM validate_fact_evidence(v_fact_id);
                    END LOOP;
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

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER supplier_sources_validate_related ON supplier_sources;
            DROP TRIGGER fact_sources_validate_related ON fact_sources;
            DROP TRIGGER supplier_facts_validate_related ON supplier_facts;
            DROP TRIGGER supplier_requires_current_name_fact ON suppliers;
            DROP FUNCTION enforce_related_fact_integrity();
            DROP FUNCTION validate_fact_evidence(uuid);
            DROP FUNCTION enforce_supplier_current_name_fact();
            DROP FUNCTION validate_current_supplier_name(uuid);
            ALTER TABLE suppliers DROP CONSTRAINT fk_suppliers_current_name_fact;
            """);

        migrationBuilder.CreateIndex(
            "IX_suppliers_id_current_name_fact_id", "suppliers",
            new[] { "id", "current_name_fact_id" }, unique: true);
        migrationBuilder.AddForeignKey(
            name: "fk_suppliers_current_name_fact", table: "suppliers",
            columns: new[] { "id", "current_name_fact_id" },
            principalTable: "supplier_facts", principalColumns: new[] { "supplier_id", "id" },
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.Sql("""
            CREATE FUNCTION enforce_supplier_current_name_fact() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.current_name_fact_id IS NULL OR NOT EXISTS (
                    SELECT 1 FROM supplier_facts f
                    JOIN fact_sources fs ON fs.fact_id = f.id
                    JOIN supplier_sources src ON src.id = fs.source_id
                    WHERE f.id = NEW.current_name_fact_id
                      AND f.supplier_id = NEW.id AND f.field_key = 'name'
                      AND f.is_current AND f.value_json = to_jsonb(NEW.name)
                      AND src.supplier_id = f.supplier_id
                      AND length(btrim(src.excerpt)) > 0
                      AND ((f.verification_status = 'official' AND src.source_type = 'official')
                        OR (f.verification_status = 'external' AND src.source_type = 'external'))
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
}
