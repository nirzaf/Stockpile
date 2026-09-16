using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Merconiq.Infrastructure.Migrations;

public partial class AddCompanyBranchOwnership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Companies",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedBy = table.Column<string>(type: "text", nullable: true),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                UpdatedBy = table.Column<string>(type: "text", nullable: true),
                Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                LegalName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                TradingName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                RegistrationNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                TaxIdentifier = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                BaseCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                CountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                IsActive = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Companies", x => x.Id));

        migrationBuilder.AddUniqueConstraint("AK_Companies_Id_TenantId", "Companies", new[] { "Id", "TenantId" });

        migrationBuilder.CreateTable(
            name: "Branches",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedBy = table.Column<string>(type: "text", nullable: true),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                UpdatedBy = table.Column<string>(type: "text", nullable: true),
                CompanyId = table.Column<int>(type: "integer", nullable: false),
                Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                TimeZoneId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Branches", x => x.Id);
                table.ForeignKey("FK_Branches_Companies_CompanyId_TenantId", x => new { x.CompanyId, x.TenantId },
                    "Companies", new[] { "Id", "TenantId" }, onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.AddUniqueConstraint("AK_Branches_Id_TenantId", "Branches", new[] { "Id", "TenantId" });

        migrationBuilder.AddColumn<int>(
            name: "BranchId",
            table: "Locations",
            type: "integer",
            nullable: true);

        migrationBuilder.CreateIndex("IX_Companies_TenantId_Code", "Companies", new[] { "TenantId", "Code" }, unique: true);
        migrationBuilder.CreateIndex("IX_Branches_TenantId_CompanyId_Code", "Branches", new[] { "TenantId", "CompanyId", "Code" }, unique: true);
        migrationBuilder.CreateIndex("IX_Branches_CompanyId_TenantId", "Branches", new[] { "CompanyId", "TenantId" });
        migrationBuilder.CreateIndex("IX_Locations_TenantId_BranchId", "Locations", new[] { "TenantId", "BranchId" });
        migrationBuilder.AddForeignKey(
            name: "FK_Locations_Branches_BranchId_TenantId",
            table: "Locations",
            columns: new[] { "BranchId", "TenantId" },
            principalTable: "Branches",
            principalColumns: new[] { "Id", "TenantId" },
            onDelete: ReferentialAction.SetNull);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Locations_Branches_BranchId_TenantId",
            table: "Locations");
        migrationBuilder.DropIndex("Locations", "IX_Locations_TenantId_BranchId");
        migrationBuilder.DropColumn("BranchId", "Locations");
        migrationBuilder.DropUniqueConstraint("AK_Branches_Id_TenantId", "Branches");
        migrationBuilder.DropTable("Branches");
        migrationBuilder.DropUniqueConstraint("AK_Companies_Id_TenantId", "Companies");
        migrationBuilder.DropTable("Companies");
    }
}
