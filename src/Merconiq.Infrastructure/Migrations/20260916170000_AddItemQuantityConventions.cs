using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations;

public partial class AddItemQuantityConventions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "UnitsOfMeasure",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                DecimalPlaces = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                IsWholeUnitOnly = table.Column<bool>(type: "boolean", nullable: false),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedBy = table.Column<string>(type: "text", nullable: true),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                UpdatedBy = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_UnitsOfMeasure", x => x.Id));

        migrationBuilder.AddColumn<int>("BaseUnitId", "Items", nullable: true);
        migrationBuilder.AddColumn<int>("PurchaseUnitId", "Items", nullable: true);
        migrationBuilder.AddColumn<int>("SalesUnitId", "Items", nullable: true);
        migrationBuilder.AddColumn<decimal>("PurchaseToBaseFactor", "Items", type: "numeric(18,6)", nullable: false, defaultValue: 1m);
        migrationBuilder.AddColumn<decimal>("SalesToBaseFactor", "Items", type: "numeric(18,6)", nullable: false, defaultValue: 1m);
        migrationBuilder.AddColumn<int>("QuantityPrecision", "Items", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<bool>("WholeUnitOnly", "Items", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<bool>("IsActive", "Items", nullable: false, defaultValue: true);

        migrationBuilder.CreateIndex("IX_UnitsOfMeasure_TenantId_Code", "UnitsOfMeasure", new[] { "TenantId", "Code" }, unique: true);
        migrationBuilder.CreateIndex("IX_Items_BaseUnitId", "Items", "BaseUnitId");
        migrationBuilder.CreateIndex("IX_Items_PurchaseUnitId", "Items", "PurchaseUnitId");
        migrationBuilder.CreateIndex("IX_Items_SalesUnitId", "Items", "SalesUnitId");
        migrationBuilder.AddForeignKey(name: "FK_Items_UnitsOfMeasure_BaseUnitId", table: "Items", column: "BaseUnitId", principalTable: "UnitsOfMeasure", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey(name: "FK_Items_UnitsOfMeasure_PurchaseUnitId", table: "Items", column: "PurchaseUnitId", principalTable: "UnitsOfMeasure", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey(name: "FK_Items_UnitsOfMeasure_SalesUnitId", table: "Items", column: "SalesUnitId", principalTable: "UnitsOfMeasure", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey("FK_Items_UnitsOfMeasure_BaseUnitId", "Items");
        migrationBuilder.DropForeignKey("FK_Items_UnitsOfMeasure_PurchaseUnitId", "Items");
        migrationBuilder.DropForeignKey("FK_Items_UnitsOfMeasure_SalesUnitId", "Items");
        migrationBuilder.DropTable("UnitsOfMeasure");
        foreach (var column in new[] { "BaseUnitId", "PurchaseUnitId", "SalesUnitId", "PurchaseToBaseFactor", "SalesToBaseFactor", "QuantityPrecision", "WholeUnitOnly", "IsActive" })
            migrationBuilder.DropColumn(column, "Items");
    }
}
