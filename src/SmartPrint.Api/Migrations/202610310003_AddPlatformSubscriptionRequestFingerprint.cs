using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202610310003_AddPlatformSubscriptionRequestFingerprint")]
public sealed class AddPlatformSubscriptionRequestFingerprint : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PlatformSubscriptionAudits"
                ADD COLUMN "RequestFingerprint" varchar(64);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PlatformSubscriptionAudits"
                DROP COLUMN IF EXISTS "RequestFingerprint";
            """);
    }
}
