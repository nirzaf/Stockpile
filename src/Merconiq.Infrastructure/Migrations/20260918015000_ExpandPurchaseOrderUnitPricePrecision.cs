using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ExpandPurchaseOrderUnitPricePrecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "UnitPrice",
                table: "OrderDetails",
                type: "numeric(20,4)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                LOCK TABLE "OrderDetails" IN ACCESS EXCLUSIVE MODE;
                DO $migration$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "OrderDetails"
                        WHERE "UnitPrice" <> ROUND("UnitPrice", 2)
                           OR "UnitPrice" <= -10000000000000000
                           OR "UnitPrice" >= 10000000000000000)
                    THEN
                        RAISE EXCEPTION 'Cannot downgrade UnitPrice precision while stored values require decimal(20,4).';
                    END IF;
                END
                $migration$;
                """);

            migrationBuilder.AlterColumn<decimal>(
                name: "UnitPrice",
                table: "OrderDetails",
                type: "numeric(18,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(20,4)");
        }
    }
}
