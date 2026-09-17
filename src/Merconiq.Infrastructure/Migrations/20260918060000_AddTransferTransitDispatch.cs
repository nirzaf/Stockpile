using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTransferTransitDispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DispatchedQuantity",
                table: "TransferOrderLines",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "TransferOrderLines",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_TransferOrderLines_Id_TenantId",
                table: "TransferOrderLines",
                columns: new[] { "Id", "TenantId" });

            migrationBuilder.CreateTable(
                name: "TransferTransitEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TransferOrderId = table.Column<int>(type: "integer", nullable: false),
                    TransferOrderLineId = table.Column<int>(type: "integer", nullable: false),
                    SourceDocumentLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    ItemId = table.Column<int>(type: "integer", nullable: false),
                    FromLocationId = table.Column<int>(type: "integer", nullable: false),
                    ToLocationId = table.Column<int>(type: "integer", nullable: false),
                    StockTransactionId = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    BatchNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExpiryDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UnitCost = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    TotalValue = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DispatchedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransferTransitEntries", x => x.Id);
                    table.CheckConstraint("CK_TransferTransitEntries_NonNegativeValue", "\"UnitCost\" >= 0 AND \"TotalValue\" >= 0");
                    table.CheckConstraint("CK_TransferTransitEntries_PositiveQuantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_TransferTransitEntries_Companies_CompanyId_TenantId",
                        columns: x => new { x.CompanyId, x.TenantId },
                        principalTable: "Companies",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransferTransitEntries_DocumentLineIdentities_SourceDocumen~",
                        columns: x => new { x.SourceDocumentLineId, x.TenantId },
                        principalTable: "DocumentLineIdentities",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransferTransitEntries_Items_ItemId_TenantId",
                        columns: x => new { x.ItemId, x.TenantId },
                        principalTable: "Items",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransferTransitEntries_Locations_FromLocationId_TenantId",
                        columns: x => new { x.FromLocationId, x.TenantId },
                        principalTable: "Locations",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransferTransitEntries_Locations_ToLocationId_TenantId",
                        columns: x => new { x.ToLocationId, x.TenantId },
                        principalTable: "Locations",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransferTransitEntries_StockTransactions_StockTransactionId~",
                        columns: x => new { x.StockTransactionId, x.TenantId },
                        principalTable: "StockTransactions",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransferTransitEntries_TransferOrderLines_TransferOrderLine~",
                        columns: x => new { x.TransferOrderLineId, x.TenantId },
                        principalTable: "TransferOrderLines",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransferTransitEntries_TransferOrders_TransferOrderId_Tenan~",
                        columns: x => new { x.TransferOrderId, x.TenantId },
                        principalTable: "TransferOrders",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_TransferOrderLines_DispatchedQuantity",
                table: "TransferOrderLines",
                sql: "\"DispatchedQuantity\" >= 0 AND \"DispatchedQuantity\" <= \"Quantity\"");

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitEntries_CompanyId_TenantId",
                table: "TransferTransitEntries",
                columns: new[] { "CompanyId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitEntries_FromLocationId_TenantId",
                table: "TransferTransitEntries",
                columns: new[] { "FromLocationId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitEntries_ItemId_TenantId",
                table: "TransferTransitEntries",
                columns: new[] { "ItemId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitEntries_SourceDocumentLineId_TenantId",
                table: "TransferTransitEntries",
                columns: new[] { "SourceDocumentLineId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitEntries_StockTransactionId_TenantId",
                table: "TransferTransitEntries",
                columns: new[] { "StockTransactionId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitEntries_TenantId_StockTransactionId",
                table: "TransferTransitEntries",
                columns: new[] { "TenantId", "StockTransactionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitEntries_TenantId_TransferOrderId_DispatchedAt",
                table: "TransferTransitEntries",
                columns: new[] { "TenantId", "TransferOrderId", "DispatchedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitEntries_TenantId_TransferOrderLineId_Idempot~",
                table: "TransferTransitEntries",
                columns: new[] { "TenantId", "TransferOrderLineId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitEntries_ToLocationId_TenantId",
                table: "TransferTransitEntries",
                columns: new[] { "ToLocationId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitEntries_TransferOrderId_TenantId",
                table: "TransferTransitEntries",
                columns: new[] { "TransferOrderId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitEntries_TransferOrderLineId_TenantId",
                table: "TransferTransitEntries",
                columns: new[] { "TransferOrderLineId", "TenantId" });

            migrationBuilder.Sql("""
                CREATE FUNCTION reject_transfer_transit_entry_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'Transfer transit entries are append-only.'
                        USING ERRCODE = '55000';
                END;
                $$;

                CREATE TRIGGER TR_TransferTransitEntries_AppendOnly
                    BEFORE UPDATE OR DELETE ON "TransferTransitEntries"
                    FOR EACH ROW
                    EXECUTE FUNCTION reject_transfer_transit_entry_mutation();

                CREATE TRIGGER TR_TransferTransitEntries_NoTruncate
                    BEFORE TRUNCATE ON "TransferTransitEntries"
                    FOR EACH STATEMENT
                    EXECUTE FUNCTION reject_transfer_transit_entry_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "TransferTransitEntries") OR
                       EXISTS (SELECT 1 FROM "TransferOrderLines" WHERE "DispatchedQuantity" > 0) THEN
                        RAISE EXCEPTION 'Cannot downgrade transfer dispatch while dispatched inventory exists.'
                            USING ERRCODE = '55000';
                    END IF;
                END;
                $$;
                """);
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS TR_TransferTransitEntries_NoTruncate ON "TransferTransitEntries";
                DROP TRIGGER IF EXISTS TR_TransferTransitEntries_AppendOnly ON "TransferTransitEntries";
                DROP FUNCTION IF EXISTS reject_transfer_transit_entry_mutation();
                """);

            migrationBuilder.DropTable(
                name: "TransferTransitEntries");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_TransferOrderLines_Id_TenantId",
                table: "TransferOrderLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TransferOrderLines_DispatchedQuantity",
                table: "TransferOrderLines");

            migrationBuilder.DropColumn(
                name: "DispatchedQuantity",
                table: "TransferOrderLines");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "TransferOrderLines");
        }
    }
}
