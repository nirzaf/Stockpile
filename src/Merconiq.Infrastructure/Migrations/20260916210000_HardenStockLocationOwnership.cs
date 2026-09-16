using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HardenStockLocationOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StockInHand_Locations_LocationId",
                table: "StockInHand");

            migrationBuilder.DropForeignKey(
                name: "FK_StockTransactions_Locations_FromLocationId",
                table: "StockTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_StockTransactions_Locations_ToLocationId",
                table: "StockTransactions");

            migrationBuilder.DropIndex(
                name: "IX_StockTransactions_FromLocationId",
                table: "StockTransactions");

            migrationBuilder.DropIndex(
                name: "IX_StockTransactions_ToLocationId",
                table: "StockTransactions");

            migrationBuilder.DropIndex(
                name: "IX_StockInHand_LocationId",
                table: "StockInHand");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Locations_Id_TenantId",
                table: "Locations",
                columns: new[] { "Id", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransactions_FromLocationId_TenantId",
                table: "StockTransactions",
                columns: new[] { "FromLocationId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransactions_ToLocationId_TenantId",
                table: "StockTransactions",
                columns: new[] { "ToLocationId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockInHand_LocationId_TenantId",
                table: "StockInHand",
                columns: new[] { "LocationId", "TenantId" });

            migrationBuilder.AddForeignKey(
                name: "FK_StockInHand_Locations_LocationId_TenantId",
                table: "StockInHand",
                columns: new[] { "LocationId", "TenantId" },
                principalTable: "Locations",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_StockTransactions_Locations_FromLocationId_TenantId",
                table: "StockTransactions",
                columns: new[] { "FromLocationId", "TenantId" },
                principalTable: "Locations",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockTransactions_Locations_ToLocationId_TenantId",
                table: "StockTransactions",
                columns: new[] { "ToLocationId", "TenantId" },
                principalTable: "Locations",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StockInHand_Locations_LocationId_TenantId",
                table: "StockInHand");

            migrationBuilder.DropForeignKey(
                name: "FK_StockTransactions_Locations_FromLocationId_TenantId",
                table: "StockTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_StockTransactions_Locations_ToLocationId_TenantId",
                table: "StockTransactions");

            migrationBuilder.DropIndex(
                name: "IX_StockTransactions_FromLocationId_TenantId",
                table: "StockTransactions");

            migrationBuilder.DropIndex(
                name: "IX_StockTransactions_ToLocationId_TenantId",
                table: "StockTransactions");

            migrationBuilder.DropIndex(
                name: "IX_StockInHand_LocationId_TenantId",
                table: "StockInHand");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Locations_Id_TenantId",
                table: "Locations");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransactions_FromLocationId",
                table: "StockTransactions",
                column: "FromLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransactions_ToLocationId",
                table: "StockTransactions",
                column: "ToLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_StockInHand_LocationId",
                table: "StockInHand",
                column: "LocationId");

            migrationBuilder.AddForeignKey(
                name: "FK_StockInHand_Locations_LocationId",
                table: "StockInHand",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_StockTransactions_Locations_FromLocationId",
                table: "StockTransactions",
                column: "FromLocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockTransactions_Locations_ToLocationId",
                table: "StockTransactions",
                column: "ToLocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
