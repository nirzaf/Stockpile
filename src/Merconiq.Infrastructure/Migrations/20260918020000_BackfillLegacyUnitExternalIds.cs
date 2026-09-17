using Merconiq.Infrastructure.Services;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations;

public partial class BackfillLegacyUnitExternalIds : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""
            LOCK TABLE "UnitsOfMeasure" IN ACCESS EXCLUSIVE MODE;

            CREATE TABLE IF NOT EXISTS "{LegacyUnitExternalId.ProvenanceTable}" (
                "UnitId" integer NOT NULL,
                "MigrationId" character varying(128) NOT NULL,
                "TenantId" character varying(64) NOT NULL,
                "PreviousExternalId" character varying(128) NOT NULL,
                "Placeholder" character varying(128) NOT NULL,
                CONSTRAINT "PK_{LegacyUnitExternalId.ProvenanceTable}" PRIMARY KEY ("UnitId", "MigrationId")
            );

            DO $migration$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM "UnitsOfMeasure" AS legacy
                    INNER JOIN "UnitsOfMeasure" AS existing
                        ON existing."TenantId" = legacy."TenantId"
                       AND lower(existing."ExternalId") = lower('{LegacyUnitExternalId.ReservedPrefix}' || legacy."Id"::text)
                       AND existing."Id" <> legacy."Id"
                    WHERE legacy."ExternalId" IS NULL OR legacy."ExternalId" = ''
                ) THEN
                    RAISE EXCEPTION 'Cannot assign reserved legacy unit IDs because an existing external ID would collide.';
                END IF;
            END
            $migration$;

            INSERT INTO "{LegacyUnitExternalId.ProvenanceTable}"
                ("UnitId", "MigrationId", "TenantId", "PreviousExternalId", "Placeholder")
            SELECT "Id", '{LegacyUnitExternalId.ForwardMigrationId}', "TenantId", COALESCE("ExternalId", ''),
                   '{LegacyUnitExternalId.ReservedPrefix}' || "Id"::text
            FROM "UnitsOfMeasure"
            WHERE "ExternalId" IS NULL OR "ExternalId" = '';

            UPDATE "UnitsOfMeasure"
            SET "ExternalId" = '{LegacyUnitExternalId.ReservedPrefix}' || "Id"::text
            WHERE "ExternalId" IS NULL OR "ExternalId" = '';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""
            LOCK TABLE "UnitsOfMeasure" IN ACCESS EXCLUSIVE MODE;

            DO $migration$
            BEGIN
                IF to_regclass('"{LegacyUnitExternalId.ProvenanceTable}"') IS NULL THEN
                    RAISE EXCEPTION 'Cannot safely downgrade legacy unit IDs because backfill provenance is missing.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM "{LegacyUnitExternalId.ProvenanceTable}" AS provenance
                    LEFT JOIN "UnitsOfMeasure" AS unit
                        ON unit."Id" = provenance."UnitId"
                       AND unit."TenantId" = provenance."TenantId"
                    WHERE provenance."MigrationId" = '{LegacyUnitExternalId.ForwardMigrationId}'
                      AND (unit."Id" IS NULL OR unit."ExternalId" <> provenance."Placeholder")
                ) THEN
                    RAISE EXCEPTION 'Cannot safely downgrade legacy unit IDs because a backfilled row was changed or removed.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM "{LegacyUnitExternalId.ProvenanceTable}" AS provenance
                    WHERE provenance."MigrationId" = '{LegacyUnitExternalId.ForwardMigrationId}'
                    GROUP BY provenance."TenantId", lower(provenance."PreviousExternalId")
                    HAVING count(*) > 1
                ) OR EXISTS (
                    SELECT 1
                    FROM "{LegacyUnitExternalId.ProvenanceTable}" AS provenance
                    INNER JOIN "UnitsOfMeasure" AS existing
                        ON existing."TenantId" = provenance."TenantId"
                       AND lower(existing."ExternalId") = lower(provenance."PreviousExternalId")
                       AND existing."Id" <> provenance."UnitId"
                    WHERE provenance."MigrationId" = '{LegacyUnitExternalId.ForwardMigrationId}'
                ) THEN
                    RAISE EXCEPTION 'Cannot safely downgrade legacy unit IDs because restoring previous values would violate tenant identity uniqueness.';
                END IF;
            END
            $migration$;

            UPDATE "UnitsOfMeasure" AS unit
            SET "ExternalId" = provenance."PreviousExternalId"
            FROM "{LegacyUnitExternalId.ProvenanceTable}" AS provenance
            WHERE provenance."MigrationId" = '{LegacyUnitExternalId.ForwardMigrationId}'
              AND unit."Id" = provenance."UnitId"
              AND unit."TenantId" = provenance."TenantId";

            DELETE FROM "{LegacyUnitExternalId.ProvenanceTable}"
            WHERE "MigrationId" = '{LegacyUnitExternalId.ForwardMigrationId}';
            """);
    }
}
