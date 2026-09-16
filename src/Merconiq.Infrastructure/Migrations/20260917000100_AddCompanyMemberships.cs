using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyMemberships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_AspNetUsers_Id_TenantId",
                table: "AspNetUsers",
                columns: new[] { "Id", "TenantId" });

            migrationBuilder.CreateTable(
                name: "CompanyMemberships",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Capabilities = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompanyMemberships_AspNetUsers_UserId_TenantId",
                        columns: x => new { x.UserId, x.TenantId },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CompanyMemberships_Companies_CompanyId_TenantId",
                        columns: x => new { x.CompanyId, x.TenantId },
                        principalTable: "Companies",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyMemberships_CompanyId_TenantId",
                table: "CompanyMemberships",
                columns: new[] { "CompanyId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyMemberships_TenantId_CompanyId_UserId",
                table: "CompanyMemberships",
                columns: new[] { "TenantId", "CompanyId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyMemberships_TenantId_UserId_IsActive",
                table: "CompanyMemberships",
                columns: new[] { "TenantId", "UserId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyMemberships_UserId_TenantId",
                table: "CompanyMemberships",
                columns: new[] { "UserId", "TenantId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyMemberships");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_AspNetUsers_Id_TenantId",
                table: "AspNetUsers");

        }
    }
}
