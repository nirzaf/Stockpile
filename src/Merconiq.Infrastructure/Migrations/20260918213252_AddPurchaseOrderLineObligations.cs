using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseOrderLineObligations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReceivingRevision",
                table: "PurchaseOrders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AcceptedQuantity",
                table: "OrderDetails",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ReceivedQuantity",
                table: "OrderDetails",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RejectedQuantity",
                table: "OrderDetails",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseOrders_ReceivingRevision",
                table: "PurchaseOrders",
                sql: "\"ReceivingRevision\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderDetails_ReceivingQuantities",
                table: "OrderDetails",
                sql: "\"ReceivedQuantity\" >= 0 AND \"AcceptedQuantity\" >= 0 AND \"RejectedQuantity\" >= 0 AND \"ReceivedQuantity\" <= CASE WHEN \"Direction\" = 'Charge' AND \"Quantity\" > 0 THEN \"Quantity\" ELSE 0 END AND (\"AcceptedQuantity\"::bigint + \"RejectedQuantity\"::bigint) <= \"ReceivedQuantity\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseOrders_ReceivingRevision",
                table: "PurchaseOrders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderDetails_ReceivingQuantities",
                table: "OrderDetails");

            migrationBuilder.DropColumn(
                name: "ReceivingRevision",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "AcceptedQuantity",
                table: "OrderDetails");

            migrationBuilder.DropColumn(
                name: "ReceivedQuantity",
                table: "OrderDetails");

            migrationBuilder.DropColumn(
                name: "RejectedQuantity",
                table: "OrderDetails");
        }
    }
}
