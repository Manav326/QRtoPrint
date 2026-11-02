using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611020004_AddDataTransferEvents")]
public sealed class AddDataTransferEvents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "DataTransferEvents" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "TransferId" uuid NOT NULL,
                "AttemptNumber" integer NOT NULL,
                "TenantId" uuid NOT NULL,
                "AgentDeviceId" uuid NULL,
                "PrinterDeviceId" uuid NULL,
                "QrStationId" uuid NULL,
                "PrintJobId" uuid NULL,
                "PrintDocumentId" uuid NULL,
                "Channel" character varying(64) NOT NULL,
                "Direction" character varying(24) NOT NULL,
                "Status" character varying(24) NOT NULL,
                "PayloadBytes" bigint NOT NULL,
                "NetworkBytes" bigint NULL,
                "DurationMilliseconds" bigint NOT NULL,
                "MeasurementSource" character varying(80) NOT NULL,
                "CreatedUtc" timestamp with time zone NOT NULL,
                CONSTRAINT "FK_DataTransferEvents_Tenants_TenantId"
                    FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE CASCADE,
                CONSTRAINT "CK_DataTransferEvents_NonNegativeMetrics"
                    CHECK ("AttemptNumber" >= 1 AND "PayloadBytes" >= 0
                        AND ("NetworkBytes" IS NULL OR "NetworkBytes" >= 0)
                        AND "DurationMilliseconds" >= 0)
            );

            CREATE UNIQUE INDEX "IX_DataTransferEvents_TransferId_AttemptNumber"
                ON "DataTransferEvents" ("TransferId", "AttemptNumber");
            CREATE INDEX "IX_DataTransferEvents_CreatedUtc"
                ON "DataTransferEvents" ("CreatedUtc");
            CREATE INDEX "IX_DataTransferEvents_TenantId_CreatedUtc"
                ON "DataTransferEvents" ("TenantId", "CreatedUtc");
            CREATE INDEX "IX_DataTransferEvents_Channel_CreatedUtc"
                ON "DataTransferEvents" ("Channel", "CreatedUtc");
            CREATE INDEX "IX_DataTransferEvents_TenantId_Channel_CreatedUtc"
                ON "DataTransferEvents" ("TenantId", "Channel", "CreatedUtc");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""DROP TABLE IF EXISTS "DataTransferEvents";""");
    }
}
