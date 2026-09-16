using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations;

public partial class AddUnitExternalId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("ExternalId", "UnitsOfMeasure", type: "character varying(128)", maxLength: 128, nullable: false, defaultValue: "");
        migrationBuilder.CreateIndex("IX_UnitsOfMeasure_TenantId_ExternalId", "UnitsOfMeasure", new[] { "TenantId", "ExternalId" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_UnitsOfMeasure_TenantId_ExternalId", "UnitsOfMeasure");
        migrationBuilder.DropColumn("ExternalId", "UnitsOfMeasure");
    }
}
