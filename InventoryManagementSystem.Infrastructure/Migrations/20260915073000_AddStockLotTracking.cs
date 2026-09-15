using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryManagementSystem.Infrastructure.Migrations;

public partial class AddStockLotTracking : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "BatchNumber",
            table: "StockInHand",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "ExpiryDate",
            table: "StockInHand",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.DropIndex("IX_StockInHand_ItemId_LocationId", "StockInHand");
        migrationBuilder.CreateIndex(
            "IX_StockInHand_TenantId_ItemId_LocationId_BatchNumber_ExpiryDate",
            "StockInHand",
            new[] { "TenantId", "ItemId", "LocationId", "BatchNumber", "ExpiryDate" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_StockInHand_TenantId_ItemId_LocationId_BatchNumber_ExpiryDate", "StockInHand");
        migrationBuilder.CreateIndex(
            "IX_StockInHand_ItemId_LocationId",
            "StockInHand",
            new[] { "ItemId", "LocationId" },
            unique: true);
        migrationBuilder.DropColumn("BatchNumber", "StockInHand");
        migrationBuilder.DropColumn("ExpiryDate", "StockInHand");
    }
}
