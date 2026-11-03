using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202610310008_PartialPageRefunds")]
public sealed class PartialPageRefunds : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs" ADD COLUMN "RejectedPageNumbersJson" jsonb NOT NULL DEFAULT '[]'::jsonb;
            ALTER TABLE "PrintPayments" ADD COLUMN "RefundedAmountMinor" bigint NOT NULL DEFAULT 0;
            ALTER TABLE "PrintRefunds" ADD COLUMN "IdempotencyKey" varchar(100);
            UPDATE "PrintRefunds" SET "IdempotencyKey" = 'legacy-' || replace("Id"::text, '-', '') WHERE "IdempotencyKey" IS NULL;
            ALTER TABLE "PrintRefunds" ALTER COLUMN "IdempotencyKey" SET NOT NULL;
            DROP INDEX IF EXISTS "IX_PrintRefunds_TenantId_PrintPaymentId";
            CREATE INDEX "IX_PrintRefunds_TenantId_PrintPaymentId" ON "PrintRefunds" ("TenantId", "PrintPaymentId");
            CREATE UNIQUE INDEX "IX_PrintRefunds_TenantId_IdempotencyKey" ON "PrintRefunds" ("TenantId", "IdempotencyKey");
            ALTER TABLE "PrintPayments" ADD CONSTRAINT "CK_PrintPayments_RefundedAmountMinor"
                CHECK ("RefundedAmountMinor" >= 0 AND "RefundedAmountMinor" <= "AmountMinor");
            ALTER TABLE "PrintJobs" ADD CONSTRAINT "CK_PrintJobs_RejectedPageNumbersJson"
                CHECK (jsonb_typeof("RejectedPageNumbersJson") = 'array');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs" DROP CONSTRAINT IF EXISTS "CK_PrintJobs_RejectedPageNumbersJson";
            ALTER TABLE "PrintPayments" DROP CONSTRAINT IF EXISTS "CK_PrintPayments_RefundedAmountMinor";
            DROP INDEX IF EXISTS "IX_PrintRefunds_TenantId_IdempotencyKey";
            DROP INDEX IF EXISTS "IX_PrintRefunds_TenantId_PrintPaymentId";
            CREATE UNIQUE INDEX "IX_PrintRefunds_TenantId_PrintPaymentId" ON "PrintRefunds" ("TenantId", "PrintPaymentId");
            ALTER TABLE "PrintRefunds" DROP COLUMN IF EXISTS "IdempotencyKey";
            ALTER TABLE "PrintPayments" DROP COLUMN IF EXISTS "RefundedAmountMinor";
            ALTER TABLE "PrintJobs" DROP COLUMN IF EXISTS "RejectedPageNumbersJson";
            """);
    }
}
