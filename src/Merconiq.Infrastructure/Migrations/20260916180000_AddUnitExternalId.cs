using Merconiq.Infrastructure.Services;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations;

public partial class AddUnitExternalId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ExternalId",
            table: "UnitsOfMeasure",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true,
            defaultValue: "");

        migrationBuilder.Sql($"""
            LOCK TABLE "UnitsOfMeasure" IN ACCESS EXCLUSIVE MODE;

            CREATE TABLE "{LegacyUnitExternalId.ProvenanceTable}" (
                "UnitId" integer NOT NULL,
                "MigrationId" character varying(128) NOT NULL,
                "TenantId" character varying(64) NOT NULL,
                "PreviousExternalId" character varying(128) NOT NULL,
                "Placeholder" character varying(128) NOT NULL,
                CONSTRAINT "PK_{LegacyUnitExternalId.ProvenanceTable}" PRIMARY KEY ("UnitId", "MigrationId")
            );

            INSERT INTO "{LegacyUnitExternalId.ProvenanceTable}"
                ("UnitId", "MigrationId", "TenantId", "PreviousExternalId", "Placeholder")
            SELECT "Id", '{LegacyUnitExternalId.InitialMigrationId}', "TenantId", COALESCE("ExternalId", ''),
                   '{LegacyUnitExternalId.ReservedPrefix}' || "Id"::text
            FROM "UnitsOfMeasure"
            WHERE "ExternalId" IS NULL OR "ExternalId" = '';

            UPDATE "UnitsOfMeasure"
            SET "ExternalId" = '{LegacyUnitExternalId.ReservedPrefix}' || "Id"::text
            WHERE "ExternalId" IS NULL OR "ExternalId" = '';
            """);

        migrationBuilder.AlterColumn<string>(
            name: "ExternalId",
            table: "UnitsOfMeasure",
            type: "character varying(128)",
            maxLength: 128,
            nullable: false,
            defaultValue: "",
            oldClrType: typeof(string),
            oldType: "character varying(128)",
            oldMaxLength: 128,
            oldNullable: true,
            oldDefaultValue: "");

        migrationBuilder.CreateIndex(
            name: "IX_UnitsOfMeasure_TenantId_ExternalId",
            table: "UnitsOfMeasure",
            columns: new[] { "TenantId", "ExternalId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""
            LOCK TABLE "UnitsOfMeasure" IN ACCESS EXCLUSIVE MODE;

            DO $migration$
            BEGIN
                IF to_regclass('"{LegacyUnitExternalId.ProvenanceTable}"') IS NULL THEN
                    RAISE EXCEPTION 'Cannot safely downgrade unit external IDs because backfill provenance is missing.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM "UnitsOfMeasure" AS unit
                    WHERE unit."ExternalId" <> ''
                      AND NOT EXISTS (
                          SELECT 1
                          FROM "{LegacyUnitExternalId.ProvenanceTable}" AS provenance
                          WHERE provenance."UnitId" = unit."Id"
                            AND provenance."TenantId" = unit."TenantId"
                            AND provenance."MigrationId" = '{LegacyUnitExternalId.InitialMigrationId}'
                            AND provenance."Placeholder" = unit."ExternalId"
                      )
                ) THEN
                    RAISE EXCEPTION 'Cannot safely downgrade unit external IDs because imported or changed IDs would be discarded.';
                END IF;
            END
            $migration$;
            """);

        migrationBuilder.DropIndex(
            name: "IX_UnitsOfMeasure_TenantId_ExternalId",
            table: "UnitsOfMeasure");
        migrationBuilder.DropColumn(
            name: "ExternalId",
            table: "UnitsOfMeasure");
        migrationBuilder.DropTable(
            name: LegacyUnitExternalId.ProvenanceTable);
    }
}
