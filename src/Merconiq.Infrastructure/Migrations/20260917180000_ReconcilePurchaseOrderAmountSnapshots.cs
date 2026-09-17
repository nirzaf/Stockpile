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
            // Retain the old header values so a clean downgrade can restore them.
            // Orders without lines retain their existing amounts unchanged.
            migrationBuilder.Sql(
                """
                CREATE TABLE "PurchaseOrderAmountReconciliationBackups" (
                    "PurchaseOrderId" integer PRIMARY KEY,
                    "OriginalNetAmount" numeric(18,6) NOT NULL,
                    "OriginalDiscountAmount" numeric(18,6) NOT NULL,
                    "OriginalTaxAmount" numeric(18,6) NOT NULL,
                    "OriginalTotalAmount" numeric(18,6) NOT NULL,
                    "ReconciledNetAmount" numeric(18,6) NOT NULL,
                    "ReconciledDiscountAmount" numeric(18,6) NOT NULL,
                    "ReconciledTaxAmount" numeric(18,6) NOT NULL,
                    "ReconciledTotalAmount" numeric(18,6) NOT NULL
                );

                INSERT INTO "PurchaseOrderAmountReconciliationBackups" (
                    "PurchaseOrderId",
                    "OriginalNetAmount",
                    "OriginalDiscountAmount",
                    "OriginalTaxAmount",
                    "OriginalTotalAmount",
                    "ReconciledNetAmount",
                    "ReconciledDiscountAmount",
                    "ReconciledTaxAmount",
                    "ReconciledTotalAmount")
                SELECT orders."Id",
                       orders."NetAmount",
                       orders."DiscountAmount",
                       orders."TaxAmount",
                       orders."TotalAmount",
                       COALESCE(line_totals."NetAmount", orders."NetAmount"),
                       COALESCE(line_totals."DiscountAmount", orders."DiscountAmount"),
                       COALESCE(line_totals."TaxAmount", orders."TaxAmount"),
                       COALESCE(line_totals."GrossAmount", orders."TotalAmount")
                FROM "PurchaseOrders" AS orders
                LEFT JOIN (
                    SELECT "PurchaseOrderId",
                           SUM("NetAmount") AS "NetAmount",
                           SUM("DiscountAmount") AS "DiscountAmount",
                           SUM("TaxAmount") AS "TaxAmount",
                           SUM("GrossAmount") AS "GrossAmount"
                    FROM "OrderDetails"
                    GROUP BY "PurchaseOrderId"
                ) AS line_totals ON orders."Id" = line_totals."PurchaseOrderId";

                UPDATE "PurchaseOrders" AS orders
                SET "NetAmount" = backups."ReconciledNetAmount",
                    "DiscountAmount" = backups."ReconciledDiscountAmount",
                    "TaxAmount" = backups."ReconciledTaxAmount",
                    "TotalAmount" = backups."ReconciledTotalAmount"
                FROM "PurchaseOrderAmountReconciliationBackups" AS backups
                WHERE orders."Id" = backups."PurchaseOrderId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Refuse to overwrite post-migration document edits or discard tax
            // evidence. For unchanged records, restore the exact prior header values.
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

                    IF EXISTS (
                           SELECT 1
                           FROM "PurchaseOrders" AS orders
                           LEFT JOIN "PurchaseOrderAmountReconciliationBackups" AS backups
                               ON backups."PurchaseOrderId" = orders."Id"
                           WHERE backups."PurchaseOrderId" IS NULL)
                       OR EXISTS (
                           SELECT 1
                           FROM "PurchaseOrderAmountReconciliationBackups" AS backups
                           LEFT JOIN "PurchaseOrders" AS orders
                               ON orders."Id" = backups."PurchaseOrderId"
                           WHERE orders."Id" IS NULL)
                    THEN
                        RAISE EXCEPTION 'Rollback blocked: purchase orders were added or removed after amount reconciliation.';
                    END IF;

                    IF EXISTS (
                           SELECT 1
                           FROM "PurchaseOrders" AS orders
                           INNER JOIN "PurchaseOrderAmountReconciliationBackups" AS backups
                               ON backups."PurchaseOrderId" = orders."Id"
                           WHERE orders."NetAmount" IS DISTINCT FROM backups."ReconciledNetAmount"
                              OR orders."DiscountAmount" IS DISTINCT FROM backups."ReconciledDiscountAmount"
                              OR orders."TaxAmount" IS DISTINCT FROM backups."ReconciledTaxAmount"
                              OR orders."TotalAmount" IS DISTINCT FROM backups."ReconciledTotalAmount")
                    THEN
                        RAISE EXCEPTION 'Rollback blocked: purchase-order amounts changed after reconciliation.';
                    END IF;
                END
                $migration$;

                UPDATE "PurchaseOrders" AS orders
                SET "NetAmount" = backups."OriginalNetAmount",
                    "DiscountAmount" = backups."OriginalDiscountAmount",
                    "TaxAmount" = backups."OriginalTaxAmount",
                    "TotalAmount" = backups."OriginalTotalAmount"
                FROM "PurchaseOrderAmountReconciliationBackups" AS backups
                WHERE orders."Id" = backups."PurchaseOrderId";

                DROP TABLE "PurchaseOrderAmountReconciliationBackups";
                """);
        }
    }
}
