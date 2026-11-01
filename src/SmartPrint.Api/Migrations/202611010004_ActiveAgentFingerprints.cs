using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611010004_ActiveAgentFingerprints")]
public sealed class ActiveAgentFingerprints : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The original migration created an unfiltered unique index. That prevented
        // a revoked machine from being enrolled again, even though the EF model
        // already defines uniqueness only for non-revoked identities.
        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS "IX_AgentDevices_TenantId_FingerprintHash";
            CREATE UNIQUE INDEX "IX_AgentDevices_TenantId_FingerprintHash"
                ON "AgentDevices" ("TenantId", "FingerprintHash")
                WHERE "RevokedUtc" IS NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Intentionally retain the filtered index on rollback. Re-enrollment creates
        // a new active row while preserving revoked history; restoring the former
        // unfiltered unique index would fail or require deleting legitimate records.
        // The filtered index is also the invariant represented by the current EF model.
    }
}