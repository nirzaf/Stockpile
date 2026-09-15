using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryManagementSystem.Infrastructure.Migrations;

/// <summary>Adds tenant ownership and database isolation support to application data.</summary>
public partial class AddTenantIsolation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[]
        {
            "AspNetUsers", "Items", "Locations", "Suppliers", "PurchaseOrders", "OrderDetails",
            "StockInHand", "StockTransactions", "AuditLogs", "WebhookSubscriptions"
        })
        {
            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: table,
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "default");
        }

        migrationBuilder.DropIndex("IX_Items_ItemCode", "Items");
        migrationBuilder.DropIndex("IX_Items_Barcode", "Items");
        migrationBuilder.DropIndex("IX_PurchaseOrders_PONumber", "PurchaseOrders");

        migrationBuilder.CreateIndex("IX_Items_TenantId_ItemCode", "Items", new[] { "TenantId", "ItemCode" }, unique: true);
        migrationBuilder.CreateIndex("IX_Items_TenantId_Barcode", "Items", new[] { "TenantId", "Barcode" }, unique: true);
        migrationBuilder.CreateIndex("IX_PurchaseOrders_TenantId_PONumber", "PurchaseOrders", new[] { "TenantId", "PONumber" }, unique: true);

        foreach (var table in new[]
        {
            "AspNetUsers", "Locations", "Suppliers", "PurchaseOrders", "OrderDetails", "StockInHand",
            "StockTransactions", "AuditLogs", "WebhookSubscriptions"
        })
        {
            migrationBuilder.CreateIndex($"IX_{table}_TenantId", table, "TenantId");
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_Items_TenantId_ItemCode", "Items");
        migrationBuilder.DropIndex("IX_Items_TenantId_Barcode", "Items");
        migrationBuilder.DropIndex("IX_PurchaseOrders_TenantId_PONumber", "PurchaseOrders");

        foreach (var table in new[]
        {
            "AspNetUsers", "Locations", "Suppliers", "PurchaseOrders", "OrderDetails", "StockInHand",
            "StockTransactions", "AuditLogs", "WebhookSubscriptions"
        })
        {
            migrationBuilder.DropIndex($"IX_{table}_TenantId", table);
        }

        migrationBuilder.CreateIndex("IX_Items_ItemCode", "Items", "ItemCode", unique: true);
        migrationBuilder.CreateIndex("IX_Items_Barcode", "Items", "Barcode", unique: true);
        migrationBuilder.CreateIndex("IX_PurchaseOrders_PONumber", "PurchaseOrders", "PONumber", unique: true);

        foreach (var table in new[]
        {
            "AspNetUsers", "Items", "Locations", "Suppliers", "PurchaseOrders", "OrderDetails",
            "StockInHand", "StockTransactions", "AuditLogs", "WebhookSubscriptions"
        })
        {
            migrationBuilder.DropColumn("TenantId", table);
        }
    }
}
