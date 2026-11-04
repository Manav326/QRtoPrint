using SmartPrint.Api.Contracts;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Tests;

public sealed class PlatformPlanValidationRulesTests
{
    [Fact]
    public void Create_metadata_requires_a_valid_code_and_name()
    {
        var errors = PlatformPlanValidationRules.ValidateMetadata("bad-code", " ", null);

        Assert.Contains("code", errors.Keys);
        Assert.Contains("name", errors.Keys);
    }

    [Fact]
    public void Update_metadata_does_not_require_a_code()
    {
        var errors = PlatformPlanValidationRules.ValidateMetadata(
            null, "  Updated Plan  ", "  Description  ", allowMissingCode: true);

        Assert.Empty(errors);
        Assert.Equal("Description", PlatformPlanValidationRules.NormalizeDescription("  Description  "));
    }

    [Fact]
    public void Metadata_rejects_descriptions_over_five_hundred_characters()
    {
        var errors = PlatformPlanValidationRules.ValidateMetadata(
            "STARTER", "Starter", new string('x', 501));

        Assert.Contains("description", errors.Keys);
    }

    [Fact]
    public void Version_validation_rejects_invalid_currency_prices_and_quotas()
    {
        var request = new PlatformPlanVersionRequest(
            "inr",
            -1,
            null,
            0,
            -1,
            1,
            0,
            0,
            false,
            false,
            false);

        var errors = PlatformPlanValidationRules.ValidateVersion(request);

        Assert.Contains("currency", errors.Keys);
        Assert.Contains("price", errors.Keys);
        Assert.Contains(nameof(request.MaxPrinters), errors.Keys);
        Assert.Contains(nameof(request.MaxAgentDevices), errors.Keys);
        Assert.Contains(nameof(request.MaxMonthlyPrintJobs), errors.Keys);
        Assert.Contains("maxDocumentBytes", errors.Keys);
    }

    [Fact]
    public void Version_validation_allows_explicit_free_price_and_unlimited_quotas()
    {
        var request = new PlatformPlanVersionRequest(
            "INR",
            0,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            false,
            false);

        Assert.Empty(PlatformPlanValidationRules.ValidateVersion(request));
    }
}
