using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202610300004_AgentLocalQueueHandoff")]
public sealed class AgentLocalQueueHandoff : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs"
                ADD COLUMN "ClaimedByAgentDeviceId" uuid NULL,
                ADD COLUMN "AgentLeaseExpiresUtc" timestamptz NULL,
                ADD COLUMN "AgentClaimedUtc" timestamptz NULL,
                ADD COLUMN "LocalQueueReceivedUtc" timestamptz NULL,
                ADD COLUMN "AgentDispatchAttempts" integer NOT NULL DEFAULT 0;

            ALTER TABLE "PrintJobs"
                ADD CONSTRAINT "FK_PrintJobs_AgentDevices_TenantId_ClaimedByAgentDeviceId"
                FOREIGN KEY ("TenantId", "ClaimedByAgentDeviceId")
                REFERENCES "AgentDevices" ("TenantId", "Id")
                ON DELETE RESTRICT;

            CREATE INDEX "IX_PrintJobs_TenantId_Status_PrinterDeviceId_AgentLeaseExpiresUtc"
                ON "PrintJobs" ("TenantId", "Status", "PrinterDeviceId", "AgentLeaseExpiresUtc");
            CREATE INDEX "IX_PrintJobs_TenantId_ClaimedByAgentDeviceId_Status"
                ON "PrintJobs" ("TenantId", "ClaimedByAgentDeviceId", "Status");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS "IX_PrintJobs_TenantId_ClaimedByAgentDeviceId_Status";
            DROP INDEX IF EXISTS "IX_PrintJobs_TenantId_Status_PrinterDeviceId_AgentLeaseExpiresUtc";
            ALTER TABLE "PrintJobs"
                DROP CONSTRAINT IF EXISTS "FK_PrintJobs_AgentDevices_TenantId_ClaimedByAgentDeviceId";
            ALTER TABLE "PrintJobs"
                DROP COLUMN IF EXISTS "ClaimedByAgentDeviceId",
                DROP COLUMN IF EXISTS "AgentLeaseExpiresUtc",
                DROP COLUMN IF EXISTS "AgentClaimedUtc",
                DROP COLUMN IF EXISTS "LocalQueueReceivedUtc",
                DROP COLUMN IF EXISTS "AgentDispatchAttempts";
            """);
    }
}
