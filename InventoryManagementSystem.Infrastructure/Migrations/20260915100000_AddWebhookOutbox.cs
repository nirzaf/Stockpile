using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace InventoryManagementSystem.Infrastructure.Migrations;

public partial class AddWebhookOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "WebhookDeliveries",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                EventId = table.Column<Guid>(type: "uuid", nullable: false),
                SubscriptionId = table.Column<int>(type: "integer", nullable: false),
                EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                AttemptCount = table.Column<int>(type: "integer", nullable: false),
                NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastStatusCode = table.Column<int>(type: "integer", nullable: true),
                LastResponse = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                LastError = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_WebhookDeliveries", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_WebhookDeliveries_EventId_SubscriptionId",
            table: "WebhookDeliveries",
            columns: new[] { "EventId", "SubscriptionId" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_WebhookDeliveries_Status_NextAttemptAt",
            table: "WebhookDeliveries",
            columns: new[] { "Status", "NextAttemptAt" });
        migrationBuilder.CreateIndex(
            name: "IX_WebhookDeliveries_TenantId",
            table: "WebhookDeliveries",
            column: "TenantId");
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("WebhookDeliveries");
}
