using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyChartOfAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChartOfAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    AccountCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AccountType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ParentAccountId = table.Column<int>(type: "integer", nullable: true),
                    IsGroupAccount = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChartOfAccounts", x => x.Id);
                    table.UniqueConstraint("AK_ChartOfAccounts_Id_CompanyId_TenantId", x => new { x.Id, x.CompanyId, x.TenantId });
                    table.CheckConstraint("CK_ChartAccounts_AccountCode", "\"AccountCode\" <> '' AND \"AccountCode\" = btrim(\"AccountCode\")");
                    table.CheckConstraint("CK_ChartAccounts_AccountType", "\"AccountType\" <> '' AND \"AccountType\" = btrim(\"AccountType\")");
                    table.CheckConstraint("CK_ChartAccounts_Name", "\"Name\" <> '' AND \"Name\" = btrim(\"Name\")");
                    table.CheckConstraint("CK_ChartAccounts_NoSelfParent", "\"ParentAccountId\" IS NULL OR \"ParentAccountId\" <> \"Id\"");
                    table.ForeignKey(
                        name: "FK_ChartOfAccounts_ChartOfAccounts_ParentAccountId_CompanyId_T~",
                        columns: x => new { x.ParentAccountId, x.CompanyId, x.TenantId },
                        principalTable: "ChartOfAccounts",
                        principalColumns: new[] { "Id", "CompanyId", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChartOfAccounts_Companies_CompanyId_TenantId",
                        columns: x => new { x.CompanyId, x.TenantId },
                        principalTable: "Companies",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChartOfAccounts_CompanyId_TenantId",
                table: "ChartOfAccounts",
                columns: new[] { "CompanyId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChartOfAccounts_ParentAccountId_CompanyId_TenantId",
                table: "ChartOfAccounts",
                columns: new[] { "ParentAccountId", "CompanyId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "UX_ChartAccounts_Tenant_Company_Code",
                table: "ChartOfAccounts",
                columns: new[] { "TenantId", "CompanyId", "AccountCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChartOfAccounts");
        }
    }
}
