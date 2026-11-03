using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611010005_AddPrintBatchFolderGroups")]
public sealed class AddPrintBatchFolderGroups : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs"
                ADD COLUMN "BatchGroupName" varchar(80) NULL;

            ALTER TABLE "LocalPrintUploadTickets"
                ADD COLUMN "BatchGroupName" varchar(80) NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "LocalPrintUploadTickets" DROP COLUMN IF EXISTS "BatchGroupName";
            ALTER TABLE "PrintJobs" DROP COLUMN IF EXISTS "BatchGroupName";
            """);
    }
}
