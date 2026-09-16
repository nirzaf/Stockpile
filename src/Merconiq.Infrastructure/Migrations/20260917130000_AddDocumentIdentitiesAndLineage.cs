using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentIdentitiesAndLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DocumentId",
                table: "PurchaseOrders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DocumentLineId",
                table: "OrderDetails",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DocumentIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: true),
                    DocumentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    HumanNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Period = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequestScope = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RequestKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentIdentities", x => x.Id);
                    table.UniqueConstraint("AK_DocumentIdentities_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.ForeignKey(
                        name: "FK_DocumentIdentities_Companies_CompanyId_TenantId",
                        columns: x => new { x.CompanyId, x.TenantId },
                        principalTable: "Companies",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            // Preserve every existing human PO number and leave its legal-company owner unknown.
            migrationBuilder.Sql(
                """
                INSERT INTO "DocumentIdentities"
                    ("Id", "CompanyId", "DocumentType", "HumanNumber", "Period", "Status", "RequestScope", "RequestKey", "RequestHash", "TenantId", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy")
                SELECT gen_random_uuid(), NULL, 'PurchaseOrder', po."PONumber",
                    EXTRACT(YEAR FROM po."OrderDate" AT TIME ZONE 'UTC')::integer,
                    CASE po."Status"
                        WHEN 'Draft' THEN 'Draft'
                        WHEN 'Cancelled' THEN 'Cancelled'
                        WHEN 'Voided' THEN 'Voided'
                        ELSE 'Active'
                    END,
                    'PurchaseOrder.Legacy', NULL, NULL, po."TenantId",
                    po."CreatedAt", po."CreatedBy", po."UpdatedAt", po."UpdatedBy"
                FROM "PurchaseOrders" AS po;
                """);

            migrationBuilder.Sql(
                """
                UPDATE "PurchaseOrders" AS po
                SET "DocumentId" = document."Id"
                FROM "DocumentIdentities" AS document
                WHERE document."TenantId" = po."TenantId"
                  AND document."DocumentType" = 'PurchaseOrder'
                  AND document."HumanNumber" = po."PONumber";
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "DocumentId",
                table: "PurchaseOrders",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "DocumentLineIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: true),
                    LineType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentLineIdentities", x => x.Id);
                    table.UniqueConstraint("AK_DocumentLineIdentities_DocumentId_Id_TenantId", x => new { x.DocumentId, x.Id, x.TenantId });
                    table.UniqueConstraint("AK_DocumentLineIdentities_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.ForeignKey(
                        name: "FK_DocumentLineIdentities_DocumentIdentities_DocumentId_TenantId",
                        columns: x => new { x.DocumentId, x.TenantId },
                        principalTable: "DocumentIdentities",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(
                """
                UPDATE "OrderDetails"
                SET "DocumentLineId" = gen_random_uuid();

                INSERT INTO "DocumentLineIdentities"
                    ("Id", "DocumentId", "CompanyId", "LineType", "TenantId", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy")
                SELECT detail."DocumentLineId", po."DocumentId", NULL, 'PurchaseOrderLine',
                    detail."TenantId", detail."CreatedAt", detail."CreatedBy", detail."UpdatedAt", detail."UpdatedBy"
                FROM "OrderDetails" AS detail
                INNER JOIN "PurchaseOrders" AS po ON po."Id" = detail."PurchaseOrderId";
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "DocumentLineId",
                table: "OrderDetails",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "DocumentLineLinks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    SourceDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelationshipType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentLineLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentLineLinks_Companies_CompanyId_TenantId",
                        columns: x => new { x.CompanyId, x.TenantId },
                        principalTable: "Companies",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentLineLinks_DocumentLineIdentities_SourceLine",
                        columns: x => new { x.SourceDocumentId, x.SourceLineId, x.TenantId },
                        principalTable: "DocumentLineIdentities",
                        principalColumns: new[] { "DocumentId", "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentLineLinks_DocumentLineIdentities_TargetLine",
                        columns: x => new { x.TargetDocumentId, x.TargetLineId, x.TenantId },
                        principalTable: "DocumentLineIdentities",
                        principalColumns: new[] { "DocumentId", "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_DocumentId_TenantId",
                table: "PurchaseOrders",
                columns: new[] { "DocumentId", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderDetails_DocumentLineId_TenantId",
                table: "OrderDetails",
                columns: new[] { "DocumentLineId", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentIdentities_CompanyId_TenantId",
                table: "DocumentIdentities",
                columns: new[] { "CompanyId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentIdentities_TenantId_CompanyId_DocumentType_Period_H~",
                table: "DocumentIdentities",
                columns: new[] { "TenantId", "CompanyId", "DocumentType", "Period", "HumanNumber" },
                unique: true,
                filter: "\"CompanyId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentIdentities_TenantId_DocumentType_Period_HumanNumber",
                table: "DocumentIdentities",
                columns: new[] { "TenantId", "DocumentType", "Period", "HumanNumber" },
                unique: true,
                filter: "\"CompanyId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentIdentities_TenantId_RequestScope_RequestKey",
                table: "DocumentIdentities",
                columns: new[] { "TenantId", "RequestScope", "RequestKey" },
                unique: true,
                filter: "\"RequestKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentLineIdentities_DocumentId_TenantId",
                table: "DocumentLineIdentities",
                columns: new[] { "DocumentId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentLineLinks_CompanyId_TenantId",
                table: "DocumentLineLinks",
                columns: new[] { "CompanyId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentLineLinks_SourceDocumentId_SourceLineId_TenantId",
                table: "DocumentLineLinks",
                columns: new[] { "SourceDocumentId", "SourceLineId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentLineLinks_TargetDocumentId_TargetLineId_TenantId",
                table: "DocumentLineLinks",
                columns: new[] { "TargetDocumentId", "TargetLineId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentLineLinks_TenantId_CompanyId_SourceDocumentId_Sourc~",
                table: "DocumentLineLinks",
                columns: new[] { "TenantId", "CompanyId", "SourceDocumentId", "SourceLineId", "TargetDocumentId", "TargetLineId", "RelationshipType" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_OrderDetails_DocumentLineIdentities_DocumentLineId_TenantId",
                table: "OrderDetails",
                columns: new[] { "DocumentLineId", "TenantId" },
                principalTable: "DocumentLineIdentities",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseOrders_DocumentIdentities_DocumentId_TenantId",
                table: "PurchaseOrders",
                columns: new[] { "DocumentId", "TenantId" },
                principalTable: "DocumentIdentities",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OrderDetails_DocumentLineIdentities_DocumentLineId_TenantId",
                table: "OrderDetails");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseOrders_DocumentIdentities_DocumentId_TenantId",
                table: "PurchaseOrders");

            migrationBuilder.DropTable(name: "DocumentLineLinks");
            migrationBuilder.DropTable(name: "DocumentLineIdentities");
            migrationBuilder.DropTable(name: "DocumentIdentities");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrders_DocumentId_TenantId",
                table: "PurchaseOrders");

            migrationBuilder.DropIndex(
                name: "IX_OrderDetails_DocumentLineId_TenantId",
                table: "OrderDetails");

            migrationBuilder.DropColumn(name: "DocumentId", table: "PurchaseOrders");
            migrationBuilder.DropColumn(name: "DocumentLineId", table: "OrderDetails");
        }
    }
}
