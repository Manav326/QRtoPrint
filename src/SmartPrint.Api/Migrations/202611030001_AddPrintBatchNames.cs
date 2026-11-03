using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611030001_AddPrintBatchNames")]
public sealed class AddPrintBatchNames : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs" ADD COLUMN "BatchName" character varying(50) NULL;
            ALTER TABLE "LocalPrintUploadTickets" ADD COLUMN "BatchName" character varying(50) NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "LocalPrintUploadTickets" DROP COLUMN IF EXISTS "BatchName";
            ALTER TABLE "PrintJobs" DROP COLUMN IF EXISTS "BatchName";
            """);
    }
}
