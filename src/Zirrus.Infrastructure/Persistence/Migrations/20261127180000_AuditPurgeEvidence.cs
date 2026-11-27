using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Zirrus.Infrastructure.Persistence.Migrations;

/// <summary>
/// Records the exact audit event sequences removed by the retention purge. Verification must
/// never infer authorization from a missing leading sequence range alone.
/// </summary>
[DbContext(typeof(ZirrusDbContext))]
[Migration("20261127180000_AuditPurgeEvidence")]
public sealed class AuditPurgeEvidence : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE audit_purged_events (
                seq bigint PRIMARY KEY,
                event_id uuid NOT NULL,
                purged_at timestamptz NOT NULL
            );

            CREATE TRIGGER audit_purged_events_append_only
                BEFORE UPDATE OR DELETE ON audit_purged_events
                FOR EACH ROW EXECUTE FUNCTION zirrus_audit_append_only();
            CREATE TRIGGER audit_purged_events_no_truncate
                BEFORE TRUNCATE ON audit_purged_events
                FOR EACH STATEMENT EXECUTE FUNCTION zirrus_audit_append_only();

            REVOKE ALL ON audit_purged_events FROM PUBLIC;
            REVOKE ALL ON audit_purged_events FROM zirrus_app;

            CREATE OR REPLACE FUNCTION audit_purge(before timestamptz) RETURNS bigint
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
            DECLARE removed bigint;
            BEGIN
                INSERT INTO audit_purged_events (seq, event_id, purged_at)
                    SELECT seq, id, clock_timestamp()
                    FROM audit_events
                    WHERE occurred_at < before
                    ON CONFLICT (seq) DO NOTHING;

                PERFORM set_config('zirrus.audit_purge', 'on', true);
                DELETE FROM audit_events WHERE occurred_at < before;
                GET DIAGNOSTICS removed = ROW_COUNT;
                PERFORM set_config('zirrus.audit_purge', 'off', true);
                RETURN removed;
            END;
            $$;
            REVOKE ALL ON FUNCTION audit_purge(timestamptz) FROM PUBLIC;
            REVOKE ALL ON FUNCTION audit_purge(timestamptz) FROM zirrus_app;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION audit_purge(before timestamptz) RETURNS bigint
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
            DECLARE removed bigint;
            BEGIN
                PERFORM set_config('zirrus.audit_purge', 'on', true);
                DELETE FROM audit_events WHERE occurred_at < before;
                GET DIAGNOSTICS removed = ROW_COUNT;
                PERFORM set_config('zirrus.audit_purge', 'off', true);
                RETURN removed;
            END;
            $$;
            DROP TRIGGER audit_purged_events_no_truncate ON audit_purged_events;
            DROP TRIGGER audit_purged_events_append_only ON audit_purged_events;
            DROP TABLE audit_purged_events;
            """);
    }
}
