using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PaymentReferenceRulesTests
{
    [Theory]
    [InlineData("1234")]
    [InlineData("ABCD1234")]
    [InlineData("utr-2026-11-04")]
    [InlineData("ref_1234")]
    [InlineData("A1b2-C3d4_E5f6")]
    [InlineData("1234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890")]
    public void AcceptsReferencesWithinLengthAndCharacterPolicy(string reference)
    {
        Assert.True(PaymentReferenceRules.IsValid(reference));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123")]
    [InlineData("123 456")]
    [InlineData("abc/1234")]
    [InlineData("abc.1234")]
    [InlineData("abc@1234")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901")]
    public void RejectsInvalidReferences(string? reference)
    {
        Assert.False(PaymentReferenceRules.IsValid(reference));
    }
}
