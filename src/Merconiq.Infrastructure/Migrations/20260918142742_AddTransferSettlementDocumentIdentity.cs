using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTransferSettlementDocumentIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DocumentId",
                table: "TransferTransitSettlements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DocumentLineId",
                table: "TransferTransitSettlements",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitSettlements_DocumentId_DocumentLineId_Tenant~",
                table: "TransferTransitSettlements",
                columns: new[] { "DocumentId", "DocumentLineId", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitSettlements_DocumentId_TenantId",
                table: "TransferTransitSettlements",
                columns: new[] { "DocumentId", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitSettlements_TenantId_DocumentId",
                table: "TransferTransitSettlements",
                columns: new[] { "TenantId", "DocumentId" },
                unique: true,
                filter: "\"DocumentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TransferTransitSettlements_TenantId_DocumentLineId",
                table: "TransferTransitSettlements",
                columns: new[] { "TenantId", "DocumentLineId" },
                unique: true,
                filter: "\"DocumentLineId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TransferTransitSettlements_DocumentIdentityPair",
                table: "TransferTransitSettlements",
                sql: "(\"DocumentId\" IS NULL AND \"DocumentLineId\" IS NULL) OR (\"DocumentId\" IS NOT NULL AND \"DocumentLineId\" IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_TransferTransitSettlements_DocumentIdentities_DocumentId_Te~",
                table: "TransferTransitSettlements",
                columns: new[] { "DocumentId", "TenantId" },
                principalTable: "DocumentIdentities",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TransferTransitSettlements_DocumentLineIdentities_DocumentI~",
                table: "TransferTransitSettlements",
                columns: new[] { "DocumentId", "DocumentLineId", "TenantId" },
                principalTable: "DocumentLineIdentities",
                principalColumns: new[] { "DocumentId", "Id", "TenantId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TransferTransitSettlements_DocumentIdentities_DocumentId_Te~",
                table: "TransferTransitSettlements");

            migrationBuilder.DropForeignKey(
                name: "FK_TransferTransitSettlements_DocumentLineIdentities_DocumentI~",
                table: "TransferTransitSettlements");

            migrationBuilder.DropIndex(
                name: "IX_TransferTransitSettlements_DocumentId_DocumentLineId_Tenant~",
                table: "TransferTransitSettlements");

            migrationBuilder.DropIndex(
                name: "IX_TransferTransitSettlements_DocumentId_TenantId",
                table: "TransferTransitSettlements");

            migrationBuilder.DropIndex(
                name: "IX_TransferTransitSettlements_TenantId_DocumentId",
                table: "TransferTransitSettlements");

            migrationBuilder.DropIndex(
                name: "IX_TransferTransitSettlements_TenantId_DocumentLineId",
                table: "TransferTransitSettlements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TransferTransitSettlements_DocumentIdentityPair",
                table: "TransferTransitSettlements");

            migrationBuilder.DropColumn(
                name: "DocumentId",
                table: "TransferTransitSettlements");

            migrationBuilder.DropColumn(
                name: "DocumentLineId",
                table: "TransferTransitSettlements");
        }
    }
}
