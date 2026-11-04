using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611040001_AddOfflineQueuePrintSettings")]
public sealed class AddOfflineQueuePrintSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs"
                ADD COLUMN "Orientation" varchar(16) NOT NULL DEFAULT 'AUTO';

            ALTER TABLE "LocalPrintUploadTickets"
                ADD COLUMN "ColourMode" varchar(12) NOT NULL DEFAULT 'Mono',
                ADD COLUMN "PaperSize" varchar(12) NOT NULL DEFAULT 'A4',
                ADD COLUMN "IsDuplex" boolean NOT NULL DEFAULT false,
                ADD COLUMN "Copies" integer NOT NULL DEFAULT 1,
                ADD COLUMN "Orientation" varchar(16) NOT NULL DEFAULT 'AUTO';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "LocalPrintUploadTickets"
                DROP COLUMN IF EXISTS "Orientation",
                DROP COLUMN IF EXISTS "Copies",
                DROP COLUMN IF EXISTS "IsDuplex",
                DROP COLUMN IF EXISTS "PaperSize",
                DROP COLUMN IF EXISTS "ColourMode";

            ALTER TABLE "PrintJobs"
                DROP COLUMN IF EXISTS "Orientation";
            """);
    }
}
