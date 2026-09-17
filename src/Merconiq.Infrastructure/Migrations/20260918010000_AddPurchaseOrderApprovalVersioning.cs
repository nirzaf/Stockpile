using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseOrderApprovalVersioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovedCommercialSnapshotJson",
                table: "PurchaseOrders",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApprovedCommercialVersion",
                table: "PurchaseOrders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CommercialVersion",
                table: "PurchaseOrders",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryTerms",
                table: "PurchaseOrders",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "PurchaseOrders",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            // Preserve the terms already approved before enabling the database invariant.
            migrationBuilder.Sql(
                """
                DO $migration$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "PurchaseOrders" AS po
                        INNER JOIN "OrderDetails" AS line
                            ON line."PurchaseOrderId" = po."Id"
                           AND line."TenantId" = po."TenantId"
                        LEFT JOIN "Items" AS item
                            ON item."Id" = line."ItemId"
                           AND item."TenantId" = line."TenantId"
                        WHERE po."Status" = 'Approved'
                          AND item."Id" IS NULL)
                    THEN
                        RAISE EXCEPTION 'Approval snapshot backfill blocked: an approved purchase-order line has no item in its tenant.';
                    END IF;
                END
                $migration$;

                UPDATE "PurchaseOrders" AS po
                SET "ApprovedCommercialVersion" = po."CommercialVersion",
                    "ApprovedCommercialSnapshotJson" = jsonb_build_object(
                        'schemaVersion', 1,
                        'tenantId', po."TenantId",
                        'documentId', po."DocumentId",
                        'poNumber', po."PONumber",
                        'companyId', (SELECT document."CompanyId"
                                      FROM "DocumentIdentities" AS document
                                      WHERE document."Id" = po."DocumentId"
                                        AND document."TenantId" = po."TenantId"),
                        'supplierId', supplier."Id",
                        'supplierName', supplier."Name",
                        'supplierAddress', supplier."Address",
                        'supplierEmail', supplier."Email",
                        'deliveryTerms', po."DeliveryTerms",
                        'notes', po."Notes",
                        'currencyScale', po."CurrencyScale",
                        'calculationVersion', po."CalculationVersion",
                        'netAmount', po."NetAmount",
                        'discountAmount', po."DiscountAmount",
                        'taxAmount', po."TaxAmount",
                        'totalAmount', po."TotalAmount",
                        'lines', COALESCE((
                            SELECT jsonb_agg(jsonb_build_object(
                                'documentLineId', line."DocumentLineId",
                                'itemId', item."Id",
                                'itemCode', item."ItemCode",
                                'itemDescription', item."Description",
                                'unitOfMeasureId', COALESCE(item."PurchaseUnitId", item."BaseUnitId"),
                                'unitOfMeasureCode', unit."Code",
                                'purchaseToBaseFactor', CASE WHEN item."PurchaseUnitId" IS NULL THEN 1 ELSE item."PurchaseToBaseFactor" END,
                                'quantity', line."Quantity",
                                'unitPrice', line."UnitPrice",
                                'discountPercent', line."DiscountPercent",
                                'taxRatePercent', line."TaxRatePercent",
                                'taxCategory', line."TaxCategory",
                                'taxMode', line."TaxMode",
                                'direction', line."Direction",
                                'calculationVersion', line."CalculationVersion",
                                'netAmount', line."NetAmount",
                                'discountAmount', line."DiscountAmount",
                                'taxAmount', line."TaxAmount",
                                'grossAmount', line."GrossAmount"
                            ) ORDER BY line."DocumentLineId")
                            FROM "OrderDetails" AS line
                            INNER JOIN "Items" AS item
                                ON item."Id" = line."ItemId" AND item."TenantId" = line."TenantId"
                            LEFT JOIN "UnitsOfMeasure" AS unit
                                ON unit."Id" = COALESCE(item."PurchaseUnitId", item."BaseUnitId")
                               AND unit."TenantId" = item."TenantId"
                            WHERE line."PurchaseOrderId" = po."Id"
                              AND line."TenantId" = po."TenantId"
                        ), '[]'::jsonb))
                FROM "Suppliers" AS supplier
                WHERE po."Status" = 'Approved'
                  AND supplier."Id" = po."SupplierId"
                  AND supplier."TenantId" = po."TenantId";

                DO $migration$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM "PurchaseOrders"
                        WHERE "Status" = 'Approved'
                          AND "ApprovedCommercialSnapshotJson" IS NULL)
                    THEN
                        RAISE EXCEPTION 'Approval snapshot backfill failed for one or more approved purchase orders.';
                    END IF;
                END
                $migration$;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseOrders_ApprovedCommercialVersion",
                table: "PurchaseOrders",
                sql: "\"Status\" <> 'Approved' OR (\"ApprovedCommercialVersion\" IS NOT NULL AND \"ApprovedCommercialVersion\" = \"CommercialVersion\" AND \"ApprovedCommercialSnapshotJson\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Keep approval/amendment writes out until the preflight and all column drops commit.
            migrationBuilder.Sql("LOCK TABLE \"PurchaseOrders\" IN ACCESS EXCLUSIVE MODE;");

            migrationBuilder.Sql(
                """
                DO $migration$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM "PurchaseOrders"
                        WHERE "ApprovedCommercialSnapshotJson" IS NOT NULL
                           OR "ApprovedCommercialVersion" IS NOT NULL
                           OR "CommercialVersion" <> 1
                           OR "DeliveryTerms" IS NOT NULL)
                    THEN
                        RAISE EXCEPTION 'Rollback blocked: approved purchase-order snapshots, version history, or delivery terms would be discarded.';
                    END IF;
                END
                $migration$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseOrders_ApprovedCommercialVersion",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "ApprovedCommercialSnapshotJson",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "ApprovedCommercialVersion",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "CommercialVersion",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "DeliveryTerms",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "PurchaseOrders");
        }
    }
}
