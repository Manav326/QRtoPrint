using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Security;

/// <summary>
/// A SmartPrint customer is a global principal: no shop tenant is attached to the
/// identity. Shop ownership is represented separately by each QR print session.
/// </summary>
public static class CustomerIdentity
{
    public const string RoleClaim = "customer_role";
    public const string RoleValue = "Customer";

    public static bool IsCustomer(AppUser user, IEnumerable<Claim> claims) =>
        user.TenantId is null &&
        claims.Any(claim => claim.Type == RoleClaim && claim.Value == RoleValue);

    public static Guid? GetCustomerId(ClaimsPrincipal principal)
    {
        if (principal.FindFirstValue(RoleClaim) != RoleValue ||
            principal.HasClaim(claim => claim.Type == "tenant_id") ||
            !Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var customerId))
        {
            return null;
        }

        return customerId;
    }
}
