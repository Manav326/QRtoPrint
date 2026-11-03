using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202610310005_AddLocalCustomerUploads")]
public sealed class AddLocalCustomerUploads : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintDocuments"
                ADD COLUMN "StorageMode" varchar(20) NOT NULL DEFAULT 'CLOUD',
                ADD COLUMN "AgentDeviceId" uuid NULL;
            ALTER TABLE "PrintDocuments"
                ADD CONSTRAINT "FK_PrintDocuments_AgentDevices_TenantId_AgentDeviceId"
                FOREIGN KEY ("TenantId", "AgentDeviceId")
                REFERENCES "AgentDevices" ("TenantId", "Id")
                ON DELETE RESTRICT;
            CREATE INDEX "IX_PrintDocuments_TenantId_AgentDeviceId"
                ON "PrintDocuments" ("TenantId", "AgentDeviceId")
                WHERE "AgentDeviceId" IS NOT NULL;

            CREATE TABLE "LocalPrintUploadTickets" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "CustomerSessionId" uuid NOT NULL,
                "QrStationId" uuid NOT NULL,
                "PrinterDeviceId" uuid NOT NULL,
                "AgentDeviceId" uuid NOT NULL,
                "PrintJobId" uuid NOT NULL,
                "JobNumber" varchar(32) NOT NULL,
                "IdempotencyKey" varchar(100) NOT NULL,
                "GrantHash" varchar(64) NOT NULL,
                "OriginalFileName" varchar(255) NOT NULL,
                "MediaType" varchar(100) NOT NULL,
                "FileExtension" varchar(8) NOT NULL,
                "SizeBytes" bigint NOT NULL,
                "Sha256" varchar(64) NOT NULL,
                "CreatedUtc" timestamptz NOT NULL,
                "ExpiresUtc" timestamptz NOT NULL,
                "ClaimedUtc" timestamptz,
                "ClaimLeaseExpiresUtc" timestamptz,
                "CompletedUtc" timestamptz,
                CONSTRAINT "FK_LocalPrintUploadTickets_Tenants_TenantId"
                    FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_LocalPrintUploadTickets_CustomerPrintSessions_TenantId_CustomerSessionId"
                    FOREIGN KEY ("TenantId", "CustomerSessionId")
                    REFERENCES "CustomerPrintSessions" ("TenantId", "Id") ON DELETE CASCADE,
                CONSTRAINT "FK_LocalPrintUploadTickets_QrStations_TenantId_QrStationId"
                    FOREIGN KEY ("TenantId", "QrStationId")
                    REFERENCES "QrStations" ("TenantId", "Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_LocalPrintUploadTickets_PrinterDevices_TenantId_PrinterDeviceId"
                    FOREIGN KEY ("TenantId", "PrinterDeviceId")
                    REFERENCES "PrinterDevices" ("TenantId", "Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_LocalPrintUploadTickets_AgentDevices_TenantId_AgentDeviceId"
                    FOREIGN KEY ("TenantId", "AgentDeviceId")
                    REFERENCES "AgentDevices" ("TenantId", "Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX "IX_LocalPrintUploadTickets_GrantHash"
                ON "LocalPrintUploadTickets" ("GrantHash");
            CREATE UNIQUE INDEX "IX_LocalPrintUploadTickets_TenantId_CustomerSessionId_IdempotencyKey"
                ON "LocalPrintUploadTickets" ("TenantId", "CustomerSessionId", "IdempotencyKey");
            CREATE UNIQUE INDEX "IX_LocalPrintUploadTickets_TenantId_PrintJobId"
                ON "LocalPrintUploadTickets" ("TenantId", "PrintJobId");
            CREATE INDEX "IX_LocalPrintUploadTickets_TenantId_AgentDeviceId_ExpiresUtc_CompletedUtc"
                ON "LocalPrintUploadTickets" ("TenantId", "AgentDeviceId", "ExpiresUtc", "CompletedUtc");
            CREATE INDEX "IX_LocalPrintUploadTickets_TenantId_QrStationId"
                ON "LocalPrintUploadTickets" ("TenantId", "QrStationId");
            CREATE INDEX "IX_LocalPrintUploadTickets_TenantId_PrinterDeviceId"
                ON "LocalPrintUploadTickets" ("TenantId", "PrinterDeviceId");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS "LocalPrintUploadTickets";
            DROP INDEX IF EXISTS "IX_PrintDocuments_TenantId_AgentDeviceId";
            ALTER TABLE "PrintDocuments"
                DROP CONSTRAINT IF EXISTS "FK_PrintDocuments_AgentDevices_TenantId_AgentDeviceId",
                DROP COLUMN IF EXISTS "StorageMode",
                DROP COLUMN IF EXISTS "AgentDeviceId";
            """);
    }
}
