using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace SmartPrint.Api.Security;

public sealed record LocalDocumentPhotoRevisionGrantClaims(
    Guid TenantId,
    Guid JobId,
    Guid AgentDeviceId,
    Guid? ActorUserId);

public static class LocalDocumentPhotoRevisionGrant
{
    private const string GrantType = "local_document_photo_revision";

    public static bool IsFor(
        LocalDocumentPhotoRevisionGrantClaims grant,
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
        Guid? actorUserId,
        DateTimeOffset now,
        DateTimeOffset expiresUtc)
    {
        if (Encoding.UTF8.GetByteCount(signingKeyText) < 32)
            throw new ArgumentException("The signing key must contain at least 32 UTF-8 bytes.", nameof(signingKeyText));
        if (tenantId == Guid.Empty || jobId == Guid.Empty || agentDeviceId == Guid.Empty)
            throw new ArgumentException("A photo revision grant must be scoped to a tenant, job and Agent.");
        if (expiresUtc <= now || expiresUtc - now > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(expiresUtc), "Photo revision grants may live for no more than two minutes.");

        var claims = new List<Claim>
        {
            new("grant_type", GrantType),
            new("tenant_id", tenantId.ToString("D")),
            new("photo_revision_job_id", jobId.ToString("D")),
            new("photo_revision_agent_id", agentDeviceId.ToString("D"))
        };
        if (actorUserId is Guid actorId)
            claims.Add(new Claim("photo_revision_actor_id", actorId.ToString("D")));

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
        out LocalDocumentPhotoRevisionGrantClaims grant)
    {
        grant = new LocalDocumentPhotoRevisionGrantClaims(Guid.Empty, Guid.Empty, Guid.Empty, null);
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 ||
            Encoding.UTF8.GetByteCount(signingKeyText) < 32)
            return false;

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
                !Guid.TryParse(principal.FindFirst("photo_revision_job_id")?.Value, out var jobId) ||
                !Guid.TryParse(principal.FindFirst("photo_revision_agent_id")?.Value, out var agentId))
                return false;

            var actorUserId = Guid.TryParse(principal.FindFirst("photo_revision_actor_id")?.Value, out var parsedActorId)
                ? parsedActorId
                : (Guid?)null;
            grant = new LocalDocumentPhotoRevisionGrantClaims(tenantId, jobId, agentId, actorUserId);
            return true;
        }
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException or FormatException)
        {
            return false;
        }
    }
}
