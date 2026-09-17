using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTransferTransitSettlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_TransferTransitEntries_Id_TenantId",
                table: "TransferTransitEntries",
                columns: new[] { "Id", "TenantId" });

            migrationBuilder.CreateTable(
                name: "TransferTransitSettlements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TransferTransitEntryId = table.Column<int>(type: "integer", nullable: false),
                    TransferOrderId = table.Column<int>(type: "integer", nullable: false),
                    TransferOrderLineId = table.Column<int>(type: "integer", nullable: false),
                    SourceDocumentLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    ItemId = table.Column<int>(type: "integer", nullable: false),
                    FromLocationId = table.Column<int>(type: "integer", nullable: false),
                    ToLocationId = table.Column<int>(type: "integer", nullable: false),
                    StockTransactionId = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    SettlementType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BatchNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExpiryDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UnitCost = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    TotalValue = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SettledBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SettledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransferTransitSettlements", x => x.Id);
                    table.CheckConstraint("CK_TransferTransitSettlements_NonNegativeValue", "\"UnitCost\" >= 0 AND \"TotalValue\" >= 0");
                    table.CheckConstraint("CK_TransferTransitSettlements_PositiveQuantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_TransferTransitSettlements_Companies_CompanyId_TenantId",
                        columns: x => new { x.CompanyId, x.TenantId },
                        principalTable: "Companies",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransferTransitSettlements_DocumentLineIdentities_SourceDoc~",
                        columns: x => new { x.SourceDocumentLineId, x.TenantId },
                        principalTable: "DocumentLineIdentities",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransferTransitSettlements_Items_ItemId_TenantId",
                        columns: x => new { x.ItemId, x.TenantId },
                        principalTable: "Items",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransferTransitSettlements_Locations_FromLocationId_TenantId",
                        columns: x => new { x.FromLocationId, x.TenantId },
                        principalTable: "Locations",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransferTransitSettlements_Locations_ToLocationId_TenantId",
                        columns: x => new { x.ToLocationId, x.TenantId },
                        principalTable: "Locations",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransferTransitSettlements_StockTransactions_StockTransacti~",
                        columns: x => new { x.StockTransactionId, x.TenantId },
                        principalTable: "StockTransactions",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransferTransitSettlements_TransferOrderLines_TransferOrder~",
                        columns: x => new { x.TransferOrderLineId, x.TenantId },
                        principalTable: "TransferOrderLines",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransferTransitSettlements_TransferOrders_TransferOrderId_T~",
                        columns: x => new { x.TransferOrderId, x.TenantId },
                        principalTable: "TransferOrders",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransferTransitSettlements_TransferTransitEntries_TransferT~",
                        columns: x => new { x.TransferTransitEntryId, x.TenantId },
                        principalTable: "TransferTransitEntries",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitSettlements_CompanyId_TenantId",
                table: "TransferTransitSettlements",
                columns: new[] { "CompanyId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitSettlements_FromLocationId_TenantId",
                table: "TransferTransitSettlements",
                columns: new[] { "FromLocationId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitSettlements_ItemId_TenantId",
                table: "TransferTransitSettlements",
                columns: new[] { "ItemId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitSettlements_SourceDocumentLineId_TenantId",
                table: "TransferTransitSettlements",
                columns: new[] { "SourceDocumentLineId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitSettlements_StockTransactionId_TenantId",
                table: "TransferTransitSettlements",
                columns: new[] { "StockTransactionId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitSettlements_TenantId_TransferOrderId_Settled~",
                table: "TransferTransitSettlements",
                columns: new[] { "TenantId", "TransferOrderId", "SettledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitSettlements_TenantId_TransferTransitEntryId_~",
                table: "TransferTransitSettlements",
                columns: new[] { "TenantId", "TransferTransitEntryId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitSettlements_ToLocationId_TenantId",
                table: "TransferTransitSettlements",
                columns: new[] { "ToLocationId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitSettlements_TransferOrderId_TenantId",
                table: "TransferTransitSettlements",
                columns: new[] { "TransferOrderId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitSettlements_TransferOrderLineId_TenantId",
                table: "TransferTransitSettlements",
                columns: new[] { "TransferOrderLineId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitSettlements_TransferTransitEntryId_TenantId",
                table: "TransferTransitSettlements",
                columns: new[] { "TransferTransitEntryId", "TenantId" });

            migrationBuilder.Sql("""
                CREATE FUNCTION reject_transfer_transit_settlement_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'Transfer transit settlements are append-only.'
                        USING ERRCODE = '55000';
                END;
                $$;

                CREATE TRIGGER TR_TransferTransitSettlements_AppendOnly
                    BEFORE UPDATE OR DELETE ON "TransferTransitSettlements"
                    FOR EACH ROW
                    EXECUTE FUNCTION reject_transfer_transit_settlement_mutation();

                CREATE TRIGGER TR_TransferTransitSettlements_NoTruncate
                    BEFORE TRUNCATE ON "TransferTransitSettlements"
                    FOR EACH STATEMENT
                    EXECUTE FUNCTION reject_transfer_transit_settlement_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS TR_TransferTransitSettlements_NoTruncate ON "TransferTransitSettlements";
                DROP TRIGGER IF EXISTS TR_TransferTransitSettlements_AppendOnly ON "TransferTransitSettlements";
                DROP FUNCTION IF EXISTS reject_transfer_transit_settlement_mutation();
                """);
            migrationBuilder.DropTable(
                name: "TransferTransitSettlements");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_TransferTransitEntries_Id_TenantId",
                table: "TransferTransitEntries");
        }
    }
}
