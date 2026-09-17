using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyCurrencyScale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CurrencyScale",
                table: "Companies",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Companies_CurrencyScale",
                table: "Companies",
                sql: "\"CurrencyScale\" IS NULL OR \"CurrencyScale\" BETWEEN 0 AND 4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Companies_CurrencyScale",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "CurrencyScale",
                table: "Companies");
        }
    }
}
