using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611010002_AddTenantStatusAudits")]
public sealed class AddTenantStatusAudits : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TenantStatusAudits",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                PreviousStatus = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                NewStatus = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TenantStatusAudits", x => x.Id);
                table.ForeignKey(name: "FK_TenantStatusAudits_AspNetUsers_ActorUserId", x => x.ActorUserId,
                    principalTable: "AspNetUsers", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey(name: "FK_TenantStatusAudits_Tenants_TenantId", x => x.TenantId,
                    principalTable: "Tenants", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            });
        migrationBuilder.CreateIndex(name: "IX_TenantStatusAudits_TenantId_CreatedUtc",
            table: "TenantStatusAudits", columns: new[] { "TenantId", "CreatedUtc" });
        migrationBuilder.CreateIndex(name: "IX_TenantStatusAudits_ActorUserId",
            table: "TenantStatusAudits", column: "ActorUserId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "TenantStatusAudits");
    }
}