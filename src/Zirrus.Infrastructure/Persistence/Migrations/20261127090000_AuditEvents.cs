using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Zirrus.Infrastructure.Persistence.Migrations;

/// <summary>Creates the audit event ledger independently of mutable asset records.</summary>
[DbContext(typeof(ZirrusDbContext))]
[Migration("20261127090000_AuditEvents")]
public sealed class AuditEvents : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE audit_events (
                id uuid PRIMARY KEY,
                organization_id uuid NOT NULL,
                catalog_id uuid NOT NULL,
                resource_id uuid NOT NULL,
                actor text NOT NULL,
                action text NOT NULL,
                outcome text NOT NULL,
                occurred_at timestamptz NOT NULL,
                correlation_id text NOT NULL
            );
            CREATE INDEX ix_audit_events_organization_time ON audit_events (organization_id, occurred_at, id);
            REVOKE ALL ON audit_events FROM PUBLIC;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE audit_events;");
    }
}
