using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTransferTransitReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_TransferTransitEntries_Id_TenantId",
                table: "TransferTransitEntries",
                columns: new[] { "Id", "TenantId" });

            migrationBuilder.CreateTable(
                name: "TransferTransitReceipts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TransferTransitEntryId = table.Column<int>(type: "integer", nullable: false),
                    StockTransactionId = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    RemainingQuantity = table.Column<int>(type: "integer", nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    TotalValue = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    RemainingValue = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReceivedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransferTransitReceipts", x => x.Id);
                    table.CheckConstraint("CK_TransferTransitReceipts_NonNegativeRemainder", "\"RemainingQuantity\" >= 0 AND \"RemainingValue\" >= 0");
                    table.CheckConstraint("CK_TransferTransitReceipts_NonNegativeValue", "\"UnitCost\" >= 0 AND \"TotalValue\" >= 0");
                    table.CheckConstraint("CK_TransferTransitReceipts_PositiveQuantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_TransferTransitReceipts_StockTransactions_StockTransactionI~",
                        columns: x => new { x.StockTransactionId, x.TenantId },
                        principalTable: "StockTransactions",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransferTransitReceipts_TransferTransitEntries_TransferTran~",
                        columns: x => new { x.TransferTransitEntryId, x.TenantId },
                        principalTable: "TransferTransitEntries",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitReceipts_StockTransactionId_TenantId",
                table: "TransferTransitReceipts",
                columns: new[] { "StockTransactionId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitReceipts_TenantId_StockTransactionId",
                table: "TransferTransitReceipts",
                columns: new[] { "TenantId", "StockTransactionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitReceipts_TenantId_TransferTransitEntryId_Ide~",
                table: "TransferTransitReceipts",
                columns: new[] { "TenantId", "TransferTransitEntryId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitReceipts_TransferTransitEntryId_TenantId",
                table: "TransferTransitReceipts",
                columns: new[] { "TransferTransitEntryId", "TenantId" });

            migrationBuilder.Sql("""
                CREATE FUNCTION reject_transfer_transit_receipt_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'Transfer transit receipts are append-only.'
                        USING ERRCODE = '55000';
                END;
                $$;

                CREATE TRIGGER TR_TransferTransitReceipts_AppendOnly
                    BEFORE UPDATE OR DELETE ON "TransferTransitReceipts"
                    FOR EACH ROW
                    EXECUTE FUNCTION reject_transfer_transit_receipt_mutation();

                CREATE TRIGGER TR_TransferTransitReceipts_NoTruncate
                    BEFORE TRUNCATE ON "TransferTransitReceipts"
                    FOR EACH STATEMENT
                    EXECUTE FUNCTION reject_transfer_transit_receipt_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "TransferTransitReceipts") THEN
                        RAISE EXCEPTION 'Cannot downgrade transfer receipts while received inventory exists.'
                            USING ERRCODE = '55000';
                    END IF;
                END;
                $$;
                """);
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS TR_TransferTransitReceipts_NoTruncate ON "TransferTransitReceipts";
                DROP TRIGGER IF EXISTS TR_TransferTransitReceipts_AppendOnly ON "TransferTransitReceipts";
                DROP FUNCTION IF EXISTS reject_transfer_transit_receipt_mutation();
                """);
            migrationBuilder.DropTable(
                name: "TransferTransitReceipts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_TransferTransitEntries_Id_TenantId",
                table: "TransferTransitEntries");
        }
    }
}
