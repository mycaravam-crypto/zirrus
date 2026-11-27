using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Zirrus.Infrastructure.Persistence.Migrations;

/// <summary>
/// Restricts retention to complete, fully expired digest ranges. A digest is never partially
/// purged by the supported retention path, so the verifier can still validate every retained event.
/// </summary>
[DbContext(typeof(ZirrusDbContext))]
[Migration("20261127190000_AuditWholeDigestRetention")]
public sealed class AuditWholeDigestRetention : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION audit_purge(before timestamptz) RETURNS bigint
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
            DECLARE
                digest_range record;
                removed bigint := 0;
                affected bigint;
            BEGIN
                FOR digest_range IN
                    SELECT d.from_seq, d.to_seq
                    FROM audit_digests d
                    WHERE (
                        SELECT count(*)
                        FROM audit_events e
                        WHERE e.seq BETWEEN d.from_seq AND d.to_seq
                    ) = d.event_count
                    AND NOT EXISTS (
                        SELECT 1
                        FROM audit_events e
                        WHERE e.seq BETWEEN d.from_seq AND d.to_seq
                          AND e.occurred_at >= before
                    )
                    ORDER BY d.to_seq
                LOOP
                    INSERT INTO audit_purged_events (seq, event_id, purged_at)
                        SELECT seq, id, clock_timestamp()
                        FROM audit_events
                        WHERE seq BETWEEN digest_range.from_seq AND digest_range.to_seq
                        ON CONFLICT (seq) DO NOTHING;

                    PERFORM set_config('zirrus.audit_purge', 'on', true);
                    DELETE FROM audit_events
                    WHERE seq BETWEEN digest_range.from_seq AND digest_range.to_seq;
                    GET DIAGNOSTICS affected = ROW_COUNT;
                    removed := removed + affected;
                END LOOP;

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
}
