using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202610310009_QuoteLockedPageRefundAllocations")]
public sealed class QuoteLockedPageRefundAllocations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs"
                ADD COLUMN "PageRefundAllocationsJson" jsonb NOT NULL DEFAULT '[]'::jsonb;
            ALTER TABLE "PrintJobs"
                ADD CONSTRAINT "CK_PrintJobs_PageRefundAllocationsJson"
                CHECK (jsonb_typeof("PageRefundAllocationsJson") = 'array');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs" DROP CONSTRAINT IF EXISTS "CK_PrintJobs_PageRefundAllocationsJson";
            ALTER TABLE "PrintJobs" DROP COLUMN IF EXISTS "PageRefundAllocationsJson";
            """);
    }
}
