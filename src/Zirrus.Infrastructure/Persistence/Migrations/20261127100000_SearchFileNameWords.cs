using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Zirrus.Infrastructure.Persistence.Migrations;

/// <summary>
/// Rebuilds the search indexes so file names are split into words on <c>.</c>, <c>_</c> and <c>-</c>.
/// PostgreSQL's parser otherwise keeps "harbour-at-dusk.jpg" as one file token, so "harbour" did not
/// match it. Keep these expressions identical to <c>AssetStore</c>.
/// </summary>
[DbContext(typeof(ZirrusDbContext))]
[Migration("20261127100000_SearchFileNameWords")]
public sealed class SearchFileNameWords : Migration
{
    private const string Words = "regexp_replace(file_name, '[._-]+', ' ', 'g')";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, Words);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, "file_name");

    private static void Rebuild(MigrationBuilder migrationBuilder, string fileName)
    {
        foreach (var config in new[] { "english", "german", "simple" })
        {
            migrationBuilder.Sql($"""
                DROP INDEX IF EXISTS ix_assets_search_{config};
                CREATE INDEX ix_assets_search_{config} ON assets USING gin
                    ((to_tsvector('{config}', {fileName}) || jsonb_to_tsvector('{config}', metadata, '["string"]'::jsonb)))
                    WHERE state = 'Active';
                """);
        }
    }
}
