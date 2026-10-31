using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202610310002_PlatformSubscriptionAudit")]
public sealed class PlatformSubscriptionAuditMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "PlatformSubscriptionAudits" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "PreviousSubscriptionId" uuid,
                "PreviousStatus" varchar(24),
                "AssignedSubscriptionId" uuid NOT NULL,
                "PlanVersionId" uuid NOT NULL,
                "ActorUserId" uuid,
                "EventType" varchar(32) NOT NULL,
                "PlanCode" varchar(40) NOT NULL,
                "BillingInterval" varchar(16) NOT NULL,
                "Status" varchar(24) NOT NULL,
                "AmountMinor" bigint NOT NULL,
                "Currency" varchar(3) NOT NULL,
                "PeriodStartUtc" timestamptz NOT NULL,
                "PeriodEndUtc" timestamptz NOT NULL,
                "Reason" varchar(500) NOT NULL,
                "IdempotencyKey" varchar(100) NOT NULL,
                "CreatedUtc" timestamptz NOT NULL,
                CONSTRAINT "FK_PlatformSubscriptionAudits_Tenants_TenantId"
                    FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_PlatformSubscriptionAudits_TenantSubscriptions_AssignedSubscriptionId"
                    FOREIGN KEY ("AssignedSubscriptionId") REFERENCES "TenantSubscriptions" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_PlatformSubscriptionAudits_TenantSubscriptions_PreviousSubscriptionId"
                    FOREIGN KEY ("PreviousSubscriptionId") REFERENCES "TenantSubscriptions" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_PlatformSubscriptionAudits_SubscriptionPlanVersions_PlanVersionId"
                    FOREIGN KEY ("PlanVersionId") REFERENCES "SubscriptionPlanVersions" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_PlatformSubscriptionAudits_AspNetUsers_ActorUserId"
                    FOREIGN KEY ("ActorUserId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "CK_PlatformSubscriptionAudits_AmountMinor" CHECK ("AmountMinor" >= 0),
                CONSTRAINT "CK_PlatformSubscriptionAudits_Period" CHECK ("PeriodEndUtc" > "PeriodStartUtc"),
                CONSTRAINT "CK_PlatformSubscriptionAudits_Currency" CHECK ("Currency" ~ '^[A-Z]{3}$')
            );
            CREATE UNIQUE INDEX "IX_PlatformSubscriptionAudits_IdempotencyKey"
                ON "PlatformSubscriptionAudits" ("IdempotencyKey");
            CREATE UNIQUE INDEX "IX_PlatformSubscriptionAudits_AssignedSubscriptionId"
                ON "PlatformSubscriptionAudits" ("AssignedSubscriptionId");
            CREATE INDEX "IX_PlatformSubscriptionAudits_TenantId_CreatedUtc"
                ON "PlatformSubscriptionAudits" ("TenantId", "CreatedUtc" DESC);

            CREATE OR REPLACE FUNCTION reject_platform_subscription_audit_mutation()
            RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'Platform subscription assignment audit is append-only; create a compensating assignment instead'
                    USING ERRCODE='23514';
            END;
            $$;

            CREATE TRIGGER trg_platform_subscription_audit_immutable
            BEFORE UPDATE OR DELETE ON "PlatformSubscriptionAudits"
            FOR EACH ROW EXECUTE FUNCTION reject_platform_subscription_audit_mutation();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS trg_platform_subscription_audit_immutable ON "PlatformSubscriptionAudits";
            DROP TABLE IF EXISTS "PlatformSubscriptionAudits";
            DROP FUNCTION IF EXISTS reject_platform_subscription_audit_mutation();
            """);
    }
}
