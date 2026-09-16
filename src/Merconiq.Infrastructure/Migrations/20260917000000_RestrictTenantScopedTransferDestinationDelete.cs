using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RestrictTenantScopedTransferDestinationDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StockTransactions_Locations_ToLocationId_TenantId",
                table: "StockTransactions");

            migrationBuilder.AddForeignKey(
                name: "FK_StockTransactions_Locations_ToLocationId_TenantId",
                table: "StockTransactions",
                columns: new[] { "ToLocationId", "TenantId" },
                principalTable: "Locations",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StockTransactions_Locations_ToLocationId_TenantId",
                table: "StockTransactions");

            migrationBuilder.AddForeignKey(
                name: "FK_StockTransactions_Locations_ToLocationId_TenantId",
                table: "StockTransactions",
                columns: new[] { "ToLocationId", "TenantId" },
                principalTable: "Locations",
                principalColumns: new[] { "Id", "TenantId" },
                // TenantId is required; reverting must not restore composite SET NULL semantics.
                onDelete: ReferentialAction.Restrict);
        }
    }
}
