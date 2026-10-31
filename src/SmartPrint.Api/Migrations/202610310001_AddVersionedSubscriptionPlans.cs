using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202610310001_AddVersionedSubscriptionPlans")]
public sealed class AddVersionedSubscriptionPlans : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "SubscriptionPlans" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Code" varchar(40) NOT NULL,
                "Name" varchar(100) NOT NULL,
                "Description" varchar(500),
                "IsActive" boolean NOT NULL,
                "IsSystemPlan" boolean NOT NULL,
                "CreatedUtc" timestamptz NOT NULL,
                "UpdatedUtc" timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX "IX_SubscriptionPlans_Code" ON "SubscriptionPlans" ("Code");

            CREATE TABLE "SubscriptionPlanVersions" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "PlanId" uuid NOT NULL,
                "Version" integer NOT NULL,
                "Status" varchar(16) NOT NULL,
                "Currency" varchar(3) NOT NULL,
                "MonthlyPriceMinor" bigint,
                "AnnualPriceMinor" bigint,
                "MaxPrinters" integer,
                "MaxAgentDevices" integer,
                "MaxQrStations" integer,
                "MaxMonthlyPrintJobs" integer,
                "MaxDocumentBytes" bigint,
                "CloudAssistedPrintingEnabled" boolean NOT NULL,
                "WhiteLabelEnabled" boolean NOT NULL,
                "AdvancedReportsEnabled" boolean NOT NULL,
                "CreatedUtc" timestamptz NOT NULL,
                "UpdatedUtc" timestamptz NOT NULL,
                "PublishedUtc" timestamptz,
                CONSTRAINT "FK_SubscriptionPlanVersions_SubscriptionPlans_PlanId"
                    FOREIGN KEY ("PlanId") REFERENCES "SubscriptionPlans" ("Id") ON DELETE CASCADE,
                CONSTRAINT "CK_SubscriptionPlanVersions_Version" CHECK ("Version" > 0),
                CONSTRAINT "CK_SubscriptionPlanVersions_Status" CHECK ("Status" IN ('Draft', 'Published', 'Retired')),
                CONSTRAINT "CK_SubscriptionPlanVersions_Currency" CHECK ("Currency" ~ '^[A-Z]{3}$'),
                CONSTRAINT "CK_SubscriptionPlanVersions_MonthlyPriceMinor" CHECK ("MonthlyPriceMinor" IS NULL OR "MonthlyPriceMinor" >= 0),
                CONSTRAINT "CK_SubscriptionPlanVersions_AnnualPriceMinor" CHECK ("AnnualPriceMinor" IS NULL OR "AnnualPriceMinor" >= 0),
                CONSTRAINT "CK_SubscriptionPlanVersions_MaxPrinters" CHECK ("MaxPrinters" IS NULL OR "MaxPrinters" > 0),
                CONSTRAINT "CK_SubscriptionPlanVersions_MaxAgentDevices" CHECK ("MaxAgentDevices" IS NULL OR "MaxAgentDevices" > 0),
                CONSTRAINT "CK_SubscriptionPlanVersions_MaxQrStations" CHECK ("MaxQrStations" IS NULL OR "MaxQrStations" > 0),
                CONSTRAINT "CK_SubscriptionPlanVersions_MaxMonthlyPrintJobs" CHECK ("MaxMonthlyPrintJobs" IS NULL OR "MaxMonthlyPrintJobs" > 0),
                CONSTRAINT "CK_SubscriptionPlanVersions_MaxDocumentBytes" CHECK ("MaxDocumentBytes" IS NULL OR "MaxDocumentBytes" > 0)
            );
            CREATE UNIQUE INDEX "IX_SubscriptionPlanVersions_PlanId_Version"
                ON "SubscriptionPlanVersions" ("PlanId", "Version");
            CREATE INDEX "IX_SubscriptionPlanVersions_PlanId_Status"
                ON "SubscriptionPlanVersions" ("PlanId", "Status");

            INSERT INTO "SubscriptionPlans"
                ("Id", "Code", "Name", "Description", "IsActive", "IsSystemPlan", "CreatedUtc", "UpdatedUtc")
            VALUES
                ('e0af4c2a-82b8-4bd1-9bc4-cb3bffb51001', 'TRIAL', 'Trial',
                 'Protected system plan for the existing 14-day introductory trial. Limits remain unset to preserve existing shop behaviour.',
                 TRUE, TRUE, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);

            INSERT INTO "SubscriptionPlanVersions"
                ("Id", "PlanId", "Version", "Status", "Currency", "MonthlyPriceMinor", "AnnualPriceMinor",
                 "MaxPrinters", "MaxAgentDevices", "MaxQrStations", "MaxMonthlyPrintJobs", "MaxDocumentBytes",
                 "CloudAssistedPrintingEnabled", "WhiteLabelEnabled", "AdvancedReportsEnabled",
                 "CreatedUtc", "UpdatedUtc", "PublishedUtc")
            VALUES
                ('f2b849a1-1be8-4a88-a6c5-7e8e80d0c002', 'e0af4c2a-82b8-4bd1-9bc4-cb3bffb51001',
                 1, 'Published', 'INR', NULL, NULL, NULL, NULL, NULL, NULL, NULL,
                 TRUE, FALSE, TRUE, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);

            ALTER TABLE "TenantSubscriptions" ADD COLUMN "PlanVersionId" uuid;
            UPDATE "TenantSubscriptions"
                SET "PlanVersionId" = 'f2b849a1-1be8-4a88-a6c5-7e8e80d0c002'
                WHERE "PlanCode" = 'TRIAL';
            CREATE INDEX "IX_TenantSubscriptions_PlanVersionId"
                ON "TenantSubscriptions" ("PlanVersionId");
            ALTER TABLE "TenantSubscriptions"
                ADD CONSTRAINT "FK_TenantSubscriptions_SubscriptionPlanVersions_PlanVersionId"
                FOREIGN KEY ("PlanVersionId") REFERENCES "SubscriptionPlanVersions" ("Id") ON DELETE RESTRICT;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "TenantSubscriptions"
                DROP CONSTRAINT IF EXISTS "FK_TenantSubscriptions_SubscriptionPlanVersions_PlanVersionId";
            DROP INDEX IF EXISTS "IX_TenantSubscriptions_PlanVersionId";
            ALTER TABLE "TenantSubscriptions" DROP COLUMN IF EXISTS "PlanVersionId";
            DROP TABLE IF EXISTS "SubscriptionPlanVersions";
            DROP TABLE IF EXISTS "SubscriptionPlans";
            """);
    }
}
