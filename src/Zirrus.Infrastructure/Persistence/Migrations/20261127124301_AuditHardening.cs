using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zirrus.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// W7 audit hardening (security.md, Q10):
    /// <list type="bullet">
    /// <item><c>audit_events</c> gains an insertion sequence and a detail column; catalog and resource become
    /// optional (sign-in has neither).</item>
    /// <item><c>audit_digests</c> stores signed digests of contiguous event ranges.</item>
    /// <item>Both tables are append-only for every role: a trigger rejects UPDATE, DELETE and TRUNCATE. Only
    /// <c>audit_purge(before)</c>, a SECURITY DEFINER function the application role cannot execute,
    /// may delete events older than the retention period.</item>
    /// <item>The application role <c>zirrus_app</c> (created without login if missing; the deployment
    /// gives it a password) gets DML on application tables but only INSERT and SELECT on the audit
    /// tables. Migrations keep running as the owner.</item>
    /// </list>
    /// </summary>
    public partial class AuditHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE audit_events
                    ALTER COLUMN catalog_id DROP NOT NULL,
                    ALTER COLUMN resource_id DROP NOT NULL,
                    ADD COLUMN seq bigint GENERATED ALWAYS AS IDENTITY,
                    ADD COLUMN detail varchar(500);
                CREATE UNIQUE INDEX ix_audit_events_seq ON audit_events (seq);

                CREATE TABLE audit_digests (
                    id uuid PRIMARY KEY,
                    from_seq bigint NOT NULL,
                    to_seq bigint NOT NULL,
                    event_count integer NOT NULL,
                    sha256 char(64) NOT NULL,
                    hmac char(64) NOT NULL,
                    previous_sha256 char(64) NOT NULL,
                    key_id varchar(16) NOT NULL,
                    created_at timestamptz NOT NULL,
                    CONSTRAINT ck_audit_digests_range CHECK (from_seq <= to_seq)
                );
                CREATE UNIQUE INDEX ix_audit_digests_to_seq ON audit_digests (to_seq);

                CREATE FUNCTION zirrus_audit_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' AND TG_TABLE_NAME = 'audit_events'
                       AND current_setting('zirrus.audit_purge', true) = 'on' THEN
                        RETURN OLD;
                    END IF;
                    RAISE EXCEPTION 'audit records are append-only (% on %)', TG_OP, TG_TABLE_NAME
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;
                CREATE TRIGGER audit_events_append_only BEFORE UPDATE OR DELETE ON audit_events
                    FOR EACH ROW EXECUTE FUNCTION zirrus_audit_append_only();
                CREATE TRIGGER audit_events_no_truncate BEFORE TRUNCATE ON audit_events
                    FOR EACH STATEMENT EXECUTE FUNCTION zirrus_audit_append_only();
                CREATE TRIGGER audit_digests_append_only BEFORE UPDATE OR DELETE ON audit_digests
                    FOR EACH ROW EXECUTE FUNCTION zirrus_audit_append_only();
                CREATE TRIGGER audit_digests_no_truncate BEFORE TRUNCATE ON audit_digests
                    FOR EACH STATEMENT EXECUTE FUNCTION zirrus_audit_append_only();

                -- Retention purge: the only way to delete audit events. Run by the maintenance (owner) role.
                CREATE FUNCTION audit_purge(before timestamptz) RETURNS bigint
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
                REVOKE ALL ON FUNCTION audit_purge(timestamptz) FROM PUBLIC;

                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'zirrus_app') THEN
                        CREATE ROLE zirrus_app NOLOGIN;
                    END IF;
                END
                $$;
                GRANT USAGE ON SCHEMA public TO zirrus_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO zirrus_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO zirrus_app;
                REVOKE UPDATE, DELETE, TRUNCATE ON audit_events, audit_digests FROM zirrus_app;
                REVOKE ALL ON "__EFMigrationsHistory" FROM zirrus_app;
                REVOKE ALL ON FUNCTION audit_purge(timestamptz) FROM zirrus_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO zirrus_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO zirrus_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The role is cluster-wide and may serve other databases: revoke here, never drop it.
            migrationBuilder.Sql("""
                ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE USAGE, SELECT ON SEQUENCES FROM zirrus_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM zirrus_app;
                REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM zirrus_app;
                REVOKE ALL ON ALL TABLES IN SCHEMA public FROM zirrus_app;
                REVOKE USAGE ON SCHEMA public FROM zirrus_app;

                DROP FUNCTION audit_purge(timestamptz);
                DROP TRIGGER audit_digests_no_truncate ON audit_digests;
                DROP TRIGGER audit_digests_append_only ON audit_digests;
                DROP TRIGGER audit_events_no_truncate ON audit_events;
                DROP TRIGGER audit_events_append_only ON audit_events;
                DROP FUNCTION zirrus_audit_append_only();
                DROP TABLE audit_digests;

                DROP INDEX ix_audit_events_seq;
                DELETE FROM audit_events WHERE catalog_id IS NULL OR resource_id IS NULL;
                ALTER TABLE audit_events
                    DROP COLUMN detail,
                    DROP COLUMN seq,
                    ALTER COLUMN resource_id SET NOT NULL,
                    ALTER COLUMN catalog_id SET NOT NULL;
                """);
        }
    }
}
