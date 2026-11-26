using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zirrus.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "catalogs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalogs", x => x.id);
                    table.UniqueConstraint("AK_catalogs_organization_id_id", x => new { x.organization_id, x.id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_catalogs_organization_id_name",
                table: "catalogs",
                columns: new[] { "organization_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "catalogs");
        }
    }
}
