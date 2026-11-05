using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static class CustomerEndpoints
{
    public static RouteGroupBuilder MapCustomerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var customerApi = endpoints.MapGroup("/api/v1/customer").RequireAuthorization();

        customerApi.MapGet("/me", async (
            ClaimsPrincipal principal,
            UserManager<AppUser> userManager) =>
        {
            var customerId = CustomerIdentity.GetCustomerId(principal);
            if (customerId is null)
            {
                return Results.Forbid();
            }

            var user = await userManager.FindByIdAsync(customerId.Value.ToString());
            if (user is null || user.TenantId is not null)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new
            {
                user.Id,
                user.DisplayName,
                user.Email
            });
        });

        customerApi.MapGet("/jobs", async (
            ClaimsPrincipal principal,
            PublicCustomerJobQueryService customerJobs,
            CancellationToken ct) =>
        {
            var customerId = CustomerIdentity.GetCustomerId(principal);
            if (customerId is null)
            {
                return Results.Forbid();
            }

            var jobs = await customerJobs.GetJobsAsync(customerId.Value, ct);
            return Results.Ok(jobs);
        });



        return customerApi;
    }
}
