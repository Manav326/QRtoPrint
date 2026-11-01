using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611020002_AddShopInstantPaymentPreference")]
public sealed class AddShopInstantPaymentPreference : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "ShopPaymentProfiles"
                ADD COLUMN "InstantPaymentEnabled" boolean NOT NULL DEFAULT FALSE;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "ShopPaymentProfiles"
                DROP COLUMN IF EXISTS "InstantPaymentEnabled";
            """);
    }
}
