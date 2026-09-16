using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Merconiq.Infrastructure.Migrations;

public partial class AddIdempotencyRecords : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "IdempotencyRecords",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Scope = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                AttemptCount = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ResponseStatusCode = table.Column<int>(type: "integer", nullable: true),
                ResponseBody = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                LastError = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_IdempotencyRecords", x => x.Id));

        migrationBuilder.CreateIndex("IX_IdempotencyRecords_ExpiresAt", "IdempotencyRecords", "ExpiresAt");
        migrationBuilder.CreateIndex("IX_IdempotencyRecords_TenantId_Scope_Key", "IdempotencyRecords", new[] { "TenantId", "Scope", "Key" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("IdempotencyRecords");
}
