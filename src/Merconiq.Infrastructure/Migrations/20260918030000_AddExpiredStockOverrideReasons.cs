using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExpiredStockOverrideReasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExpiryExceptionReason",
                table: "StockTransactions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpiryExceptionReason",
                table: "StockReservations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                LOCK TABLE "StockTransactions", "StockReservations" IN ACCESS EXCLUSIVE MODE;
                DO $migration$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "StockTransactions"
                        WHERE "ExpiryExceptionReason" IS NOT NULL
                    ) OR EXISTS (
                        SELECT 1
                        FROM "StockReservations"
                        WHERE "ExpiryExceptionReason" IS NOT NULL
                    ) THEN
                        RAISE EXCEPTION 'Cannot downgrade expired-stock override reasons while audit reasons are still persisted.';
                    END IF;
                END
                $migration$;
                """);

            migrationBuilder.DropColumn(
                name: "ExpiryExceptionReason",
                table: "StockTransactions");

            migrationBuilder.DropColumn(
                name: "ExpiryExceptionReason",
                table: "StockReservations");
        }
    }
}
