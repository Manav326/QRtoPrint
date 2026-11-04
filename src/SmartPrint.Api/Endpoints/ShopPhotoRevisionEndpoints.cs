using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Security;
using SmartPrint.Api.Services;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopPhotoRevisionEndpoints
{
    public static RouteGroupBuilder MapShopPhotoRevisionEndpoints(
        this RouteGroupBuilder shop,
        string signingKeyText,
        string issuer,
        string audience)
    {
        shop.MapPost("/print-jobs/{jobId:guid}/photo-revision-grant", async (
            Guid jobId,
            ClaimsPrincipal principal,
            ShopPhotoRevisionService revisions,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await revisions.CreateLocalGrantAsync(
                jobId, tenantId, GetActorId(principal), signingKeyText, issuer, audience, ct);
            return MapResult(result);
        });

        shop.MapPost("/print-jobs/{jobId:guid}/photo-revision", async (
            Guid jobId,
            ClaimsPrincipal principal,
            HttpContext context,
            ShopPhotoRevisionService revisions,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            if (!context.Request.HasFormContentType)
                return Results.BadRequest(new
                {
                    code = "photo_revision_form_required",
                    message = "Send the prepared photo sheet as a multipart file."
                });

            var form = await context.Request.ReadFormAsync(ct);
            if (form.Files.Count != 1)
                return Results.BadRequest(new
                {
                    code = "one_photo_sheet_required",
                    message = "Save exactly one prepared photo-sheet PDF."
                });

            var photoSizeCode = form["photoSizeCode"].FirstOrDefault() ?? string.Empty;
            if (!int.TryParse(form["photoCount"].FirstOrDefault(), out var photoCount) ||
                !int.TryParse(form["photosPerPage"].FirstOrDefault(), out var photosPerPage))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["photoDetails"] = ["Choose a supported ID photo size, quantity and photos-per-page layout."]
                });
            }

            var result = await revisions.SaveCloudRevisionAsync(
                jobId,
                tenantId,
                GetActorId(principal),
                form.Files[0],
                photoSizeCode,
                photoCount,
                photosPerPage,
                ct);
            return MapResult(result);
        });

        return shop;
    }

    private static IResult MapResult(ShopPhotoRevisionOutcome result)
    {
        if (result.StatusCode == StatusCodes.Status200OK)
            return Results.Ok(result.Payload);
        return result.Payload is null
            ? Results.StatusCode(result.StatusCode)
            : Results.Json(result.Payload, statusCode: result.StatusCode);
    }
}
