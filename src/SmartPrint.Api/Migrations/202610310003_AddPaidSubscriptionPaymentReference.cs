using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202610310003_AddPaidSubscriptionPaymentReference")]
public sealed class AddPaidSubscriptionPaymentReference : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PlatformSubscriptionAudits"
                ADD COLUMN "PaymentReference" varchar(100);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PlatformSubscriptionAudits"
                DROP COLUMN IF EXISTS "PaymentReference";
            """);
    }
}
