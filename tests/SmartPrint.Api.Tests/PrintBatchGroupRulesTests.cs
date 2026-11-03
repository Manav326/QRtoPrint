using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrintBatchGroupRulesTests
{
    [Fact]
    public void EmptyOrMissingGroupUsesLooseFiles()
    {
        Assert.Equal(PrintBatchGroupRules.LooseFilesName, PrintBatchGroupRules.Normalize(null));
        Assert.Equal(PrintBatchGroupRules.LooseFilesName, PrintBatchGroupRules.Normalize("  "));
    }

    [Fact]
    public void TrimsAndPreservesAValidFolderName()
        => Assert.Equal("School Work", PrintBatchGroupRules.Normalize("  School Work  "));

    [Theory]
    [InlineData("../private")]
    [InlineData("folder/name")]
    [InlineData("folder\\name")]
    [InlineData(".")]
    [InlineData("..")]
    public void RejectsPathsAndTraversalSegments(string value)
        => Assert.Throws<ArgumentException>(() => PrintBatchGroupRules.Normalize(value));

    [Fact]
    public void RejectsOverlongAndControlCharacterNames()
    {
        Assert.Throws<ArgumentException>(() => PrintBatchGroupRules.Normalize(new string('x', PrintBatchGroupRules.MaximumNameLength + 1)));
        Assert.Throws<ArgumentException>(() => PrintBatchGroupRules.Normalize("Folder\nName"));
    }
}
