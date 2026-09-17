using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddQuarantinedStockQuantity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "QuarantinedQuantity",
                table: "StockInHand",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockInHand_AvailableQuantity",
                table: "StockInHand",
                sql: "\"Quantity\" >= 0 AND \"ReservedQuantity\" >= 0 AND \"QuarantinedQuantity\" >= 0 AND \"ReservedQuantity\" + \"QuarantinedQuantity\" <= \"Quantity\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_StockInHand_AvailableQuantity",
                table: "StockInHand");

            migrationBuilder.DropColumn(
                name: "QuarantinedQuantity",
                table: "StockInHand");
        }
    }
}
