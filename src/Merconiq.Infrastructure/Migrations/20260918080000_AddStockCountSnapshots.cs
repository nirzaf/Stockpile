using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStockCountSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StockCounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LocationId = table.Column<int>(type: "integer", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: true),
                    SnapshotAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MovementWatermark = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockCounts", x => x.Id);
                    table.UniqueConstraint("AK_StockCounts_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.ForeignKey(
                        name: "FK_StockCounts_Companies_CompanyId_TenantId",
                        columns: x => new { x.CompanyId, x.TenantId },
                        principalTable: "Companies",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockCounts_Locations_LocationId_TenantId",
                        columns: x => new { x.LocationId, x.TenantId },
                        principalTable: "Locations",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockCountLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StockCountId = table.Column<int>(type: "integer", nullable: false),
                    ItemId = table.Column<int>(type: "integer", nullable: false),
                    ItemCodeSnapshot = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ItemDescriptionSnapshot = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    BatchNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExpiryDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SnapshotQuantity = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockCountLines", x => x.Id);
                    table.UniqueConstraint("AK_StockCountLines_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("CK_StockCountLines_SnapshotQuantity", "\"SnapshotQuantity\" >= 0");
                    table.ForeignKey(
                        name: "FK_StockCountLines_Items_ItemId_TenantId",
                        columns: x => new { x.ItemId, x.TenantId },
                        principalTable: "Items",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockCountLines_StockCounts_StockCountId_TenantId",
                        columns: x => new { x.StockCountId, x.TenantId },
                        principalTable: "StockCounts",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StockCountObservations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StockCountLineId = table.Column<int>(type: "integer", nullable: false),
                    CountedQuantity = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockCountObservations", x => x.Id);
                    table.CheckConstraint("CK_StockCountObservations_CountedQuantity", "\"CountedQuantity\" >= 0");
                    table.ForeignKey(
                        name: "FK_StockCountObservations_StockCountLines_StockCountLineId_Ten~",
                        columns: x => new { x.StockCountLineId, x.TenantId },
                        principalTable: "StockCountLines",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockCountLines_ItemId_TenantId",
                table: "StockCountLines",
                columns: new[] { "ItemId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockCountLines_StockCountId_TenantId",
                table: "StockCountLines",
                columns: new[] { "StockCountId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockCountObservations_StockCountLineId_TenantId",
                table: "StockCountObservations",
                columns: new[] { "StockCountLineId", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockCounts_CompanyId_TenantId",
                table: "StockCounts",
                columns: new[] { "CompanyId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockCounts_LocationId_TenantId",
                table: "StockCounts",
                columns: new[] { "LocationId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockCounts_TenantId_LocationId_SnapshotAtUtc",
                table: "StockCounts",
                columns: new[] { "TenantId", "LocationId", "SnapshotAtUtc" });

            migrationBuilder.Sql("""
                CREATE FUNCTION reject_stock_count_history_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'Stock-count snapshots and observations are append-only.'
                        USING ERRCODE = '55000';
                    RETURN NULL;
                END;
                $$;

                CREATE TRIGGER TR_StockCounts_AppendOnly
                    BEFORE UPDATE OR DELETE ON "StockCounts"
                    FOR EACH ROW
                    EXECUTE FUNCTION reject_stock_count_history_mutation();

                CREATE TRIGGER TR_StockCounts_NoTruncate
                    BEFORE TRUNCATE ON "StockCounts"
                    FOR EACH STATEMENT
                    EXECUTE FUNCTION reject_stock_count_history_mutation();

                CREATE TRIGGER TR_StockCountLines_AppendOnly
                    BEFORE UPDATE OR DELETE ON "StockCountLines"
                    FOR EACH ROW
                    EXECUTE FUNCTION reject_stock_count_history_mutation();

                CREATE TRIGGER TR_StockCountLines_NoTruncate
                    BEFORE TRUNCATE ON "StockCountLines"
                    FOR EACH STATEMENT
                    EXECUTE FUNCTION reject_stock_count_history_mutation();

                CREATE TRIGGER TR_StockCountObservations_AppendOnly
                    BEFORE UPDATE OR DELETE ON "StockCountObservations"
                    FOR EACH ROW
                    EXECUTE FUNCTION reject_stock_count_history_mutation();

                CREATE TRIGGER TR_StockCountObservations_NoTruncate
                    BEFORE TRUNCATE ON "StockCountObservations"
                    FOR EACH STATEMENT
                    EXECUTE FUNCTION reject_stock_count_history_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS TR_StockCountObservations_NoTruncate ON "StockCountObservations";
                DROP TRIGGER IF EXISTS TR_StockCountObservations_AppendOnly ON "StockCountObservations";
                DROP TRIGGER IF EXISTS TR_StockCountLines_NoTruncate ON "StockCountLines";
                DROP TRIGGER IF EXISTS TR_StockCountLines_AppendOnly ON "StockCountLines";
                DROP TRIGGER IF EXISTS TR_StockCounts_NoTruncate ON "StockCounts";
                DROP TRIGGER IF EXISTS TR_StockCounts_AppendOnly ON "StockCounts";
                DROP FUNCTION IF EXISTS reject_stock_count_history_mutation();
                """);

            migrationBuilder.DropTable(
                name: "StockCountObservations");

            migrationBuilder.DropTable(
                name: "StockCountLines");

            migrationBuilder.DropTable(
                name: "StockCounts");
        }
    }
}
