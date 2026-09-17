using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMasterDataExternalIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "Suppliers",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "Locations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "Companies",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "Branches",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_TenantId_ExternalId",
                table: "Suppliers",
                columns: new[] { "TenantId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Locations_TenantId_ExternalId",
                table: "Locations",
                columns: new[] { "TenantId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Companies_TenantId_ExternalId",
                table: "Companies",
                columns: new[] { "TenantId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Branches_TenantId_ExternalId",
                table: "Branches",
                columns: new[] { "TenantId", "ExternalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Suppliers_TenantId_ExternalId",
                table: "Suppliers");

            migrationBuilder.DropIndex(
                name: "IX_Locations_TenantId_ExternalId",
                table: "Locations");

            migrationBuilder.DropIndex(
                name: "IX_Companies_TenantId_ExternalId",
                table: "Companies");

            migrationBuilder.DropIndex(
                name: "IX_Branches_TenantId_ExternalId",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "Branches");
        }
    }
}
