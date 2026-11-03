using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202610300005_PrintSpoolerExecution")]
public sealed class PrintSpoolerExecution : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs"
                ADD COLUMN "PrintDispatchLeaseExpiresUtc" timestamptz NULL,
                ADD COLUMN "PrintDispatchAttempts" integer NOT NULL DEFAULT 0,
                ADD COLUMN "SpoolerJobId" integer NULL,
                ADD COLUMN "SpoolerJobName" character varying(180) NULL,
                ADD COLUMN "SpoolerStatus" character varying(80) NULL,
                ADD COLUMN "SpoolerSubmittedUtc" timestamptz NULL,
                ADD COLUMN "SpoolerObservedUtc" timestamptz NULL,
                ADD COLUMN "OutputConfirmedUtc" timestamptz NULL;

            CREATE INDEX "IX_PrintJobs_TenantId_Status_PrintDispatchLeaseExpiresUtc"
                ON "PrintJobs" ("TenantId", "Status", "PrintDispatchLeaseExpiresUtc");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS "IX_PrintJobs_TenantId_Status_PrintDispatchLeaseExpiresUtc";
            ALTER TABLE "PrintJobs"
                DROP COLUMN IF EXISTS "PrintDispatchLeaseExpiresUtc",
                DROP COLUMN IF EXISTS "PrintDispatchAttempts",
                DROP COLUMN IF EXISTS "SpoolerJobId",
                DROP COLUMN IF EXISTS "SpoolerJobName",
                DROP COLUMN IF EXISTS "SpoolerStatus",
                DROP COLUMN IF EXISTS "SpoolerSubmittedUtc",
                DROP COLUMN IF EXISTS "SpoolerObservedUtc",
                DROP COLUMN IF EXISTS "OutputConfirmedUtc";
            """);
    }
}
