using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.IdentityModel.Tokens;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Endpoints;
using SmartPrint.Api.Pricing;
using SmartPrint.Api.Security;
using SmartPrint.Api.Storage;
using static SmartPrint.Api.Security.EndpointIdentity;
using static SmartPrint.Api.Security.OpaqueTokenHasher;
using static SmartPrint.Api.Domain.ShopProfileRules;
using SmartPrint.Api.Services;
using SmartPrint.Documents;

namespace SmartPrint.Api.Endpoints;

public static partial class PublicJobEndpoints
{
    private static void MapPublicJobStatusEndpoint(RouteGroupBuilder publicApi)
    {
        publicApi.MapGet("/jobs/{jobId:guid}/status", async (
            Guid jobId,
            HttpContext context,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return Results.Unauthorized();
            }

            var tokenHash = HashOpaqueToken(rawToken);
            var now = DateTimeOffset.UtcNow;
            var session = await db.CustomerPrintSessions.AsNoTracking()
                .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                               item.RevokedUtc == null && item.ExpiresUtc > now)
                .Select(item => new { item.Id, item.TenantId })
                .SingleOrDefaultAsync(ct);
            if (session is null)
            {
                return Results.Unauthorized();
            }

            var job = await db.PrintJobs.AsNoTracking()
                .Where(item => item.Id == jobId && item.TenantId == session.TenantId &&
                               item.CustomerSessionId == session.Id)
                .Select(item => new
                {
                    item.Id,
                    item.JobNumber,
                    item.Status,
                    item.PageCount,
                    item.Copies,
                    item.ColourMode,
                    item.PaperSize,
                    item.IsDuplex,
                    item.PrintKind,
                    item.PhotoSizeCode,
                    item.PhotoCount,
                    item.PhotoUnitPriceMinorSnapshot,
                    item.Currency,
                    item.QuotedAmountMinor,
                    item.QuoteExpiresUtc,
                    item.RejectedPageNumbersJson,
                    item.UpdatedUtc
                })
                .SingleOrDefaultAsync(ct);
            if (job is null)
            {
                return Results.NotFound();
            }

            var timeline = await db.PrintJobEvents.AsNoTracking()
                .Where(item => item.TenantId == session.TenantId && item.PrintJobId == jobId)
                .OrderBy(item => item.CreatedUtc)
                .Select(item => new { item.NewStatus, item.ActorType, item.CreatedUtc })
                .ToListAsync(ct);
            var payment = await db.PrintPayments.AsNoTracking()
                .Where(item => item.TenantId == session.TenantId && item.PrintJobId == jobId)
                .OrderByDescending(item => item.CreatedUtc)
                .Select(item => new
                {
                    item.Id,
                    item.BatchPaymentId,
                    item.Method,
                    item.Status,
                    item.AmountMinor,
                    item.RefundedAmountMinor,
                    item.Currency,
                    item.UpiIdSnapshot,
                    item.PayeeNameSnapshot,
                    item.PaymentReference,
                    item.RejectionReason,
                    item.CreatedUtc,
                    item.UpdatedUtc,
                    item.VerifiedUtc
                })
                .FirstOrDefaultAsync(ct);
            PrintBatchPayment? batchPayment = null;
            if (payment?.BatchPaymentId is Guid batchPaymentId)
            {
                batchPayment = await db.PrintBatchPayments.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == batchPaymentId && item.TenantId == session.TenantId, ct);
            }
            var batchFileCount = batchPayment is null
                ? 0
                : await db.PrintPayments.AsNoTracking()
                    .CountAsync(item => item.TenantId == session.TenantId &&
                                        item.BatchPaymentId == batchPayment.Id, ct);
            var paymentStatus = payment is null ? null : new
            {
                payment.Id,
                payment.BatchPaymentId,
                payment.Method,
                payment.Status,
                payment.AmountMinor,
                payment.RefundedAmountMinor,
                payment.Currency,
                payment.UpiIdSnapshot,
                payment.PayeeNameSnapshot,
                payment.PaymentReference,
                payment.RejectionReason,
                payment.CreatedUtc,
                payment.UpdatedUtc,
                payment.VerifiedUtc,
                UpiUri = batchPayment is null
                    ? PublicPaymentResponses.BuildUpiUri(payment.Id, payment.Method, payment.UpiIdSnapshot,
                        payment.PayeeNameSnapshot, payment.AmountMinor, payment.Currency, job.JobNumber)
                    : null,
                BatchPayment = batchPayment is null ? null : new
                {
                    batchPayment.Id,
                    batchPayment.Method,
                    batchPayment.Status,
                    batchPayment.AmountMinor,
                    batchPayment.Currency,
                    batchPayment.BatchId,
                    FileCount = batchFileCount,
                    batchPayment.UpiIdSnapshot,
                    batchPayment.PayeeNameSnapshot,
                    batchPayment.PaymentReference,
                    batchPayment.RejectionReason,
                    batchPayment.CreatedUtc,
                    batchPayment.UpdatedUtc,
                    batchPayment.VerifiedUtc,
                    UpiUri = PublicPaymentResponses.BuildUpiUri(batchPayment.Id, batchPayment.Method, batchPayment.UpiIdSnapshot,
                        batchPayment.PayeeNameSnapshot, batchPayment.AmountMinor, batchPayment.Currency,
                        "Batch " + batchPayment.BatchId.ToString("N")[..8].ToUpperInvariant())
                }
            };
            return Results.Ok(new { Job = job, Timeline = timeline, Payment = paymentStatus });
        });
    }
}
