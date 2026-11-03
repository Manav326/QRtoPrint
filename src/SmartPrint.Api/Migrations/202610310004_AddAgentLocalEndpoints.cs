using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202610310004_AddAgentLocalEndpoints")]
public sealed class AddAgentLocalEndpoints : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "LocalEndpointsJson",
            table: "AgentDevices",
            type: "jsonb",
            nullable: false,
            defaultValueSql: "'[]'::jsonb");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "LocalEndpointsJson",
            table: "AgentDevices");
    }
}
