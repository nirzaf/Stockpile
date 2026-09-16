using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HardenStockValuation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StockValuationBuckets_Items_ItemId",
                table: "StockValuationBuckets");

            migrationBuilder.DropForeignKey(
                name: "FK_StockValuationEntries_Items_ItemId",
                table: "StockValuationEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_StockValuationEntries_StockTransactions_StockTransactionId",
                table: "StockValuationEntries");

            migrationBuilder.DropIndex(
                name: "IX_StockValuationEntries_ItemId",
                table: "StockValuationEntries");

            migrationBuilder.DropIndex(
                name: "IX_StockValuationEntries_StockTransactionId",
                table: "StockValuationEntries");

            migrationBuilder.DropIndex(
                name: "IX_StockValuationBuckets_ItemId",
                table: "StockValuationBuckets");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_StockTransactions_Id_TenantId",
                table: "StockTransactions",
                columns: new[] { "Id", "TenantId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Items_Id_TenantId",
                table: "Items",
                columns: new[] { "Id", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockValuationEntries_ItemId_TenantId",
                table: "StockValuationEntries",
                columns: new[] { "ItemId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockValuationEntries_StockTransactionId_TenantId",
                table: "StockValuationEntries",
                columns: new[] { "StockTransactionId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockValuationBuckets_ItemId_TenantId",
                table: "StockValuationBuckets",
                columns: new[] { "ItemId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransactions_Id_TenantId",
                table: "StockTransactions",
                columns: new[] { "Id", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Items_Id_TenantId",
                table: "Items",
                columns: new[] { "Id", "TenantId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_StockValuationBuckets_Items_ItemId_TenantId",
                table: "StockValuationBuckets",
                columns: new[] { "ItemId", "TenantId" },
                principalTable: "Items",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockValuationEntries_Items_ItemId_TenantId",
                table: "StockValuationEntries",
                columns: new[] { "ItemId", "TenantId" },
                principalTable: "Items",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockValuationEntries_StockTransactions_StockTransactionId_~",
                table: "StockValuationEntries",
                columns: new[] { "StockTransactionId", "TenantId" },
                principalTable: "StockTransactions",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION prevent_stock_valuation_entry_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'Stock valuation entries are append-only.';
                END;
                $$;

                CREATE TRIGGER "TR_StockValuationEntries_AppendOnly"
                BEFORE UPDATE OR DELETE ON "StockValuationEntries"
                FOR EACH ROW EXECUTE FUNCTION prevent_stock_valuation_entry_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS "TR_StockValuationEntries_AppendOnly" ON "StockValuationEntries";
                DROP FUNCTION IF EXISTS prevent_stock_valuation_entry_mutation();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_StockValuationBuckets_Items_ItemId_TenantId",
                table: "StockValuationBuckets");

            migrationBuilder.DropForeignKey(
                name: "FK_StockValuationEntries_Items_ItemId_TenantId",
                table: "StockValuationEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_StockValuationEntries_StockTransactions_StockTransactionId_~",
                table: "StockValuationEntries");

            migrationBuilder.DropIndex(
                name: "IX_StockValuationEntries_ItemId_TenantId",
                table: "StockValuationEntries");

            migrationBuilder.DropIndex(
                name: "IX_StockValuationEntries_StockTransactionId_TenantId",
                table: "StockValuationEntries");

            migrationBuilder.DropIndex(
                name: "IX_StockValuationBuckets_ItemId_TenantId",
                table: "StockValuationBuckets");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_StockTransactions_Id_TenantId",
                table: "StockTransactions");

            migrationBuilder.DropIndex(
                name: "IX_StockTransactions_Id_TenantId",
                table: "StockTransactions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Items_Id_TenantId",
                table: "Items");

            migrationBuilder.DropIndex(
                name: "IX_Items_Id_TenantId",
                table: "Items");

            migrationBuilder.CreateIndex(
                name: "IX_StockValuationEntries_ItemId",
                table: "StockValuationEntries",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_StockValuationEntries_StockTransactionId",
                table: "StockValuationEntries",
                column: "StockTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_StockValuationBuckets_ItemId",
                table: "StockValuationBuckets",
                column: "ItemId");

            migrationBuilder.AddForeignKey(
                name: "FK_StockValuationBuckets_Items_ItemId",
                table: "StockValuationBuckets",
                column: "ItemId",
                principalTable: "Items",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockValuationEntries_Items_ItemId",
                table: "StockValuationEntries",
                column: "ItemId",
                principalTable: "Items",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockValuationEntries_StockTransactions_StockTransactionId",
                table: "StockValuationEntries",
                column: "StockTransactionId",
                principalTable: "StockTransactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
