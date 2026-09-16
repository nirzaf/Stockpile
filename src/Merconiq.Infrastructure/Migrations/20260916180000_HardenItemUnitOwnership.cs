using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations;

public partial class HardenItemUnitOwnership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey("FK_Items_UnitsOfMeasure_BaseUnitId", "Items");
        migrationBuilder.DropForeignKey("FK_Items_UnitsOfMeasure_PurchaseUnitId", "Items");
        migrationBuilder.DropForeignKey("FK_Items_UnitsOfMeasure_SalesUnitId", "Items");

        migrationBuilder.AddUniqueConstraint(
            name: "AK_UnitsOfMeasure_Id_TenantId",
            table: "UnitsOfMeasure",
            columns: new[] { "Id", "TenantId" });

        migrationBuilder.CreateIndex("IX_Items_BaseUnitId_TenantId", "Items",
            new[] { "BaseUnitId", "TenantId" });
        migrationBuilder.CreateIndex("IX_Items_PurchaseUnitId_TenantId", "Items",
            new[] { "PurchaseUnitId", "TenantId" });
        migrationBuilder.CreateIndex("IX_Items_SalesUnitId_TenantId", "Items",
            new[] { "SalesUnitId", "TenantId" });

        AddUnitForeignKey(migrationBuilder, "BaseUnitId");
        AddUnitForeignKey(migrationBuilder, "PurchaseUnitId");
        AddUnitForeignKey(migrationBuilder, "SalesUnitId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey("FK_Items_UnitsOfMeasure_BaseUnitId_TenantId", "Items");
        migrationBuilder.DropForeignKey("FK_Items_UnitsOfMeasure_PurchaseUnitId_TenantId", "Items");
        migrationBuilder.DropForeignKey("FK_Items_UnitsOfMeasure_SalesUnitId_TenantId", "Items");

        migrationBuilder.DropIndex("IX_Items_BaseUnitId_TenantId", "Items");
        migrationBuilder.DropIndex("IX_Items_PurchaseUnitId_TenantId", "Items");
        migrationBuilder.DropIndex("IX_Items_SalesUnitId_TenantId", "Items");
        migrationBuilder.DropUniqueConstraint("AK_UnitsOfMeasure_Id_TenantId", "UnitsOfMeasure");

        migrationBuilder.AddForeignKey("FK_Items_UnitsOfMeasure_BaseUnitId", "Items",
            "BaseUnitId", "UnitsOfMeasure", "Id", onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey("FK_Items_UnitsOfMeasure_PurchaseUnitId", "Items",
            "PurchaseUnitId", "UnitsOfMeasure", "Id", onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey("FK_Items_UnitsOfMeasure_SalesUnitId", "Items",
            "SalesUnitId", "UnitsOfMeasure", "Id", onDelete: ReferentialAction.Restrict);
    }

    private static void AddUnitForeignKey(MigrationBuilder migrationBuilder, string unitColumn)
    {
        migrationBuilder.AddForeignKey(
            name: $"FK_Items_UnitsOfMeasure_{unitColumn}_TenantId",
            table: "Items",
            columns: new[] { unitColumn, "TenantId" },
            principalTable: "UnitsOfMeasure",
            principalColumns: new[] { "Id", "TenantId" },
            onDelete: ReferentialAction.Restrict);
    }
}
