using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611020003_AddPrintJobPrinterIdentitySnapshot")]
public sealed class AddPrintJobPrinterIdentitySnapshot : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs"
                ADD COLUMN "PrinterNameSnapshot" character varying(160) NULL,
                ADD COLUMN "PrinterQueueNameSnapshot" character varying(160) NULL,
                ADD COLUMN "PrinterAgentNameSnapshot" character varying(160) NULL,
                ADD COLUMN "PrinterSnapshotUtc" timestamp with time zone NULL;

            UPDATE "PrintJobs" AS job
            SET "PrinterNameSnapshot" = printer."DisplayName",
                "PrinterQueueNameSnapshot" = printer."QueueName",
                "PrinterAgentNameSnapshot" = agent."DeviceName",
                "PrinterSnapshotUtc" = COALESCE(
                    job."SpoolerSubmittedUtc",
                    job."OutputConfirmedUtc",
                    job."UpdatedUtc")
            FROM "PrinterDevices" AS printer
            LEFT JOIN "AgentDevices" AS agent
                ON agent."TenantId" = printer."TenantId"
               AND agent."Id" = printer."AgentDeviceId"
            WHERE job."TenantId" = printer."TenantId"
              AND job."PrinterDeviceId" = printer."Id"
              AND (
                  job."SpoolerSubmittedUtc" IS NOT NULL
                  OR job."SpoolerJobId" IS NOT NULL
                  OR job."OutputConfirmedUtc" IS NOT NULL
                  OR job."Status" IN (
                      'PRINT_AUTHORISED',
                      'PRINT_SUBMITTING',
                      'SPOOLER_ACCEPTED',
                      'PRINTING',
                      'OUTPUT_UNCONFIRMED',
                      'COMPLETED'));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs"
                DROP COLUMN IF EXISTS "PrinterSnapshotUtc",
                DROP COLUMN IF EXISTS "PrinterAgentNameSnapshot",
                DROP COLUMN IF EXISTS "PrinterQueueNameSnapshot",
                DROP COLUMN IF EXISTS "PrinterNameSnapshot";
            """);
    }
}
