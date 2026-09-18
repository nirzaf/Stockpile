using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTransferTransitWriteOff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "StockTransactionId",
                table: "TransferTransitSettlements",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TransferTransitSettlements_StockTransactionDisposition",
                table: "TransferTransitSettlements",
                sql: "(\"SettlementType\" = 'WrittenOff' AND \"StockTransactionId\" IS NULL) OR (\"SettlementType\" <> 'WrittenOff' AND \"StockTransactionId\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "TransferTransitSettlements"
                        WHERE "SettlementType" = 'WrittenOff'
                          AND "StockTransactionId" IS NULL
                    ) THEN
                        RAISE EXCEPTION 'Cannot remove transit write-off support while write-off settlements exist.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_TransferTransitSettlements_StockTransactionDisposition",
                table: "TransferTransitSettlements");

            migrationBuilder.AlterColumn<int>(
                name: "StockTransactionId",
                table: "TransferTransitSettlements",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
