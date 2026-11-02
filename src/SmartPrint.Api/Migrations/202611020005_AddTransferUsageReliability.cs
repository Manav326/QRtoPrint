using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611020005_AddTransferUsageReliability")]
public sealed class AddTransferUsageReliability : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PlatformOperationalSettings"
                ADD COLUMN "DataTransferRawRetentionDays" integer NOT NULL DEFAULT 90,
                ADD COLUMN "DataTransferMeasurementStartedUtc" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP;

            ALTER TABLE "PlatformOperationalSettings"
                ADD CONSTRAINT "CK_PlatformOperationalSettings_DataTransferRawRetentionDays"
                CHECK ("DataTransferRawRetentionDays" BETWEEN 30 AND 3650);

            ALTER TABLE "PrintJobs"
                ADD COLUMN "AgentDocumentDownloadAttempts" integer NOT NULL DEFAULT 0;
            ALTER TABLE "PrintJobs"
                ADD CONSTRAINT "CK_PrintJobs_AgentDocumentDownloadAttempts"
                CHECK ("AgentDocumentDownloadAttempts" >= 0);

            ALTER TABLE "LocalPrintUploadTickets"
                ADD COLUMN "AgentUploadAttempts" integer NOT NULL DEFAULT 0;
            ALTER TABLE "LocalPrintUploadTickets"
                ADD CONSTRAINT "CK_LocalPrintUploadTickets_AgentUploadAttempts"
                CHECK ("AgentUploadAttempts" >= 0);

            ALTER TABLE "AgentDevices"
                ADD COLUMN "LocalQueueDocumentCount" bigint NULL,
                ADD COLUMN "LocalQueueLogicalBytes" bigint NULL,
                ADD COLUMN "LocalQueueDiskBytes" bigint NULL,
                ADD COLUMN "LocalOutputFileCount" bigint NULL,
                ADD COLUMN "LocalOutputFileBytes" bigint NULL,
                ADD COLUMN "LocalStorageMeasuredUtc" timestamp with time zone NULL;

            ALTER TABLE "AgentDevices"
                ADD CONSTRAINT "CK_AgentDevices_LocalStorageMetrics"
                CHECK (
                    ("LocalQueueDocumentCount" IS NULL OR "LocalQueueDocumentCount" >= 0) AND
                    ("LocalQueueLogicalBytes" IS NULL OR "LocalQueueLogicalBytes" >= 0) AND
                    ("LocalQueueDiskBytes" IS NULL OR "LocalQueueDiskBytes" >= 0) AND
                    ("LocalOutputFileCount" IS NULL OR "LocalOutputFileCount" >= 0) AND
                    ("LocalOutputFileBytes" IS NULL OR "LocalOutputFileBytes" >= 0)
                );

            CREATE TABLE "DataTransferDailyAggregates" (
                "UtcDate" date NOT NULL,
                "TenantId" uuid NOT NULL,
                "Channel" character varying(64) NOT NULL,
                "Direction" character varying(24) NOT NULL,
                "Status" character varying(24) NOT NULL,
                "TransferCount" bigint NOT NULL,
                "PayloadBytes" bigint NOT NULL,
                "NetworkBytes" bigint NULL,
                "TotalDurationMilliseconds" bigint NOT NULL,
                "DurationSampleCount" bigint NOT NULL,
                CONSTRAINT "PK_DataTransferDailyAggregates"
                    PRIMARY KEY ("UtcDate", "TenantId", "Channel", "Direction", "Status"),
                CONSTRAINT "FK_DataTransferDailyAggregates_Tenants_TenantId"
                    FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE CASCADE,
                CONSTRAINT "CK_DataTransferDailyAggregates_NonNegativeMetrics"
                    CHECK ("TransferCount" >= 0 AND "PayloadBytes" >= 0
                        AND ("NetworkBytes" IS NULL OR "NetworkBytes" >= 0)
                        AND "TotalDurationMilliseconds" >= 0
                        AND "DurationSampleCount" >= 0)
            );

            CREATE INDEX "IX_DataTransferDailyAggregates_UtcDate"
                ON "DataTransferDailyAggregates" ("UtcDate");
            CREATE INDEX "IX_DataTransferDailyAggregates_TenantId_UtcDate"
                ON "DataTransferDailyAggregates" ("TenantId", "UtcDate");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS "DataTransferDailyAggregates";
            ALTER TABLE "AgentDevices"
                DROP CONSTRAINT IF EXISTS "CK_AgentDevices_LocalStorageMetrics",
                DROP COLUMN IF EXISTS "LocalQueueDocumentCount",
                DROP COLUMN IF EXISTS "LocalQueueLogicalBytes",
                DROP COLUMN IF EXISTS "LocalQueueDiskBytes",
                DROP COLUMN IF EXISTS "LocalOutputFileCount",
                DROP COLUMN IF EXISTS "LocalOutputFileBytes",
                DROP COLUMN IF EXISTS "LocalStorageMeasuredUtc";
            ALTER TABLE "LocalPrintUploadTickets"
                DROP CONSTRAINT IF EXISTS "CK_LocalPrintUploadTickets_AgentUploadAttempts",
                DROP COLUMN IF EXISTS "AgentUploadAttempts";
            ALTER TABLE "PrintJobs"
                DROP CONSTRAINT IF EXISTS "CK_PrintJobs_AgentDocumentDownloadAttempts",
                DROP COLUMN IF EXISTS "AgentDocumentDownloadAttempts";
            ALTER TABLE "PlatformOperationalSettings"
                DROP CONSTRAINT IF EXISTS "CK_PlatformOperationalSettings_DataTransferRawRetentionDays",
                DROP COLUMN IF EXISTS "DataTransferRawRetentionDays",
                DROP COLUMN IF EXISTS "DataTransferMeasurementStartedUtc";
            """);
    }
}
