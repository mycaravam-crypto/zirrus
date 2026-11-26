using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zirrus.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssetsAndStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    media_type = table.Column<string>(type: "character varying(127)", maxLength: 127, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    state_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: false),
                    embedded_metadata = table.Column<string>(type: "jsonb", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assets", x => x.id);
                    table.UniqueConstraint("AK_assets_catalog_id_id", x => new { x.catalog_id, x.id });
                    table.CheckConstraint("ck_assets_sha256", "sha256 IS NULL OR sha256 ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_assets_catalogs_organization_id_catalog_id",
                        columns: x => new { x.organization_id, x.catalog_id },
                        principalTable: "catalogs",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_categories", x => x.id);
                    table.UniqueConstraint("AK_categories_catalog_id_id", x => new { x.catalog_id, x.id });
                    table.ForeignKey(
                        name: "FK_categories_catalogs_organization_id_catalog_id",
                        columns: x => new { x.organization_id, x.catalog_id },
                        principalTable: "catalogs",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_categories_categories_catalog_id_parent_id",
                        columns: x => new { x.catalog_id, x.parent_id },
                        principalTable: "categories",
                        principalColumns: new[] { "catalog_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "metadata_schemas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_schemas", x => x.id);
                    table.ForeignKey(
                        name: "FK_metadata_schemas_catalogs_organization_id_catalog_id",
                        columns: x => new { x.organization_id, x.catalog_id },
                        principalTable: "catalogs",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    job_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: true),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    run_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    locked_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.id);
                    table.ForeignKey(
                        name: "FK_jobs_assets_catalog_id_asset_id",
                        columns: x => new { x.catalog_id, x.asset_id },
                        principalTable: "assets",
                        principalColumns: new[] { "catalog_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "renditions",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pipeline_version = table.Column<int>(type: "integer", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    media_type = table.Column<string>(type: "character varying(127)", maxLength: 127, nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_renditions", x => new { x.asset_id, x.kind });
                    table.ForeignKey(
                        name: "FK_renditions_assets_catalog_id_asset_id",
                        columns: x => new { x.catalog_id, x.asset_id },
                        principalTable: "assets",
                        principalColumns: new[] { "catalog_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "asset_categories",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_categories", x => new { x.asset_id, x.category_id });
                    table.ForeignKey(
                        name: "FK_asset_categories_assets_catalog_id_asset_id",
                        columns: x => new { x.catalog_id, x.asset_id },
                        principalTable: "assets",
                        principalColumns: new[] { "catalog_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_asset_categories_categories_catalog_id_category_id",
                        columns: x => new { x.catalog_id, x.category_id },
                        principalTable: "categories",
                        principalColumns: new[] { "catalog_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "metadata_fields",
                columns: table => new
                {
                    schema_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    multiple = table.Column<bool>(type: "boolean", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_fields", x => new { x.schema_id, x.key });
                    table.ForeignKey(
                        name: "FK_metadata_fields_metadata_schemas_schema_id",
                        column: x => x.schema_id,
                        principalTable: "metadata_schemas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_asset_categories_catalog_id_asset_id",
                table: "asset_categories",
                columns: new[] { "catalog_id", "asset_id" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_categories_catalog_id_category_id",
                table: "asset_categories",
                columns: new[] { "catalog_id", "category_id" });

            migrationBuilder.CreateIndex(
                name: "IX_assets_catalog_id_sha256",
                table: "assets",
                columns: new[] { "catalog_id", "sha256" });

            migrationBuilder.CreateIndex(
                name: "IX_assets_catalog_id_state_updated_at",
                table: "assets",
                columns: new[] { "catalog_id", "state", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "IX_assets_metadata",
                table: "assets",
                column: "metadata")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "jsonb_path_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_assets_organization_id_catalog_id",
                table: "assets",
                columns: new[] { "organization_id", "catalog_id" });

            migrationBuilder.CreateIndex(
                name: "IX_assets_original_key",
                table: "assets",
                column: "original_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assets_state_state_changed_at",
                table: "assets",
                columns: new[] { "state", "state_changed_at" });

            migrationBuilder.CreateIndex(
                name: "IX_categories_catalog_id_parent_id_name",
                table: "categories",
                columns: new[] { "catalog_id", "parent_id", "name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_categories_organization_id_catalog_id",
                table: "categories",
                columns: new[] { "organization_id", "catalog_id" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_catalog_id_asset_id",
                table: "jobs",
                columns: new[] { "catalog_id", "asset_id" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_job_key",
                table: "jobs",
                column: "job_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_jobs_state_run_after",
                table: "jobs",
                columns: new[] { "state", "run_after" },
                filter: "state IN ('Ready', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_metadata_schemas_catalog_id_version",
                table: "metadata_schemas",
                columns: new[] { "catalog_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_metadata_schemas_organization_id_catalog_id",
                table: "metadata_schemas",
                columns: new[] { "organization_id", "catalog_id" });

            migrationBuilder.CreateIndex(
                name: "IX_renditions_catalog_id_asset_id",
                table: "renditions",
                columns: new[] { "catalog_id", "asset_id" });

            migrationBuilder.CreateIndex(
                name: "IX_renditions_storage_key",
                table: "renditions",
                column: "storage_key",
                unique: true);

            // Existing catalogs must reference an organization row before the foreign key exists:
            // create one per distinct organization_id, plus the default from appsettings.json.
            migrationBuilder.Sql(
                """
                INSERT INTO organizations (id, name, created_at)
                SELECT DISTINCT organization_id, 'Default organization', now() FROM catalogs
                UNION
                SELECT '6f0d3d2a-7c1e-4b8e-9a61-1d5c0a7e2b01'::uuid, 'Default organization', now()
                ON CONFLICT (id) DO NOTHING;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_catalogs_organizations_organization_id",
                table: "catalogs",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_catalogs_organizations_organization_id",
                table: "catalogs");

            migrationBuilder.DropTable(
                name: "asset_categories");

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "metadata_fields");

            migrationBuilder.DropTable(
                name: "organizations");

            migrationBuilder.DropTable(
                name: "renditions");

            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "metadata_schemas");

            migrationBuilder.DropTable(
                name: "assets");
        }
    }
}
