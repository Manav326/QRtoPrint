using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611020006_AddCloudUploadAttemptCounter")]
public sealed class AddCloudUploadAttemptCounter : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "CustomerPrintSessions"
                ADD COLUMN "CloudUploadAttemptCount" integer NOT NULL DEFAULT 0;

            ALTER TABLE "CustomerPrintSessions"
                ADD CONSTRAINT "CK_CustomerPrintSessions_CloudUploadAttemptCount"
                CHECK ("CloudUploadAttemptCount" >= 0);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "CustomerPrintSessions"
                DROP CONSTRAINT IF EXISTS "CK_CustomerPrintSessions_CloudUploadAttemptCount",
                DROP COLUMN IF EXISTS "CloudUploadAttemptCount";
            """);
    }
}
