using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202610310006_AddPdfExportDestinations")]
public sealed class AddPdfExportDestinations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrinterDevices"
                ADD COLUMN "OutputMode" varchar(20) NOT NULL DEFAULT 'PHYSICAL';
            ALTER TABLE "PrinterDevices"
                ADD CONSTRAINT "CK_PrinterDevices_OutputMode"
                CHECK ("OutputMode" IN ('PHYSICAL', 'PDF_EXPORT'));

            ALTER TABLE "PrintJobs"
                ADD COLUMN "OutputType" varchar(20) NOT NULL DEFAULT 'PHYSICAL',
                ADD COLUMN "OutputFilePath" varchar(1024),
                ADD COLUMN "OutputFileName" varchar(255),
                ADD COLUMN "OutputSha256" varchar(64),
                ADD COLUMN "OutputSizeBytes" bigint,
                ADD COLUMN "OutputCompletedUtc" timestamptz;
            ALTER TABLE "PrintJobs"
                ADD CONSTRAINT "CK_PrintJobs_OutputType"
                CHECK ("OutputType" IN ('PHYSICAL', 'PDF_EXPORT')),
                ADD CONSTRAINT "CK_PrintJobs_OutputSizeBytes"
                CHECK ("OutputSizeBytes" IS NULL OR "OutputSizeBytes" > 0),
                ADD CONSTRAINT "CK_PrintJobs_OutputSha256"
                CHECK ("OutputSha256" IS NULL OR "OutputSha256" ~ '^[0-9a-fA-F]{64}$');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs"
                DROP CONSTRAINT IF EXISTS "CK_PrintJobs_OutputType",
                DROP CONSTRAINT IF EXISTS "CK_PrintJobs_OutputSizeBytes",
                DROP CONSTRAINT IF EXISTS "CK_PrintJobs_OutputSha256",
                DROP COLUMN IF EXISTS "OutputType",
                DROP COLUMN IF EXISTS "OutputFilePath",
                DROP COLUMN IF EXISTS "OutputFileName",
                DROP COLUMN IF EXISTS "OutputSha256",
                DROP COLUMN IF EXISTS "OutputSizeBytes",
                DROP COLUMN IF EXISTS "OutputCompletedUtc";
            ALTER TABLE "PrinterDevices"
                DROP CONSTRAINT IF EXISTS "CK_PrinterDevices_OutputMode",
                DROP COLUMN IF EXISTS "OutputMode";
            """);
    }
}
