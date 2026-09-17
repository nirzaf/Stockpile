using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceLinkedStockReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION reject_stock_transaction_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'Stock transactions are append-only.'
                        USING ERRCODE = '55000';
                END;
                $$;

                CREATE TRIGGER TR_StockTransactions_AppendOnly
                    BEFORE UPDATE OR DELETE ON "StockTransactions"
                    FOR EACH ROW
                    EXECUTE FUNCTION reject_stock_transaction_mutation();

                CREATE TRIGGER TR_StockTransactions_NoTruncate
                    BEFORE TRUNCATE ON "StockTransactions"
                    FOR EACH STATEMENT
                    EXECUTE FUNCTION reject_stock_transaction_mutation();
                """);

            migrationBuilder.AddColumn<int>(
                name: "OriginalTransactionId",
                table: "StockTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReturnDisposition",
                table: "StockTransactions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceLineReference",
                table: "StockTransactions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitCost",
                table: "StockTransactions",
                type: "numeric(18,6)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockTransactions_OriginalTransactionId_TenantId",
                table: "StockTransactions",
                columns: new[] { "OriginalTransactionId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransactions_TenantId_SourceLineReference",
                table: "StockTransactions",
                columns: new[] { "TenantId", "SourceLineReference" },
                unique: true,
                filter: "\"SourceLineReference\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_StockTransactions_StockTransactions_OriginalTransactionId_T~",
                table: "StockTransactions",
                columns: new[] { "OriginalTransactionId", "TenantId" },
                principalTable: "StockTransactions",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS TR_StockTransactions_NoTruncate ON "StockTransactions";
                DROP TRIGGER IF EXISTS TR_StockTransactions_AppendOnly ON "StockTransactions";
                DROP FUNCTION IF EXISTS reject_stock_transaction_mutation();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_StockTransactions_StockTransactions_OriginalTransactionId_T~",
                table: "StockTransactions");

            migrationBuilder.DropIndex(
                name: "IX_StockTransactions_OriginalTransactionId_TenantId",
                table: "StockTransactions");

            migrationBuilder.DropIndex(
                name: "IX_StockTransactions_TenantId_SourceLineReference",
                table: "StockTransactions");

            migrationBuilder.DropColumn(
                name: "OriginalTransactionId",
                table: "StockTransactions");

            migrationBuilder.DropColumn(
                name: "ReturnDisposition",
                table: "StockTransactions");

            migrationBuilder.DropColumn(
                name: "SourceLineReference",
                table: "StockTransactions");

            migrationBuilder.DropColumn(
                name: "UnitCost",
                table: "StockTransactions");
        }
    }
}
