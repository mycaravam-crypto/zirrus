using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Zirrus.Infrastructure.Persistence.Migrations;

/// <summary>PostgreSQL expression indexes for synchronous multilingual asset search and keyset paging.</summary>
[DbContext(typeof(ZirrusDbContext))]
[Migration("20261127080000_SearchIndexes")]
public sealed class SearchIndexes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // SQL-managed expression indexes; keep these expressions identical to AssetStore.
        migrationBuilder.Sql("""
            CREATE INDEX ix_assets_search_english ON assets USING gin
                ((to_tsvector('english', file_name) || jsonb_to_tsvector('english', metadata, '["string"]'::jsonb)))
                WHERE state = 'Active';
            CREATE INDEX ix_assets_search_german ON assets USING gin
                ((to_tsvector('german', file_name) || jsonb_to_tsvector('german', metadata, '["string"]'::jsonb)))
                WHERE state = 'Active';
            CREATE INDEX ix_assets_search_simple ON assets USING gin
                ((to_tsvector('simple', file_name) || jsonb_to_tsvector('simple', metadata, '["string"]'::jsonb)))
                WHERE state = 'Active';
            CREATE INDEX ix_assets_catalog_created_id ON assets (catalog_id, created_at DESC, id DESC);
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX ix_assets_catalog_created_id;
            DROP INDEX ix_assets_search_simple;
            DROP INDEX ix_assets_search_german;
            DROP INDEX ix_assets_search_english;
            """);
    }
}
