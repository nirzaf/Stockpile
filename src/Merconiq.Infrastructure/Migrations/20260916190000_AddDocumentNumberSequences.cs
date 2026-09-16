using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations;

public partial class AddDocumentNumberSequences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "DocumentNumberSequences",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                CompanyId = table.Column<int>(type: "integer", nullable: false),
                DocumentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Period = table.Column<int>(type: "integer", nullable: false),
                Prefix = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                NextNumber = table.Column<int>(type: "integer", nullable: false),
                Version = table.Column<uint>(type: "xid", nullable: false),
                TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedBy = table.Column<string>(type: "text", nullable: true),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                UpdatedBy = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_DocumentNumberSequences", x => x.Id));

        migrationBuilder.CreateIndex("IX_DocumentNumberSequences_TenantId_CompanyId_DocumentType_Period", "DocumentNumberSequences", new[] { "TenantId", "CompanyId", "DocumentType", "Period" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("DocumentNumberSequences");
}
