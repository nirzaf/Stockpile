using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTaxRulesAndAmountSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "TotalAmount",
                table: "PurchaseOrders",
                type: "numeric(18,6)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)");

            migrationBuilder.AddColumn<int>(
                name: "CalculationVersion",
                table: "PurchaseOrders",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "CurrencyScale",
                table: "PurchaseOrders",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountAmount",
                table: "PurchaseOrders",
                type: "numeric(18,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "NetAmount",
                table: "PurchaseOrders",
                type: "numeric(18,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmount",
                table: "PurchaseOrders",
                type: "numeric(18,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "CalculationVersion",
                table: "OrderDetails",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "CurrencyScale",
                table: "OrderDetails",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<string>(
                name: "Direction",
                table: "OrderDetails",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Charge");

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountAmount",
                table: "OrderDetails",
                type: "numeric(18,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountPercent",
                table: "OrderDetails",
                type: "numeric(18,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "GrossAmount",
                table: "OrderDetails",
                type: "numeric(18,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "NetAmount",
                table: "OrderDetails",
                type: "numeric(18,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmount",
                table: "OrderDetails",
                type: "numeric(18,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "TaxCategory",
                table: "OrderDetails",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Standard");

            migrationBuilder.AddColumn<DateTime>(
                name: "TaxEffectiveFromUtc",
                table: "OrderDetails",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxMode",
                table: "OrderDetails",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Exclusive");

            migrationBuilder.AddColumn<decimal>(
                name: "TaxRatePercent",
                table: "OrderDetails",
                type: "numeric(18,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "TaxRuleId",
                table: "OrderDetails",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxableAmount",
                table: "OrderDetails",
                type: "numeric(18,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "TaxRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RatePercent = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    CalculationMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EffectiveFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EffectiveToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxRules", x => x.Id);
                });

            // Existing purchase orders have no prior tax policy. Preserve their existing
            // two-decimal totals as zero-tax snapshots rather than inventing a tax rule.
            migrationBuilder.Sql(
                """
                UPDATE "PurchaseOrders"
                SET "NetAmount" = "TotalAmount",
                    "DiscountAmount" = 0,
                    "TaxAmount" = 0,
                    "CurrencyScale" = 2,
                    "CalculationVersion" = 1;

                UPDATE "OrderDetails"
                SET "NetAmount" = ROUND(("Quantity" * "UnitPrice")::numeric, 2),
                    "TaxableAmount" = ROUND(("Quantity" * "UnitPrice")::numeric, 2),
                    "GrossAmount" = ROUND(("Quantity" * "UnitPrice")::numeric, 2),
                    "DiscountAmount" = 0,
                    "TaxAmount" = 0,
                    "DiscountPercent" = 0,
                    "TaxRatePercent" = 0,
                    "CurrencyScale" = 2,
                    "CalculationVersion" = 1,
                    "TaxCategory" = 'Standard',
                    "TaxMode" = 'Exclusive',
                    "Direction" = 'Charge';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_OrderDetails_TaxRuleId",
                table: "OrderDetails",
                column: "TaxRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxRules_TenantId_Code_EffectiveFromUtc",
                table: "TaxRules",
                columns: new[] { "TenantId", "Code", "EffectiveFromUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxRules_TenantId_Code_IsActive",
                table: "TaxRules",
                columns: new[] { "TenantId", "Code", "IsActive" });

            migrationBuilder.AddForeignKey(
                name: "FK_OrderDetails_TaxRules_TaxRuleId",
                table: "OrderDetails",
                column: "TaxRuleId",
                principalTable: "TaxRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OrderDetails_TaxRules_TaxRuleId",
                table: "OrderDetails");

            migrationBuilder.DropTable(
                name: "TaxRules");

            migrationBuilder.DropIndex(
                name: "IX_OrderDetails_TaxRuleId",
                table: "OrderDetails");

            migrationBuilder.DropColumn(
                name: "CalculationVersion",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "CurrencyScale",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "DiscountAmount",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "NetAmount",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "TaxAmount",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "CalculationVersion",
                table: "OrderDetails");

            migrationBuilder.DropColumn(
                name: "CurrencyScale",
                table: "OrderDetails");

            migrationBuilder.DropColumn(
                name: "Direction",
                table: "OrderDetails");

            migrationBuilder.DropColumn(
                name: "DiscountAmount",
                table: "OrderDetails");

            migrationBuilder.DropColumn(
                name: "DiscountPercent",
                table: "OrderDetails");

            migrationBuilder.DropColumn(
                name: "GrossAmount",
                table: "OrderDetails");

            migrationBuilder.DropColumn(
                name: "NetAmount",
                table: "OrderDetails");

            migrationBuilder.DropColumn(
                name: "TaxAmount",
                table: "OrderDetails");

            migrationBuilder.DropColumn(
                name: "TaxCategory",
                table: "OrderDetails");

            migrationBuilder.DropColumn(
                name: "TaxEffectiveFromUtc",
                table: "OrderDetails");

            migrationBuilder.DropColumn(
                name: "TaxMode",
                table: "OrderDetails");

            migrationBuilder.DropColumn(
                name: "TaxRatePercent",
                table: "OrderDetails");

            migrationBuilder.DropColumn(
                name: "TaxRuleId",
                table: "OrderDetails");

            migrationBuilder.DropColumn(
                name: "TaxableAmount",
                table: "OrderDetails");

            migrationBuilder.AlterColumn<decimal>(
                name: "TotalAmount",
                table: "PurchaseOrders",
                type: "numeric(18,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)");
        }
    }
}
