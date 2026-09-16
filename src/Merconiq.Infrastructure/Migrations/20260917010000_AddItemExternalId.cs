using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations;

public partial class AddItemExternalId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ExternalId",
            table: "Items",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Items_TenantId_ExternalId",
            table: "Items",
            columns: new[] { "TenantId", "ExternalId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_Items_TenantId_ExternalId", "Items");
        migrationBuilder.DropColumn("ExternalId", "Items");
    }
}
