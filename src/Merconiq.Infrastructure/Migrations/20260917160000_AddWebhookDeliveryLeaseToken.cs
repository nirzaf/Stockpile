using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookDeliveryLeaseToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LeaseToken",
                table: "WebhookDeliveries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_LeaseToken",
                table: "WebhookDeliveries",
                column: "LeaseToken",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WebhookDeliveries_LeaseToken",
                table: "WebhookDeliveries");

            migrationBuilder.DropColumn(
                name: "LeaseToken",
                table: "WebhookDeliveries");
        }
    }
}
