using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTransferOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TransferOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    FromLocationId = table.Column<int>(type: "integer", nullable: false),
                    ToLocationId = table.Column<int>(type: "integer", nullable: false),
                    OrderDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransferOrders", x => x.Id);
                    table.UniqueConstraint("AK_TransferOrders_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("CK_TransferOrders_DistinctLocations", "\"FromLocationId\" <> \"ToLocationId\"");
                    table.ForeignKey(
                        name: "FK_TransferOrders_Companies_CompanyId_TenantId",
                        columns: x => new { x.CompanyId, x.TenantId },
                        principalTable: "Companies",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransferOrders_DocumentIdentities_DocumentId_TenantId",
                        columns: x => new { x.DocumentId, x.TenantId },
                        principalTable: "DocumentIdentities",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransferOrders_Locations_FromLocationId_TenantId",
                        columns: x => new { x.FromLocationId, x.TenantId },
                        principalTable: "Locations",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransferOrders_Locations_ToLocationId_TenantId",
                        columns: x => new { x.ToLocationId, x.TenantId },
                        principalTable: "Locations",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TransferOrderLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DocumentLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    TransferOrderId = table.Column<int>(type: "integer", nullable: false),
                    ItemId = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    BatchNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExpiryDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReservationVersion = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransferOrderLines", x => x.Id);
                    table.CheckConstraint("CK_TransferOrderLines_PositiveQuantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_TransferOrderLines_DocumentLineIdentities_DocumentLineId_Te~",
                        columns: x => new { x.DocumentLineId, x.TenantId },
                        principalTable: "DocumentLineIdentities",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransferOrderLines_Items_ItemId_TenantId",
                        columns: x => new { x.ItemId, x.TenantId },
                        principalTable: "Items",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransferOrderLines_TransferOrders_TransferOrderId_TenantId",
                        columns: x => new { x.TransferOrderId, x.TenantId },
                        principalTable: "TransferOrders",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TransferOrderLines_DocumentLineId_TenantId",
                table: "TransferOrderLines",
                columns: new[] { "DocumentLineId", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransferOrderLines_ItemId_TenantId",
                table: "TransferOrderLines",
                columns: new[] { "ItemId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferOrderLines_TransferOrderId_TenantId",
                table: "TransferOrderLines",
                columns: new[] { "TransferOrderId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferOrders_CompanyId_TenantId",
                table: "TransferOrders",
                columns: new[] { "CompanyId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferOrders_DocumentId_TenantId",
                table: "TransferOrders",
                columns: new[] { "DocumentId", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransferOrders_FromLocationId_TenantId",
                table: "TransferOrders",
                columns: new[] { "FromLocationId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferOrders_TenantId_CompanyId_Status",
                table: "TransferOrders",
                columns: new[] { "TenantId", "CompanyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferOrders_ToLocationId_TenantId",
                table: "TransferOrders",
                columns: new[] { "ToLocationId", "TenantId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TransferOrderLines");

            migrationBuilder.DropTable(
                name: "TransferOrders");
        }
    }
}
