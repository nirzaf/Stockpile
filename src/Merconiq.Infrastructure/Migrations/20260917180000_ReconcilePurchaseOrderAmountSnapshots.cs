using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReconcilePurchaseOrderAmountSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Make the document headers use the exact rounded line snapshots that
            // were backfilled by AddTaxRulesAndAmountSnapshots. This preserves the
            // documented sum-of-rounded-lines rule and avoids a header/line mismatch.
            migrationBuilder.Sql(
                """
                UPDATE "PurchaseOrders" AS orders
                SET "NetAmount" = line_totals."NetAmount",
                    "DiscountAmount" = line_totals."DiscountAmount",
                    "TaxAmount" = line_totals."TaxAmount",
                    "TotalAmount" = line_totals."GrossAmount"
                FROM (
                    SELECT "PurchaseOrderId",
                           SUM("NetAmount") AS "NetAmount",
                           SUM("DiscountAmount") AS "DiscountAmount",
                           SUM("TaxAmount") AS "TaxAmount",
                           SUM("GrossAmount") AS "GrossAmount"
                    FROM "OrderDetails"
                    GROUP BY "PurchaseOrderId"
                ) AS line_totals
                WHERE orders."Id" = line_totals."PurchaseOrderId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // This data-only correction is safe to retain on downgrade, but the
            // preceding tax/snapshot migration would discard tax policy or
            // non-default calculation evidence. Fail closed before it can run.
            migrationBuilder.Sql(
                """
                DO $migration$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "TaxRules")
                       OR EXISTS (
                           SELECT 1 FROM "OrderDetails"
                           WHERE "TaxRuleId" IS NOT NULL
                              OR "TaxCategory" <> 'Standard'
                              OR "TaxMode" <> 'Exclusive'
                              OR "Direction" <> 'Charge'
                              OR "DiscountPercent" <> 0
                              OR "DiscountAmount" <> 0
                              OR "TaxRatePercent" <> 0
                              OR "TaxEffectiveFromUtc" IS NOT NULL
                              OR "TaxAmount" <> 0
                              OR "CurrencyScale" <> 2
                              OR "CalculationVersion" <> 1)
                       OR EXISTS (
                           SELECT 1 FROM "PurchaseOrders"
                           WHERE "DiscountAmount" <> 0
                              OR "TaxAmount" <> 0
                              OR "CurrencyScale" <> 2
                              OR "CalculationVersion" <> 1)
                    THEN
                        RAISE EXCEPTION 'Rollback blocked: persisted tax rules or non-default amount snapshots would be discarded.';
                    END IF;
                END
                $migration$;
                """);
        }
    }
}
