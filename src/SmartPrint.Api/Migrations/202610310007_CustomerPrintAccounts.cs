using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202610310007_CustomerPrintAccounts")]
public sealed class CustomerPrintAccounts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "CustomerPrintSessions"
                ADD COLUMN "CustomerUserId" uuid NULL;

            CREATE INDEX "IX_CustomerPrintSessions_CustomerUserId_CreatedUtc"
                ON "CustomerPrintSessions" ("CustomerUserId", "CreatedUtc");

            ALTER TABLE "CustomerPrintSessions"
                ADD CONSTRAINT "FK_CustomerPrintSessions_AspNetUsers_CustomerUserId"
                FOREIGN KEY ("CustomerUserId") REFERENCES "AspNetUsers" ("Id")
                ON DELETE SET NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "CustomerPrintSessions"
                DROP CONSTRAINT IF EXISTS "FK_CustomerPrintSessions_AspNetUsers_CustomerUserId";
            DROP INDEX IF EXISTS "IX_CustomerPrintSessions_CustomerUserId_CreatedUtc";
            ALTER TABLE "CustomerPrintSessions"
                DROP COLUMN IF EXISTS "CustomerUserId";
            """);
    }
}
