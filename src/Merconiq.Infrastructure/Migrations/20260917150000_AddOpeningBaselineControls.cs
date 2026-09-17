using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOpeningBaselineControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CutoverAt",
                table: "OpeningStockImports",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");

            migrationBuilder.AddColumn<int>(
                name: "StockTransactionId",
                table: "OpeningStockImportLines",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OpeningStockCorrections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OpeningStockImportId = table.Column<int>(type: "integer", nullable: false),
                    CorrectionReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ApprovalReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CorrectedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CorrectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LineCount = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpeningStockCorrections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpeningStockCorrections_OpeningStockImports_OpeningStockImp~",
                        columns: x => new { x.OpeningStockImportId, x.TenantId },
                        principalTable: "OpeningStockImports",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OpeningStockImports_TenantId",
                table: "OpeningStockImports",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpeningStockImportLines_StockTransactionId_TenantId",
                table: "OpeningStockImportLines",
                columns: new[] { "StockTransactionId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_OpeningStockCorrections_OpeningStockImportId_TenantId",
                table: "OpeningStockCorrections",
                columns: new[] { "OpeningStockImportId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_OpeningStockCorrections_TenantId_ApprovalReference",
                table: "OpeningStockCorrections",
                columns: new[] { "TenantId", "ApprovalReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpeningStockCorrections_TenantId_CorrectionReference",
                table: "OpeningStockCorrections",
                columns: new[] { "TenantId", "CorrectionReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpeningStockCorrections_TenantId_OpeningStockImportId",
                table: "OpeningStockCorrections",
                columns: new[] { "TenantId", "OpeningStockImportId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_OpeningStockImportLines_StockTransactions_StockTransactionI~",
                table: "OpeningStockImportLines",
                columns: new[] { "StockTransactionId", "TenantId" },
                principalTable: "StockTransactions",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE FUNCTION reject_opening_baseline_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'Approved opening baselines and corrections are append-only.'
                        USING ERRCODE = '55000';
                END;
                $$;

                CREATE TRIGGER TR_OpeningStockImports_AppendOnly
                    BEFORE UPDATE OR DELETE ON "OpeningStockImports"
                    FOR EACH ROW
                    EXECUTE FUNCTION reject_opening_baseline_mutation();

                CREATE TRIGGER TR_OpeningStockImportLines_AppendOnly
                    BEFORE UPDATE OR DELETE ON "OpeningStockImportLines"
                    FOR EACH ROW
                    EXECUTE FUNCTION reject_opening_baseline_mutation();

                CREATE TRIGGER TR_OpeningStockCorrections_AppendOnly
                    BEFORE UPDATE OR DELETE ON "OpeningStockCorrections"
                    FOR EACH ROW
                    EXECUTE FUNCTION reject_opening_baseline_mutation();

                CREATE TRIGGER TR_OpeningStockImports_NoTruncate
                    BEFORE TRUNCATE ON "OpeningStockImports"
                    FOR EACH STATEMENT
                    EXECUTE FUNCTION reject_opening_baseline_mutation();

                CREATE TRIGGER TR_OpeningStockImportLines_NoTruncate
                    BEFORE TRUNCATE ON "OpeningStockImportLines"
                    FOR EACH STATEMENT
                    EXECUTE FUNCTION reject_opening_baseline_mutation();

                CREATE TRIGGER TR_OpeningStockCorrections_NoTruncate
                    BEFORE TRUNCATE ON "OpeningStockCorrections"
                    FOR EACH STATEMENT
                    EXECUTE FUNCTION reject_opening_baseline_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS TR_OpeningStockCorrections_NoTruncate ON "OpeningStockCorrections";
                DROP TRIGGER IF EXISTS TR_OpeningStockImportLines_NoTruncate ON "OpeningStockImportLines";
                DROP TRIGGER IF EXISTS TR_OpeningStockImports_NoTruncate ON "OpeningStockImports";
                DROP TRIGGER IF EXISTS TR_OpeningStockCorrections_AppendOnly ON "OpeningStockCorrections";
                DROP TRIGGER IF EXISTS TR_OpeningStockImportLines_AppendOnly ON "OpeningStockImportLines";
                DROP TRIGGER IF EXISTS TR_OpeningStockImports_AppendOnly ON "OpeningStockImports";
                DROP FUNCTION IF EXISTS reject_opening_baseline_mutation();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_OpeningStockImportLines_StockTransactions_StockTransactionI~",
                table: "OpeningStockImportLines");

            migrationBuilder.DropTable(
                name: "OpeningStockCorrections");

            migrationBuilder.DropIndex(
                name: "IX_OpeningStockImports_TenantId",
                table: "OpeningStockImports");

            migrationBuilder.DropIndex(
                name: "IX_OpeningStockImportLines_StockTransactionId_TenantId",
                table: "OpeningStockImportLines");

            migrationBuilder.DropColumn(
                name: "CutoverAt",
                table: "OpeningStockImports");

            migrationBuilder.DropColumn(
                name: "StockTransactionId",
                table: "OpeningStockImportLines");
        }
    }
}
