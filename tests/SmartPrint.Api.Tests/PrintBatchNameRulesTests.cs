using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrintBatchNameRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingOrBlankNameRemainsOptional(string? value)
        => Assert.Null(PrintBatchNameRules.Normalize(value));

    [Fact]
    public void TrimsAndPreservesCustomerChosenName()
        => Assert.Equal("Rahul – College Notes", PrintBatchNameRules.Normalize("  Rahul – College Notes  "));

    [Fact]
    public void AllowsTheMaximumLength()
        => Assert.Equal(new string('x', 50), PrintBatchNameRules.Normalize(new string('x', 50)));

    [Fact]
    public void RejectsOverlongAndControlCharacterNames()
    {
        Assert.Throws<ArgumentException>(() => PrintBatchNameRules.Normalize(new string('x', 51)));
        Assert.Throws<ArgumentException>(() => PrintBatchNameRules.Normalize("College\nNotes"));
    }

    [Fact]
    public void KeepsAnExistingNameWhenARecoveryRequestOmitsIt()
        => Assert.Equal("Rahul – College Notes", PrintBatchNameRules.Resolve(null, new string?[] { "Rahul – College Notes", null }));

    [Fact]
    public void AllowsAddingANewNameToAnUnnamedBatch()
        => Assert.Equal("Chemistry Notes", PrintBatchNameRules.Resolve("Chemistry Notes", new string?[] { null, " " }));

    [Fact]
    public void RejectsRenamingAnExistingBatch()
        => Assert.Throws<ArgumentException>(() => PrintBatchNameRules.Resolve("New name", new string?[] { "Original name" }));

    [Fact]
    public void RejectsConflictingHistoricalNames()
        => Assert.Throws<ArgumentException>(() => PrintBatchNameRules.Resolve(null, new string?[] { "First", "Second" }));
}
