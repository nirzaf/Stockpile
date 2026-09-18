using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStockCountVariances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StockCountVariances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StockCountLineId = table.Column<int>(type: "integer", nullable: false),
                    ExpectedCurrentQuantity = table.Column<int>(type: "integer", nullable: false),
                    CountedQuantity = table.Column<int>(type: "integer", nullable: false),
                    DeltaQuantity = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ApprovedUnitCost = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    ValueAdjustment = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    StockTransactionId = table.Column<int>(type: "integer", nullable: true),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockCountVariances", x => x.Id);
                    table.CheckConstraint("CK_StockCountVariances_ApprovedUnitCost", "\"ApprovedUnitCost\" IS NULL OR \"ApprovedUnitCost\" >= 0");
                    table.CheckConstraint("CK_StockCountVariances_Quantities", "\"ExpectedCurrentQuantity\" >= 0 AND \"CountedQuantity\" >= 0 AND \"DeltaQuantity\" = \"CountedQuantity\" - \"ExpectedCurrentQuantity\"");
                    table.CheckConstraint("CK_StockCountVariances_Reason", "length(trim(\"Reason\")) > 0");
                    table.ForeignKey(
                        name: "FK_StockCountVariances_StockCountLines_StockCountLineId_Tenant~",
                        columns: x => new { x.StockCountLineId, x.TenantId },
                        principalTable: "StockCountLines",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockCountVariances_StockTransactions_StockTransactionId_Te~",
                        columns: x => new { x.StockTransactionId, x.TenantId },
                        principalTable: "StockTransactions",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockCountVariances_StockCountLineId_TenantId",
                table: "StockCountVariances",
                columns: new[] { "StockCountLineId", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockCountVariances_StockTransactionId_TenantId",
                table: "StockCountVariances",
                columns: new[] { "StockTransactionId", "TenantId" },
                unique: true,
                filter: "\"StockTransactionId\" IS NOT NULL");

            migrationBuilder.Sql("""
                CREATE FUNCTION reject_stock_count_variance_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'Stock-count variances are append-only.'
                        USING ERRCODE = '55000';
                    RETURN NULL;
                END;
                $$;

                CREATE TRIGGER TR_StockCountVariances_AppendOnly
                    BEFORE UPDATE OR DELETE ON "StockCountVariances"
                    FOR EACH ROW
                    EXECUTE FUNCTION reject_stock_count_variance_mutation();

                CREATE TRIGGER TR_StockCountVariances_NoTruncate
                    BEFORE TRUNCATE ON "StockCountVariances"
                    FOR EACH STATEMENT
                    EXECUTE FUNCTION reject_stock_count_variance_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS TR_StockCountVariances_NoTruncate ON "StockCountVariances";
                DROP TRIGGER IF EXISTS TR_StockCountVariances_AppendOnly ON "StockCountVariances";
                DROP FUNCTION IF EXISTS reject_stock_count_variance_mutation();
                """);

            migrationBuilder.DropTable(
                name: "StockCountVariances");
        }
    }
}
