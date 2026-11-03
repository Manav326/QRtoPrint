using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202610300003_ShopPaymentVerification")]
public sealed class ShopPaymentVerification : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS "IX_AgentDevices_TenantId_FingerprintHash";
            CREATE UNIQUE INDEX "IX_AgentDevices_TenantId_FingerprintHash"
                ON "AgentDevices" ("TenantId", "FingerprintHash")
                WHERE "RevokedUtc" IS NULL;

            CREATE TABLE "ShopPaymentProfiles" (
                "TenantId" uuid NOT NULL PRIMARY KEY,
                "UpiEnabled" boolean NOT NULL,
                "UpiId" varchar(160),
                "PayeeName" varchar(120),
                "CashEnabled" boolean NOT NULL,
                "UpdatedUtc" timestamptz NOT NULL,
                CONSTRAINT "FK_ShopPaymentProfiles_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE CASCADE
            );

            INSERT INTO "ShopPaymentProfiles" ("TenantId", "UpiEnabled", "UpiId", "PayeeName", "CashEnabled", "UpdatedUtc")
            SELECT "Id", false, NULL, NULL, false, CURRENT_TIMESTAMP FROM "Tenants";

            CREATE TABLE "PrintPayments" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "PrintJobId" uuid NOT NULL,
                "Method" varchar(16) NOT NULL,
                "Status" varchar(32) NOT NULL,
                "AmountMinor" bigint NOT NULL,
                "Currency" varchar(3) NOT NULL,
                "UpiIdSnapshot" varchar(160),
                "PayeeNameSnapshot" varchar(120),
                "PaymentReference" varchar(100),
                "RejectionReason" varchar(300),
                "VerifiedByUserId" uuid,
                "VerifiedUtc" timestamptz,
                "CreatedUtc" timestamptz NOT NULL,
                "UpdatedUtc" timestamptz NOT NULL,
                CONSTRAINT "FK_PrintPayments_PrintJobs_TenantId_PrintJobId" FOREIGN KEY ("TenantId", "PrintJobId") REFERENCES "PrintJobs" ("TenantId", "Id") ON DELETE CASCADE,
                CONSTRAINT "AK_PrintPayments_TenantId_Id" UNIQUE ("TenantId", "Id")
            );
            CREATE INDEX "IX_PrintPayments_TenantId_PrintJobId_CreatedUtc" ON "PrintPayments" ("TenantId", "PrintJobId", "CreatedUtc");
            CREATE UNIQUE INDEX "IX_PrintPayments_TenantId_PrintJobId_Active" ON "PrintPayments" ("TenantId", "PrintJobId")
                WHERE "Status" IN ('AWAITING_CUSTOMER_PAYMENT','CUSTOMER_REPORTED','CASH_AWAITING_PAYMENT');
            CREATE INDEX "IX_PrintPayments_TenantId_Status" ON "PrintPayments" ("TenantId", "Status");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS "PrintPayments";
            DROP TABLE IF EXISTS "ShopPaymentProfiles";
            DROP INDEX IF EXISTS "IX_AgentDevices_TenantId_FingerprintHash";
            CREATE UNIQUE INDEX "IX_AgentDevices_TenantId_FingerprintHash"
                ON "AgentDevices" ("TenantId", "FingerprintHash")
                WHERE "RevokedUtc" IS NULL;
            """);
    }
}
