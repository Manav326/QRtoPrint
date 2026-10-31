using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202610310007_FileBatchesAndRefunds")]
public sealed class FileBatchesAndRefunds : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "QrStations"
                ADD COLUMN "PublicCode" varchar(64) NULL;

            ALTER TABLE "PrintJobs"
                ADD COLUMN "BatchId" uuid NULL;
            CREATE INDEX "IX_PrintJobs_TenantId_BatchId"
                ON "PrintJobs" ("TenantId", "BatchId");

            ALTER TABLE "LocalPrintUploadTickets"
                ADD COLUMN "BatchId" uuid NULL;
            CREATE INDEX "IX_LocalPrintUploadTickets_TenantId_BatchId"
                ON "LocalPrintUploadTickets" ("TenantId", "BatchId");

            CREATE TABLE "PrintRefunds" (
                "Id" uuid NOT NULL,
                "TenantId" uuid NOT NULL,
                "PrintJobId" uuid NOT NULL,
                "PrintPaymentId" uuid NOT NULL,
                "AmountMinor" bigint NOT NULL,
                "Currency" varchar(3) NOT NULL,
                "Method" varchar(16) NOT NULL,
                "Status" varchar(24) NOT NULL,
                "Reference" varchar(100),
                "Reason" varchar(300) NOT NULL,
                "RefundedByUserId" uuid NOT NULL,
                "RefundedUtc" timestamptz NOT NULL,
                "CreatedUtc" timestamptz NOT NULL,
                CONSTRAINT "PK_PrintRefunds" PRIMARY KEY ("Id"),
                CONSTRAINT "CK_PrintRefunds_AmountMinor" CHECK ("AmountMinor" > 0),
                CONSTRAINT "CK_PrintRefunds_Method" CHECK ("Method" IN ('UPI', 'CASH')),
                CONSTRAINT "CK_PrintRefunds_Status" CHECK ("Status" = 'RECORDED_AS_SENT'),
                CONSTRAINT "FK_PrintRefunds_PrintJobs_TenantId_PrintJobId"
                    FOREIGN KEY ("TenantId", "PrintJobId")
                    REFERENCES "PrintJobs" ("TenantId", "Id") ON DELETE CASCADE,
                CONSTRAINT "FK_PrintRefunds_PrintPayments_TenantId_PrintPaymentId"
                    FOREIGN KEY ("TenantId", "PrintPaymentId")
                    REFERENCES "PrintPayments" ("TenantId", "Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_PrintRefunds_AspNetUsers_RefundedByUserId"
                    FOREIGN KEY ("RefundedByUserId")
                    REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX "IX_PrintRefunds_TenantId_PrintPaymentId"
                ON "PrintRefunds" ("TenantId", "PrintPaymentId");
            CREATE INDEX "IX_PrintRefunds_TenantId_PrintJobId_CreatedUtc"
                ON "PrintRefunds" ("TenantId", "PrintJobId", "CreatedUtc");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS "PrintRefunds";
            DROP INDEX IF EXISTS "IX_PrintJobs_TenantId_BatchId";
            ALTER TABLE "PrintJobs" DROP COLUMN IF EXISTS "BatchId";
            DROP INDEX IF EXISTS "IX_LocalPrintUploadTickets_TenantId_BatchId";
            ALTER TABLE "LocalPrintUploadTickets" DROP COLUMN IF EXISTS "BatchId";
            ALTER TABLE "QrStations" DROP COLUMN IF EXISTS "PublicCode";
            """);
    }
}
