using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStockReservationAllocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_StockReservations_Id_TenantId",
                table: "StockReservations",
                columns: new[] { "Id", "TenantId" });

            migrationBuilder.CreateTable(
                name: "StockReservationAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReservationId = table.Column<int>(type: "integer", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    BatchNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExpiryDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    ConsumedQuantity = table.Column<int>(type: "integer", nullable: false),
                    ExpiryExceptionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockReservationAllocations", x => x.Id);
                    table.CheckConstraint("CK_StockReservationAllocations_Quantities", "\"Quantity\" > 0 AND \"ConsumedQuantity\" >= 0 AND \"ConsumedQuantity\" <= \"Quantity\"");
                    table.ForeignKey(
                        name: "FK_StockReservationAllocations_StockReservations_ReservationId~",
                        columns: x => new { x.ReservationId, x.TenantId },
                        principalTable: "StockReservations",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockReservationAllocations_ReservationId_TenantId",
                table: "StockReservationAllocations",
                columns: new[] { "ReservationId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockReservationAllocations_TenantId_ReservationId",
                table: "StockReservationAllocations",
                columns: new[] { "TenantId", "ReservationId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockReservationAllocations_TenantId_ReservationId_Ordinal",
                table: "StockReservationAllocations",
                columns: new[] { "TenantId", "ReservationId", "Ordinal" },
                unique: true);

            migrationBuilder.Sql(
                """
                INSERT INTO "StockReservationAllocations"
                    ("ReservationId", "Ordinal", "BatchNumber", "ExpiryDate", "Quantity",
                     "ConsumedQuantity", "ExpiryExceptionReason", "TenantId", "CreatedAt", "CreatedBy",
                     "UpdatedAt", "UpdatedBy")
                SELECT "Id", 0, "BatchNumber", "ExpiryDate", "Quantity", "ConsumedQuantity",
                       "ExpiryExceptionReason", "TenantId", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy"
                FROM "StockReservations";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                LOCK TABLE "StockReservationAllocations", "StockReservations" IN ACCESS EXCLUSIVE MODE;
                DO $migration$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "StockReservationAllocations" allocation
                        JOIN "StockReservations" reservation
                          ON reservation."Id" = allocation."ReservationId"
                         AND reservation."TenantId" = allocation."TenantId"
                        GROUP BY reservation."Id", reservation."TenantId"
                        HAVING COUNT(*) > 1
                            OR BOOL_OR(
                                reservation."BatchNumber" IS DISTINCT FROM allocation."BatchNumber" OR
                                reservation."ExpiryDate" IS DISTINCT FROM allocation."ExpiryDate" OR
                                reservation."Quantity" IS DISTINCT FROM allocation."Quantity" OR
                                reservation."ConsumedQuantity" IS DISTINCT FROM allocation."ConsumedQuantity" OR
                                reservation."ExpiryExceptionReason" IS DISTINCT FROM allocation."ExpiryExceptionReason")
                    ) THEN
                        RAISE EXCEPTION 'Cannot downgrade while reservation allocations contain multi-lot or non-mirrored data.';
                    END IF;
                END
                $migration$;
                """);

            migrationBuilder.DropTable(
                name: "StockReservationAllocations");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_StockReservations_Id_TenantId",
                table: "StockReservations");
        }
    }
}
