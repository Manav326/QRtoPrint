using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611010001_AddDocumentPurgedUtc")]
public sealed class AddDocumentPurgedUtc : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "PurgedUtc", table: "PrintDocuments",
            type: "timestamp with time zone", nullable: true);
        migrationBuilder.CreateIndex(
            name: "IX_PrintDocuments_StorageMode_PurgedUtc",
            table: "PrintDocuments",
            columns: new[] { "StorageMode", "PurgedUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_PrintDocuments_StorageMode_PurgedUtc", table: "PrintDocuments");
        migrationBuilder.DropColumn(name: "PurgedUtc", table: "PrintDocuments");
    }
}
