using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202610300002_AgentPairingAndDiscovery")]
public sealed class AgentPairingAndDiscovery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "AgentDevices" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "DeviceName" varchar(160) NOT NULL,
                "FingerprintHash" varchar(64) NOT NULL,
                "SecretHash" varchar(64) NOT NULL,
                "Status" varchar(24) NOT NULL,
                "CreatedUtc" timestamptz NOT NULL,
                "LastSeenUtc" timestamptz,
                "RevokedUtc" timestamptz,
                CONSTRAINT "FK_AgentDevices_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE CASCADE,
                CONSTRAINT "AK_AgentDevices_TenantId_Id" UNIQUE ("TenantId", "Id")
            );
            CREATE UNIQUE INDEX "IX_AgentDevices_TenantId_FingerprintHash" ON "AgentDevices" ("TenantId", "FingerprintHash");
            CREATE INDEX "IX_AgentDevices_TenantId_Status" ON "AgentDevices" ("TenantId", "Status");

            CREATE TABLE "AgentEnrollmentCodes" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "CodeHash" varchar(64) NOT NULL,
                "CreatedUtc" timestamptz NOT NULL,
                "ExpiresUtc" timestamptz NOT NULL,
                "UsedUtc" timestamptz,
                "CreatedByUserId" uuid,
                CONSTRAINT "FK_AgentEnrollmentCodes_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX "IX_AgentEnrollmentCodes_CodeHash" ON "AgentEnrollmentCodes" ("CodeHash");
            CREATE INDEX "IX_AgentEnrollmentCodes_TenantId_ExpiresUtc_UsedUtc" ON "AgentEnrollmentCodes" ("TenantId", "ExpiresUtc", "UsedUtc");

            ALTER TABLE "PrinterDevices" ADD COLUMN "AgentDeviceId" uuid NULL;
            ALTER TABLE "PrinterDevices" ADD COLUMN "IsDefault" boolean NOT NULL DEFAULT false;
            ALTER TABLE "PrinterDevices"
                ADD CONSTRAINT "FK_PrinterDevices_AgentDevices_TenantId_AgentDeviceId"
                FOREIGN KEY ("TenantId", "AgentDeviceId")
                REFERENCES "AgentDevices" ("TenantId", "Id")
                ON DELETE RESTRICT;
            CREATE UNIQUE INDEX "IX_PrinterDevices_TenantId_AgentDeviceId_QueueName"
                ON "PrinterDevices" ("TenantId", "AgentDeviceId", "QueueName")
                WHERE "AgentDeviceId" IS NOT NULL AND "QueueName" IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS "IX_PrinterDevices_TenantId_AgentDeviceId_QueueName";
            ALTER TABLE "PrinterDevices" DROP CONSTRAINT IF EXISTS "FK_PrinterDevices_AgentDevices_TenantId_AgentDeviceId";
            ALTER TABLE "PrinterDevices" DROP COLUMN IF EXISTS "AgentDeviceId";
            ALTER TABLE "PrinterDevices" DROP COLUMN IF EXISTS "IsDefault";
            DROP TABLE IF EXISTS "AgentEnrollmentCodes";
            DROP TABLE IF EXISTS "AgentDevices";
            """);
    }
}
