using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStockQuarantineReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "QuarantineReason",
                table: "StockTransactions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                LOCK TABLE "StockTransactions" IN ACCESS EXCLUSIVE MODE;
                DO $migration$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "StockTransactions"
                        WHERE "QuarantineReason" IS NOT NULL
                    ) THEN
                        RAISE EXCEPTION 'Cannot downgrade while quarantine audit reasons are still persisted.';
                    END IF;
                END
                $migration$;
                """);

            migrationBuilder.DropColumn(
                name: "QuarantineReason",
                table: "StockTransactions");
        }
    }
}
