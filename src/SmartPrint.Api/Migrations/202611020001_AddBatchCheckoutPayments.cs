using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611020001_AddBatchCheckoutPayments")]
public sealed class AddBatchCheckoutPayments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "PrintBatchPayments" (
                "Id" uuid NOT NULL,
                "TenantId" uuid NOT NULL,
                "CustomerSessionId" uuid NOT NULL,
                "BatchId" uuid NOT NULL,
                "Method" varchar(16) NOT NULL,
                "Status" varchar(32) NOT NULL,
                "AmountMinor" bigint NOT NULL,
                "Currency" varchar(3) NOT NULL,
                "UpiIdSnapshot" varchar(160) NULL,
                "PayeeNameSnapshot" varchar(120) NULL,
                "PaymentReference" varchar(100) NULL,
                "RejectionReason" varchar(300) NULL,
                "VerifiedByUserId" uuid NULL,
                "VerifiedUtc" timestamp with time zone NULL,
                "CreatedUtc" timestamp with time zone NOT NULL,
                "UpdatedUtc" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_PrintBatchPayments" PRIMARY KEY ("Id"),
                CONSTRAINT "AK_PrintBatchPayments_TenantId_Id" UNIQUE ("TenantId", "Id"),
                CONSTRAINT "CK_PrintBatchPayments_AmountMinor" CHECK ("AmountMinor" > 0),
                CONSTRAINT "FK_PrintBatchPayments_Tenants_TenantId"
                    FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_PrintBatchPayments_AspNetUsers_VerifiedByUserId"
                    FOREIGN KEY ("VerifiedByUserId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT
            );

            CREATE INDEX "IX_PrintBatchPayments_TenantId_BatchId_CreatedUtc"
                ON "PrintBatchPayments" ("TenantId", "BatchId", "CreatedUtc");

            CREATE UNIQUE INDEX "IX_PrintBatchPayments_TenantId_BatchId_Active"
                ON "PrintBatchPayments" ("TenantId", "BatchId")
                WHERE "Status" IN ('AWAITING_CUSTOMER_PAYMENT', 'CUSTOMER_REPORTED', 'CASH_AWAITING_PAYMENT');

            ALTER TABLE "PrintPayments"
                ADD COLUMN "BatchPaymentId" uuid NULL;

            CREATE INDEX "IX_PrintPayments_TenantId_BatchPaymentId"
                ON "PrintPayments" ("TenantId", "BatchPaymentId");

            ALTER TABLE "PrintPayments"
                ADD CONSTRAINT "FK_PrintPayments_PrintBatchPayments_TenantId_BatchPaymentId"
                FOREIGN KEY ("TenantId", "BatchPaymentId")
                REFERENCES "PrintBatchPayments" ("TenantId", "Id")
                ON DELETE RESTRICT;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintPayments"
                DROP CONSTRAINT IF EXISTS "FK_PrintPayments_PrintBatchPayments_TenantId_BatchPaymentId";

            DROP INDEX IF EXISTS "IX_PrintPayments_TenantId_BatchPaymentId";
            ALTER TABLE "PrintPayments" DROP COLUMN IF EXISTS "BatchPaymentId";

            DROP INDEX IF EXISTS "IX_PrintBatchPayments_TenantId_BatchId_Active";
            DROP INDEX IF EXISTS "IX_PrintBatchPayments_TenantId_BatchId_CreatedUtc";
            DROP TABLE IF EXISTS "PrintBatchPayments";
            """);
    }
}
