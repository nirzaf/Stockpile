using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOpeningStockReplay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OpeningStockImports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ImportReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ApprovalReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LineCount = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpeningStockImports", x => x.Id);
                    table.UniqueConstraint("AK_OpeningStockImports_Id_TenantId", x => new { x.Id, x.TenantId });
                });

            migrationBuilder.CreateTable(
                name: "OpeningStockImportLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OpeningStockImportId = table.Column<int>(type: "integer", nullable: false),
                    RowNumber = table.Column<int>(type: "integer", nullable: false),
                    ExternalReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ItemId = table.Column<int>(type: "integer", nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpeningStockImportLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpeningStockImportLines_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OpeningStockImportLines_Locations_LocationId_TenantId",
                        columns: x => new { x.LocationId, x.TenantId },
                        principalTable: "Locations",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OpeningStockImportLines_OpeningStockImports_OpeningStockImp~",
                        columns: x => new { x.OpeningStockImportId, x.TenantId },
                        principalTable: "OpeningStockImports",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OpeningStockImportLines_ItemId",
                table: "OpeningStockImportLines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_OpeningStockImportLines_LocationId_TenantId",
                table: "OpeningStockImportLines",
                columns: new[] { "LocationId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_OpeningStockImportLines_OpeningStockImportId_ExternalRefere~",
                table: "OpeningStockImportLines",
                columns: new[] { "OpeningStockImportId", "ExternalReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpeningStockImportLines_OpeningStockImportId_TenantId",
                table: "OpeningStockImportLines",
                columns: new[] { "OpeningStockImportId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_OpeningStockImportLines_TenantId_ItemId_LocationId",
                table: "OpeningStockImportLines",
                columns: new[] { "TenantId", "ItemId", "LocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_OpeningStockImports_TenantId_ApprovalReference",
                table: "OpeningStockImports",
                columns: new[] { "TenantId", "ApprovalReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpeningStockImports_TenantId_ImportReference",
                table: "OpeningStockImports",
                columns: new[] { "TenantId", "ImportReference" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OpeningStockImportLines");

            migrationBuilder.DropTable(
                name: "OpeningStockImports");
        }
    }
}
