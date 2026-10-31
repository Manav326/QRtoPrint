using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace SmartPrint.Api.Security;

public sealed record LocalDocumentPreviewGrantClaims(Guid TenantId, Guid JobId, Guid AgentDeviceId);

public static class LocalDocumentPreviewGrant
{
    private const string GrantType = "local_document_preview";

    public static bool IsFor(
        LocalDocumentPreviewGrantClaims grant,
        Guid tenantId,
        Guid jobId,
        Guid agentDeviceId) =>
        grant.TenantId == tenantId &&
        grant.JobId == jobId &&
        grant.AgentDeviceId == agentDeviceId;

    public static string Issue(
        string signingKeyText,
        string issuer,
        string audience,
        Guid tenantId,
        Guid jobId,
        Guid agentDeviceId,
        DateTimeOffset now,
        DateTimeOffset expiresUtc)
    {
        if (Encoding.UTF8.GetByteCount(signingKeyText) < 32)
            throw new ArgumentException("The signing key must contain at least 32 UTF-8 bytes.", nameof(signingKeyText));
        if (tenantId == Guid.Empty || jobId == Guid.Empty || agentDeviceId == Guid.Empty)
            throw new ArgumentException("A preview grant must be scoped to a tenant, job and Agent.");
        if (expiresUtc <= now || expiresUtc - now > TimeSpan.FromMinutes(3))
            throw new ArgumentOutOfRangeException(nameof(expiresUtc), "Preview grants may live for no more than three minutes.");

        var claims = new[]
        {
            new Claim("grant_type", GrantType),
            new Claim("tenant_id", tenantId.ToString("D")),
            new Claim("preview_job_id", jobId.ToString("D")),
            new Claim("preview_agent_id", agentDeviceId.ToString("D"))
        };
        var signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKeyText)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer,
            audience,
            claims,
            now.AddSeconds(-5).UtcDateTime,
            expiresUtc.UtcDateTime,
            signingCredentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static bool TryValidate(
        string? token,
        string signingKeyText,
        string issuer,
        string audience,
        DateTimeOffset now,
        out LocalDocumentPreviewGrantClaims grant)
    {
        grant = new LocalDocumentPreviewGrantClaims(Guid.Empty, Guid.Empty, Guid.Empty);
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 ||
            Encoding.UTF8.GetByteCount(signingKeyText) < 32)
        {
            return false;
        }

        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKeyText)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(5),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
            }, out var validatedToken);

            if (validatedToken is not JwtSecurityToken jwt ||
                !string.Equals(jwt.Header.Alg, SecurityAlgorithms.HmacSha256, StringComparison.Ordinal) ||
                jwt.ValidFrom > now.UtcDateTime.AddSeconds(5) ||
                jwt.ValidTo <= now.UtcDateTime.AddSeconds(-5) ||
                principal.FindFirst("grant_type")?.Value != GrantType ||
                !Guid.TryParse(principal.FindFirst("tenant_id")?.Value, out var tenantId) ||
                !Guid.TryParse(principal.FindFirst("preview_job_id")?.Value, out var jobId) ||
                !Guid.TryParse(principal.FindFirst("preview_agent_id")?.Value, out var agentDeviceId))
            {
                return false;
            }

            grant = new LocalDocumentPreviewGrantClaims(tenantId, jobId, agentDeviceId);
            return true;
        }
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException or FormatException)
        {
            return false;
        }
    }
}
