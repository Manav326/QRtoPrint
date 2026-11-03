using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611030002_AddPhotoPrintingPricingAndJobSnapshots")]
public sealed class AddPhotoPrintingPricingAndJobSnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "ShopPricingProfiles"
                ADD COLUMN "Photo35x45Minor" bigint NOT NULL DEFAULT 0,
                ADD COLUMN "Photo25x35Minor" bigint NOT NULL DEFAULT 0,
                ADD COLUMN "Photo2x2Minor" bigint NOT NULL DEFAULT 0,
                ADD COLUMN "Photo4x6Minor" bigint NOT NULL DEFAULT 0;

            ALTER TABLE "ShopPricingProfiles"
                ADD CONSTRAINT "CK_ShopPricingProfiles_PhotoRates"
                CHECK (
                    "Photo35x45Minor" >= 0 AND
                    "Photo25x35Minor" >= 0 AND
                    "Photo2x2Minor" >= 0 AND
                    "Photo4x6Minor" >= 0
                );

            ALTER TABLE "PrintJobs"
                ADD COLUMN "PrintKind" varchar(16) NOT NULL DEFAULT 'DOCUMENT',
                ADD COLUMN "PhotoSizeCode" varchar(12) NULL,
                ADD COLUMN "PhotoCount" integer NOT NULL DEFAULT 0,
                ADD COLUMN "PhotoUnitPriceMinorSnapshot" bigint NOT NULL DEFAULT 0;

            ALTER TABLE "PrintJobs"
                ADD CONSTRAINT "CK_PrintJobs_PrintKind"
                CHECK ("PrintKind" IN ('DOCUMENT', 'PHOTO')),
                ADD CONSTRAINT "CK_PrintJobs_PhotoQuoteSnapshot"
                CHECK (
                    (
                        "PrintKind" = 'DOCUMENT' AND
                        "PhotoSizeCode" IS NULL AND
                        "PhotoCount" = 0 AND
                        "PhotoUnitPriceMinorSnapshot" = 0
                    )
                    OR
                    (
                        "PrintKind" = 'PHOTO' AND
                        "PhotoSizeCode" IN ('35x45', '25x35', '2x2', '4x6') AND
                        "PhotoCount" BETWEEN 1 AND 100 AND
                        "PhotoUnitPriceMinorSnapshot" > 0
                    )
                );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs"
                DROP CONSTRAINT IF EXISTS "CK_PrintJobs_PhotoQuoteSnapshot",
                DROP CONSTRAINT IF EXISTS "CK_PrintJobs_PrintKind",
                DROP COLUMN IF EXISTS "PhotoUnitPriceMinorSnapshot",
                DROP COLUMN IF EXISTS "PhotoCount",
                DROP COLUMN IF EXISTS "PhotoSizeCode",
                DROP COLUMN IF EXISTS "PrintKind";

            ALTER TABLE "ShopPricingProfiles"
                DROP CONSTRAINT IF EXISTS "CK_ShopPricingProfiles_PhotoRates",
                DROP COLUMN IF EXISTS "Photo4x6Minor",
                DROP COLUMN IF EXISTS "Photo2x2Minor",
                DROP COLUMN IF EXISTS "Photo25x35Minor",
                DROP COLUMN IF EXISTS "Photo35x45Minor";
            """);
    }
}
