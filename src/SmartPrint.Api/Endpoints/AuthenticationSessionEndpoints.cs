using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Endpoints;

public static partial class AuthenticationEndpoints
{
    private static void MapAuthenticationSessionEndpoints(RouteGroupBuilder auth)
    {
        auth.MapPost("/customer-login", async (
            LoginRequest request,
            UserManager<AppUser> userManager,
            SignInManager<AppUser> signInManager,
            SmartPrintDbContext db,
            TokenIssuer tokenIssuer,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.Unauthorized();
            }

            var user = await userManager.FindByEmailAsync(request.Email.Trim());
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var claims = await userManager.GetClaimsAsync(user);
            if (!CustomerIdentity.IsCustomer(user, claims))
            {
                return Results.Unauthorized();
            }

            var signIn = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
            if (!signIn.Succeeded)
            {
                return Results.Unauthorized();
            }

            var tokens = await tokenIssuer.CreateAsync(user);
            var now = DateTimeOffset.UtcNow;
            db.RefreshTokens.Add(new RefreshToken
            {
                UserId = user.Id,
                TokenHash = TokenIssuer.HashRefreshToken(tokens.RefreshToken),
                CreatedUtc = now,
                ExpiresUtc = now.AddDays(30)
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new TokenResponse("Bearer", tokens.AccessToken, tokens.RefreshToken, tokens.AccessTokenExpiresUtc));
        }).AllowAnonymous();

        auth.MapPost("/login", async (
            LoginRequest request,
            UserManager<AppUser> userManager,
            SignInManager<AppUser> signInManager,
            SmartPrintDbContext db,
            TokenIssuer tokenIssuer,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.Unauthorized();
            }

            var user = await userManager.FindByEmailAsync(request.Email.Trim());
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var userClaims = await userManager.GetClaimsAsync(user);
            if (user.TenantId is null && !PlatformAdminAuthorization.IsPlatformAdmin(user.TenantId, userClaims))
            {
                return Results.Unauthorized();
            }

            var signIn = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
            if (!signIn.Succeeded)
            {
                return Results.Unauthorized();
            }

            var pair = await tokenIssuer.CreateAsync(user);
            var now = DateTimeOffset.UtcNow;
            db.RefreshTokens.Add(new RefreshToken
            {
                UserId = user.Id,
                TokenHash = TokenIssuer.HashRefreshToken(pair.RefreshToken),
                CreatedUtc = now,
                ExpiresUtc = now.AddDays(30)
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new TokenResponse("Bearer", pair.AccessToken, pair.RefreshToken, pair.AccessTokenExpiresUtc));
        }).AllowAnonymous();

        auth.MapPost("/refresh", async (
            RefreshRequest request,
            SmartPrintDbContext db,
            UserManager<AppUser> userManager,
            TokenIssuer tokenIssuer,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.RefreshToken))
            {
                return Results.Unauthorized();
            }

            var hash = TokenIssuer.HashRefreshToken(request.RefreshToken);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var existing = await db.RefreshTokens.SingleOrDefaultAsync(token =>
                token.TokenHash == hash && token.RevokedUtc == null && token.ExpiresUtc > now, ct);

            if (existing is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.Unauthorized();
            }

            var claimed = await db.RefreshTokens
                .Where(token => token.Id == existing.Id && token.RevokedUtc == null && token.ExpiresUtc > now)
                .ExecuteUpdateAsync(update => update.SetProperty(token => token.RevokedUtc, now), ct);
            if (claimed != 1)
            {
                await transaction.RollbackAsync(ct);
                return Results.Unauthorized();
            }

            var user = await userManager.FindByIdAsync(existing.UserId.ToString());
            if (user is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.Unauthorized();
            }

            var userClaims = await userManager.GetClaimsAsync(user);
            var isCustomer = CustomerIdentity.IsCustomer(user, userClaims);
            if (user.TenantId is null &&
                !PlatformAdminAuthorization.IsPlatformAdmin(user.TenantId, userClaims) &&
                !isCustomer)
            {
                await transaction.RollbackAsync(ct);
                return Results.Unauthorized();
            }

            var pair = await tokenIssuer.CreateAsync(user);
            var replacement = new RefreshToken
            {
                UserId = user.Id,
                TokenHash = TokenIssuer.HashRefreshToken(pair.RefreshToken),
                CreatedUtc = now,
                ExpiresUtc = now.AddDays(30)
            };
            existing.ReplacedByTokenId = replacement.Id;
            db.RefreshTokens.Add(replacement);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new TokenResponse("Bearer", pair.AccessToken, pair.RefreshToken, pair.AccessTokenExpiresUtc));
        }).AllowAnonymous();

        auth.MapPost("/logout", async (RefreshRequest request, SmartPrintDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.RefreshToken))
            {
                return Results.NoContent();
            }

            var hash = TokenIssuer.HashRefreshToken(request.RefreshToken);
            var now = DateTimeOffset.UtcNow;
            await db.RefreshTokens.Where(token => token.TokenHash == hash && token.RevokedUtc == null)
                .ExecuteUpdateAsync(update => update.SetProperty(token => token.RevokedUtc, now), ct);
            return Results.NoContent();
        }).AllowAnonymous();
    }
}
