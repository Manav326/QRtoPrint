using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Security;

public sealed class TokenIssuer(IConfiguration configuration, UserManager<AppUser> userManager)
{
    public async Task<TokenPair> CreateAsync(AppUser user)
    {
        var issuer = configuration["Authentication:Issuer"] ?? "QRtoPrint";
        var audience = configuration["Authentication:Audience"] ?? "QRtoPrint.Clients";
        var keyText = configuration["Authentication:SigningKey"]
            ?? throw new InvalidOperationException("Authentication:SigningKey is not configured.");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(keyText));
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new("display_name", user.DisplayName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        if (user.TenantId is Guid tenantId)
        {
            claims.Add(new Claim("tenant_id", tenantId.ToString()));
        }

        claims.AddRange(await userManager.GetClaimsAsync(user));
        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(30);
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(issuer, audience, claims, now, expires, credentials);
        var accessToken = new JwtSecurityTokenHandler().WriteToken(jwt);

        var refreshToken = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
            RandomNumberGenerator.GetBytes(48));
        return new TokenPair(accessToken, refreshToken, expires);
    }

    public static string HashRefreshToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}

public sealed record TokenPair(string AccessToken, string RefreshToken, DateTime AccessTokenExpiresUtc);
