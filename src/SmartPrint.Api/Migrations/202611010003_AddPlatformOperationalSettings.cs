using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611010003_AddPlatformOperationalSettings")]
public sealed class AddPlatformOperationalSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PlatformOperationalSettings",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false),
                TrialMaxMonthlyPrintJobs = table.Column<int>(type: "integer", nullable: false, defaultValue: 3000),
                CloudCompletedDocumentRetentionDays = table.Column<int>(type: "integer", nullable: false, defaultValue: 30),
                AgentCompletedDocumentRetentionDays = table.Column<int>(type: "integer", nullable: false, defaultValue: 30),
                AbandonedDocumentRetentionDays = table.Column<int>(type: "integer", nullable: false, defaultValue: 30),
                PendingLocalUploadRetentionDays = table.Column<int>(type: "integer", nullable: false, defaultValue: 30),
                ExpiredUploadTicketRetentionDays = table.Column<int>(type: "integer", nullable: false, defaultValue: 7),
                EmptyCustomerSessionRetentionDays = table.Column<int>(type: "integer", nullable: false, defaultValue: 90),
                StaleRefreshTokenRetentionDays = table.Column<int>(type: "integer", nullable: false, defaultValue: 30),
                ApiRetentionSweepHours = table.Column<int>(type: "integer", nullable: false, defaultValue: 24),
                AgentRetentionSweepHours = table.Column<int>(type: "integer", nullable: false, defaultValue: 6),
                AllowPaidJobClaimsWhenSuspended = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PlatformOperationalSettings", x => x.Id);
                table.CheckConstraint("CK_PlatformOperationalSettings_Singleton", "\"Id\" = 1");
                table.CheckConstraint("CK_PlatformOperationalSettings_TrialQuota", "\"TrialMaxMonthlyPrintJobs\" BETWEEN 1 AND 10000000");
                table.CheckConstraint("CK_PlatformOperationalSettings_RetentionDays",
                    "\"CloudCompletedDocumentRetentionDays\" BETWEEN 1 AND 3650 AND \"AgentCompletedDocumentRetentionDays\" BETWEEN 1 AND 3650 AND \"AbandonedDocumentRetentionDays\" BETWEEN 1 AND 3650 AND \"PendingLocalUploadRetentionDays\" BETWEEN 1 AND 3650 AND \"ExpiredUploadTicketRetentionDays\" BETWEEN 1 AND 3650 AND \"EmptyCustomerSessionRetentionDays\" BETWEEN 1 AND 3650 AND \"StaleRefreshTokenRetentionDays\" BETWEEN 1 AND 3650");
                table.CheckConstraint("CK_PlatformOperationalSettings_SweepHours",
                    "\"ApiRetentionSweepHours\" BETWEEN 1 AND 168 AND \"AgentRetentionSweepHours\" BETWEEN 1 AND 24");
                table.ForeignKey(name: "FK_PlatformOperationalSettings_AspNetUsers_UpdatedByUserId",
                    column: x => x.UpdatedByUserId, principalTable: "AspNetUsers", principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "PlatformOperationalSettingsAudits",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ChangesJson = table.Column<string>(type: "jsonb", nullable: false),
                Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PlatformOperationalSettingsAudits", x => x.Id);
                table.ForeignKey(name: "FK_PlatformOperationalSettingsAudits_AspNetUsers_ActorUserId",
                    column: x => x.ActorUserId, principalTable: "AspNetUsers", principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });
        migrationBuilder.CreateIndex(
            name: "IX_PlatformOperationalSettings_UpdatedByUserId",
            table: "PlatformOperationalSettings", column: "UpdatedByUserId");
        migrationBuilder.CreateIndex(
            name: "IX_PlatformOperationalSettingsAudits_ActorUserId",
            table: "PlatformOperationalSettingsAudits", column: "ActorUserId");
        migrationBuilder.CreateIndex(
            name: "IX_PlatformOperationalSettingsAudits_CreatedUtc",
            table: "PlatformOperationalSettingsAudits", column: "CreatedUtc");

        migrationBuilder.InsertData(
            table: "PlatformOperationalSettings",
            columns: new[]
            {
                "Id", "TrialMaxMonthlyPrintJobs", "CloudCompletedDocumentRetentionDays",
                "AgentCompletedDocumentRetentionDays", "AbandonedDocumentRetentionDays",
                "PendingLocalUploadRetentionDays", "ExpiredUploadTicketRetentionDays",
                "EmptyCustomerSessionRetentionDays", "StaleRefreshTokenRetentionDays",
                "ApiRetentionSweepHours", "AgentRetentionSweepHours",
                "AllowPaidJobClaimsWhenSuspended", "UpdatedUtc"
            },
            values: new object[]
            {
                1, 3000, 30, 30, 30, 30, 7, 90, 30, 24, 6, true, DateTimeOffset.UtcNow
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "PlatformOperationalSettingsAudits");
        migrationBuilder.DropTable(name: "PlatformOperationalSettings");
    }
}
