using SmartPrint.Api.Contracts;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class AgentEnrollmentRulesTests
{
    [Fact]
    public void TrimsEnrollmentCodeAndDeviceNameButPreservesFingerprintIdentity()
    {
        var request = new AgentEnrollmentRequest("  CODE-123  ", " fingerprint ", "  Office PC  ");

        Assert.True(AgentEnrollmentRules.TryNormalize(request, out var code, out var fingerprint, out var name));
        Assert.Equal("CODE-123", code);
        Assert.Equal(" fingerprint ", fingerprint);
        Assert.Equal("Office PC", name);
    }

    [Theory]
    [InlineData("", "fingerprint", "PC")]
    [InlineData("   ", "fingerprint", "PC")]
    [InlineData("code", "", "PC")]
    [InlineData("code", "   ", "PC")]
    [InlineData("code", "fingerprint", "")]
    [InlineData("code", "fingerprint", "   ")]
    public void RejectsMissingEnrollmentFields(string code, string fingerprint, string deviceName)
    {
        Assert.False(AgentEnrollmentRules.TryNormalize(
            new AgentEnrollmentRequest(code, fingerprint, deviceName), out _, out _, out _));
    }

    [Fact]
    public void EnforcesEnrollmentCodeFingerprintAndDeviceNameLimits()
    {
        Assert.False(AgentEnrollmentRules.TryNormalize(
            new AgentEnrollmentRequest(new string('c', 101), "fingerprint", "PC"), out _, out _, out _));
        Assert.False(AgentEnrollmentRules.TryNormalize(
            new AgentEnrollmentRequest("code", new string('f', 257), "PC"), out _, out _, out _));
        Assert.False(AgentEnrollmentRules.TryNormalize(
            new AgentEnrollmentRequest("code", "fingerprint", new string('n', 161)), out _, out _, out _));
        Assert.True(AgentEnrollmentRules.TryNormalize(
            new AgentEnrollmentRequest(new string('c', 100), new string('f', 256), new string('n', 160)), out _, out _, out _));
    }
}
