using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;
using Xunit;

namespace SmartPrint.Api.Tests;

public sealed class ApiWorkflowIntegrationTests
{
    [Fact]
    public async Task Extracted_api_preserves_registration_sessions_print_payment_refund_and_platform_workflows()
    {
        var database = await ApiIntegrationTestDatabase.TryCreateAsync();
        if (database is null)
        {
            // The Windows API build job has no PostgreSQL service. The dedicated
            // postgres-integration job supplies SMARTPRINT_TEST_POSTGRES and runs this journey.
            return;
        }

        await using (database)
        {
            var storageRoot = Path.Combine(
                Path.GetTempPath(),
                "qrtoprint-api-integration",
                database.DatabaseName);
            using var factory = new ApiIntegrationWebApplicationFactory(
                database.ConnectionString,
                storageRoot);
            using var client = factory.CreateClient();

            using (var live = await client.GetAsync("/health/live"))
                Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            using (var ready = await client.GetAsync("/health/ready"))
                Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            using (var anonymousShopRead = await client.GetAsync("/api/v1/shop/profile"))
                Assert.Equal(HttpStatusCode.Unauthorized, anonymousShopRead.StatusCode);

            var shop = await RegisterShopAsync(client, "Primary Integration Shop");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", shop.AccessToken);

            using (var profile = await client.GetAsync("/api/v1/shop/profile"))
            {
                using var json = await ReadExpectedJsonAsync(profile, HttpStatusCode.OK);
                Assert.Equal("Primary Integration Shop", json.RootElement.GetProperty("displayName").GetString());
            }

            using (var subscription = await client.GetAsync("/api/v1/shop/subscription"))
                Assert.Equal(HttpStatusCode.OK, subscription.StatusCode);
            using (var pricing = await client.GetAsync("/api/v1/shop/pricing"))
                Assert.Equal(HttpStatusCode.OK, pricing.StatusCode);
            using (var paymentSettings = await client.GetAsync("/api/v1/shop/payment-settings"))
                Assert.Equal(HttpStatusCode.OK, paymentSettings.StatusCode);

            using (var pricingUpdate = await client.PutAsJsonAsync("/api/v1/shop/pricing", new
            {
                monoPerSideMinor = 100,
                colourPerSideMinor = 200,
                monoDuplexPerSheetMinor = 50,
                colourDuplexPerSheetMinor = 100,
                minimumChargeMinor = 100,
                photo35x45Minor = 0,
                photo25x35Minor = 0,
                photo2x2Minor = 0,
                photo4x6Minor = 0
            }))
                Assert.Equal(HttpStatusCode.OK, pricingUpdate.StatusCode);

            using (var paymentUpdate = await client.PutAsJsonAsync("/api/v1/shop/payment-settings", new
            {
                upiEnabled = true,
                upiId = "integration@bank",
                payeeName = "Primary Integration Shop",
                cashEnabled = true,
                instantPaymentEnabled = false
            }))
                Assert.Equal(HttpStatusCode.OK, paymentUpdate.StatusCode);

            var customer = await RegisterCustomerAsync(client, "Integration Customer");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", customer.AccessToken);
            using (var customerMe = await client.GetAsync("/api/v1/customer/me"))
            {
                using var json = await ReadExpectedJsonAsync(customerMe, HttpStatusCode.OK);
                Assert.Equal("Integration Customer", json.RootElement.GetProperty("displayName").GetString());
            }

            using (var customerJobs = await client.GetAsync("/api/v1/customer/jobs"))
                Assert.Equal(HttpStatusCode.OK, customerJobs.StatusCode);

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", shop.AccessToken);

            using var printerResponse = await client.PostAsJsonAsync("/api/v1/shop/printers", new
            {
                displayName = "Integration Printer",
                manufacturer = "Test Manufacturer",
                model = "Integration Queue",
                connectionType = "WindowsQueue",
                connectionAddress = (string?)null,
                queueName = "QRTP-INTEGRATION-QUEUE",
                capabilitiesJson = "{}"
            });
            using var printerJson = await ReadExpectedJsonAsync(printerResponse, HttpStatusCode.Created);
            var printerId = printerJson.RootElement.GetProperty("id").GetGuid();

            using var stationResponse = await client.PostAsJsonAsync("/api/v1/shop/qr-stations", new
            {
                displayName = "Integration Counter",
                printerDeviceId = printerId
            });
            using var stationJson = await ReadExpectedJsonAsync(stationResponse, HttpStatusCode.Created);
            var stationUrl = stationJson.RootElement.GetProperty("publicUrl").GetString();
            Assert.False(string.IsNullOrWhiteSpace(stationUrl));
            var publicCode = new Uri(stationUrl!).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();

            // Start the public station session while authenticated as the customer.
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", customer.AccessToken);
            using var sessionResponse = await client.PostAsync(
                $"/api/v1/public/stations/{Uri.EscapeDataString(publicCode)}/sessions",
                content: null);
            using var sessionJson = await ReadExpectedJsonAsync(sessionResponse, HttpStatusCode.Created);
            var sessionId = sessionJson.RootElement.GetProperty("id").GetGuid();
            var sessionToken = sessionJson.RootElement.GetProperty("sessionToken").GetString();
            Assert.False(string.IsNullOrWhiteSpace(sessionToken));

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", shop.AccessToken);
            using (var currentSession = await SendWithSessionAsync(
                client,
                HttpMethod.Get,
                "/api/v1/public/sessions/current",
                sessionToken!))
                Assert.Equal(HttpStatusCode.OK, currentSession.StatusCode);
            using (var renewedSession = await SendWithSessionAsync(
                client,
                HttpMethod.Post,
                "/api/v1/public/sessions/current/renew",
                sessionToken!))
                Assert.Equal(HttpStatusCode.OK, renewedSession.StatusCode);
            using (var resumedSession = await SendWithSessionJsonAsync(
                client,
                HttpMethod.Post,
                "/api/v1/public/sessions/current/resume",
                sessionToken!,
                new { publicCode }))
                Assert.Equal(HttpStatusCode.OK, resumedSession.StatusCode);

            var jobId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SmartPrintDbContext>();
                db.PrintJobs.Add(new PrintJob
                {
                    Id = jobId,
                    TenantId = shop.TenantId,
                    QrStationId = stationJson.RootElement.GetProperty("id").GetGuid(),
                    CustomerSessionId = sessionId,
                    JobNumber = $"SP-IT-{Guid.NewGuid():N}"[..20],
                    IdempotencyKey = $"API-INTEGRATION-{Guid.NewGuid():N}",
                    Status = "AWAITING_CONFIGURATION",
                    PageCount = 2,
                    Copies = 1,
                    ColourMode = "Mono",
                    PaperSize = "A4",
                    IsDuplex = false,
                    Orientation = "AUTO",
                    PrintKind = "DOCUMENT",
                    Currency = "INR",
                    QuotedAmountMinor = 0,
                    CreatedUtc = now,
                    UpdatedUtc = now
                });
                db.PrintDocuments.Add(new PrintDocument
                {
                    TenantId = shop.TenantId,
                    PrintJobId = jobId,
                    OriginalFileName = "integration-document.pdf",
                    MediaType = "application/pdf",
                    FileExtension = ".pdf",
                    SizeBytes = 1024,
                    Sha256 = new string('a', 64),
                    StorageKey = $"integration/{jobId:N}.pdf",
                    StorageMode = "CLOUD",
                    PageSizesJson = "[\"A4\",\"A4\"]",
                    CreatedUtc = now
                });
                await db.SaveChangesAsync();
            }

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", customer.AccessToken);
            using (var customerJobsAfterUpload = await client.GetAsync("/api/v1/customer/jobs"))
            {
                using var json = await ReadExpectedJsonAsync(customerJobsAfterUpload, HttpStatusCode.OK);
                Assert.Contains(
                    json.RootElement.GetProperty("jobs").EnumerateArray(),
                    item => item.GetProperty("id").GetGuid() == jobId);
            }

            using (var quote = await SendWithSessionJsonAsync(
                client,
                HttpMethod.Post,
                $"/api/v1/public/jobs/{jobId}/quote",
                sessionToken!,
                new
                {
                    colourMode = "Mono",
                    paperSize = "A4",
                    isDuplex = false,
                    copies = 1
                }))
            {
                using var json = await ReadExpectedJsonAsync(quote, HttpStatusCode.OK);
                Assert.Equal(200L, json.RootElement.GetProperty("quotedAmountMinor").GetInt64());
                Assert.Equal("AWAITING_CUSTOMER_APPROVAL", json.RootElement.GetProperty("status").GetString());
            }

            using (var accepted = await SendWithSessionAsync(
                client,
                HttpMethod.Post,
                $"/api/v1/public/jobs/{jobId}/accept-quote",
                sessionToken!))
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            using (var duplicateAcceptance = await SendWithSessionAsync(
                client,
                HttpMethod.Post,
                $"/api/v1/public/jobs/{jobId}/accept-quote",
                sessionToken!))
            {
                using var json = await ReadExpectedJsonAsync(duplicateAcceptance, HttpStatusCode.OK);
                Assert.True(json.RootElement.GetProperty("duplicateRequest").GetBoolean());
            }

            using (var approval = await client.PostAsync(
                $"/api/v1/shop/print-jobs/{jobId}/approve",
                content: null))
                Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
            using (var duplicateApproval = await client.PostAsync(
                $"/api/v1/shop/print-jobs/{jobId}/approve",
                content: null))
            {
                using var json = await ReadExpectedJsonAsync(duplicateApproval, HttpStatusCode.OK);
                Assert.True(json.RootElement.GetProperty("duplicateRequest").GetBoolean());
            }

            using var paymentStart = await SendWithSessionJsonAsync(
                client,
                HttpMethod.Post,
                $"/api/v1/public/jobs/{jobId}/payments",
                sessionToken!,
                new { method = "UPI" });
            using var paymentJson = await ReadExpectedJsonAsync(paymentStart, HttpStatusCode.OK);
            var paymentId = paymentJson.RootElement.GetProperty("id").GetGuid();
            Assert.Equal("AWAITING_CUSTOMER_PAYMENT", paymentJson.RootElement.GetProperty("status").GetString());

            using (var duplicatePaymentStart = await SendWithSessionJsonAsync(
                client,
                HttpMethod.Post,
                $"/api/v1/public/jobs/{jobId}/payments",
                sessionToken!,
                new { method = "UPI" }))
            {
                using var json = await ReadExpectedJsonAsync(duplicatePaymentStart, HttpStatusCode.OK);
                Assert.Equal(paymentId, json.RootElement.GetProperty("id").GetGuid());
            }

            using (var report = await SendWithSessionJsonAsync(
                client,
                HttpMethod.Post,
                $"/api/v1/public/payments/{paymentId}/report",
                sessionToken!,
                new { referenceNumber = "IT-UPI-REFERENCE-1234" }))
                Assert.Equal(HttpStatusCode.OK, report.StatusCode);

            using (var verification = await client.PostAsync(
                $"/api/v1/shop/payments/{paymentId}/verify",
                content: null))
                Assert.Equal(HttpStatusCode.OK, verification.StatusCode);
            using (var duplicateVerification = await client.PostAsync(
                $"/api/v1/shop/payments/{paymentId}/verify",
                content: null))
            {
                using var json = await ReadExpectedJsonAsync(duplicateVerification, HttpStatusCode.OK);
                Assert.True(json.RootElement.GetProperty("duplicateRequest").GetBoolean());
            }

            using (var refundPreview = await client.PostAsJsonAsync(
                $"/api/v1/shop/print-jobs/{jobId}/refund-preview",
                new { rejectedPageNumbers = new[] { 1 } }))
            {
                using var json = await ReadExpectedJsonAsync(refundPreview, HttpStatusCode.OK);
                Assert.Equal(100L, json.RootElement.GetProperty("additionalRefundAmountMinor").GetInt64());
                Assert.Equal(100L, json.RootElement.GetProperty("remainingPaidAmountMinor").GetInt64());
            }

            const string refundKey = "integration-refund-0001";
            using var refundRequest = JsonContent.Create(new
            {
                refundMethod = "UPI",
                refundReference = "IT-REFUND-1234",
                reason = "Page was damaged",
                confirmedSent = true,
                idempotencyKey = refundKey,
                rejectedPageNumbers = new[] { 1 }
            });
            using var refundResponse = await client.PostAsync(
                $"/api/v1/shop/print-jobs/{jobId}/refund",
                refundRequest);
            using var refundJson = await ReadExpectedJsonAsync(refundResponse, HttpStatusCode.Created);
            var refundId = refundJson.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(100L, refundJson.RootElement.GetProperty("amountMinor").GetInt64());

            using (var duplicateRefund = await client.PostAsJsonAsync(
                $"/api/v1/shop/print-jobs/{jobId}/refund",
                new
                {
                    refundMethod = "UPI",
                    refundReference = "IT-REFUND-1234",
                    reason = "Page was damaged",
                    confirmedSent = true,
                    idempotencyKey = refundKey,
                    rejectedPageNumbers = new[] { 1 }
                }))
            {
                using var json = await ReadExpectedJsonAsync(duplicateRefund, HttpStatusCode.OK);
                Assert.Equal(refundId, json.RootElement.GetProperty("id").GetGuid());
                Assert.True(json.RootElement.GetProperty("duplicateRequest").GetBoolean());
            }

            await using (var verificationScope = factory.Services.CreateAsyncScope())
            {
                var db = verificationScope.ServiceProvider.GetRequiredService<SmartPrintDbContext>();
                var persistedJob = await db.PrintJobs.SingleAsync(item => item.Id == jobId);
                var persistedPayment = await db.PrintPayments.SingleAsync(item => item.Id == paymentId);
                Assert.Equal("PAID_AUTHORISED", persistedJob.Status);
                Assert.Equal("[1]", persistedJob.RejectedPageNumbersJson);
                Assert.Equal("PARTIALLY_REFUNDED", persistedPayment.Status);
                Assert.Equal(100L, persistedPayment.RefundedAmountMinor);
                Assert.Equal(1, await db.PrintRefunds.CountAsync(item => item.PrintJobId == jobId));
            }

            // Exercise the platform routes with a real platform-admin identity.
            var platformToken = await CreatePlatformAdminTokenAsync(factory);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", platformToken);

            using (var overview = await client.GetAsync("/api/v1/platform/overview"))
                Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
            using (var tenantList = await client.GetAsync("/api/v1/platform/tenants?search=Primary%20Integration"))
                Assert.Equal(HttpStatusCode.OK, tenantList.StatusCode);
            using (var subscriptionList = await client.GetAsync(
                "/api/v1/platform/subscriptions?search=Primary%20Integration&page=1&pageSize=10"))
                Assert.Equal(HttpStatusCode.OK, subscriptionList.StatusCode);
            using (var planList = await client.GetAsync("/api/v1/platform/plans"))
                Assert.Equal(HttpStatusCode.OK, planList.StatusCode);

            using var planResponse = await client.PostAsJsonAsync("/api/v1/platform/plans", new
            {
                code = "INTEGRATION_FREE",
                name = "Integration Free",
                description = "Platform integration test plan",
                version = new
                {
                    currency = "INR",
                    monthlyPriceMinor = 0,
                    annualPriceMinor = 0,
                    maxPrinters = 5,
                    maxAgentDevices = 2,
                    maxQrStations = 5,
                    maxMonthlyPrintJobs = 3000,
                    maxDocumentBytes = 20 * 1024 * 1024,
                    cloudAssistedPrintingEnabled = false,
                    whiteLabelEnabled = false,
                    advancedReportsEnabled = false
                }
            });
            using var createdPlan = await ReadExpectedJsonAsync(planResponse, HttpStatusCode.Created);
            var planId = createdPlan.RootElement.GetProperty("id").GetGuid();
            var planVersionId = createdPlan.RootElement.GetProperty("version").GetProperty("id").GetGuid();

            using (var publish = await client.PostAsync(
                $"/api/v1/platform/plans/{planId}/versions/1/publish",
                content: null))
                Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

            var assignmentRequest = new
            {
                planVersionId,
                billingInterval = "Monthly",
                reason = "Integration verification assignment",
                idempotencyKey = "integration-plan-assignment-0001",
                paymentConfirmed = false,
                paymentReference = (string?)null
            };
            using (var assignment = await client.PostAsJsonAsync(
                $"/api/v1/platform/tenants/{shop.TenantId}/subscriptions",
                assignmentRequest))
                Assert.Equal(HttpStatusCode.Created, assignment.StatusCode);
            using (var duplicateAssignment = await client.PostAsJsonAsync(
                $"/api/v1/platform/tenants/{shop.TenantId}/subscriptions",
                assignmentRequest))
            {
                using var json = await ReadExpectedJsonAsync(duplicateAssignment, HttpStatusCode.OK);
                Assert.True(json.RootElement.GetProperty("duplicateRequest").GetBoolean());
            }

            using (var operationalSettings = await client.GetAsync("/api/v1/platform/settings/operations"))
                Assert.Equal(HttpStatusCode.OK, operationalSettings.StatusCode);
            using (var updateOperationalSettings = await client.PutAsJsonAsync(
                "/api/v1/platform/settings/operations",
                new
                {
                    trialMaxMonthlyPrintJobs = 3001,
                    cloudCompletedDocumentRetentionDays = 30,
                    agentCompletedDocumentRetentionDays = 30,
                    abandonedDocumentRetentionDays = 30,
                    pendingLocalUploadRetentionDays = 30,
                    expiredUploadTicketRetentionDays = 7,
                    emptyCustomerSessionRetentionDays = 90,
                    staleRefreshTokenRetentionDays = 30,
                    apiRetentionSweepHours = 24,
                    agentRetentionSweepHours = 6,
                    allowPaidJobClaimsWhenSuspended = true,
                    reason = "Integration test audit"
                }))
                Assert.Equal(HttpStatusCode.OK, updateOperationalSettings.StatusCode);
            using (var operationalHistory = await client.GetAsync("/api/v1/platform/settings/operations/history"))
            {
                using var json = await ReadExpectedJsonAsync(operationalHistory, HttpStatusCode.OK);
                Assert.Contains(
                    json.RootElement.EnumerateArray(),
                    item => item.GetProperty("reason").GetString() == "Integration test audit");
            }
            using (var subscriptionHistory = await client.GetAsync(
                $"/api/v1/platform/tenants/{shop.TenantId}/subscription-history"))
                Assert.Equal(HttpStatusCode.OK, subscriptionHistory.StatusCode);

            // A second, job-free tenant can be suspended. Read-only recovery remains
            // accessible while new shop mutations are rejected by the tenant filter.
            var suspendedShop = await RegisterShopAsync(client, "Suspended Integration Shop");
            using (var suspend = await client.PatchAsJsonAsync(
                $"/api/v1/platform/tenants/{suspendedShop.TenantId}/status",
                new
                {
                    status = "Suspended",
                    reason = "Integration tenant suspension verification"
                }))
                Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", suspendedShop.AccessToken);
            using (var recoveryRead = await client.GetAsync("/api/v1/shop/profile"))
                Assert.Equal(HttpStatusCode.OK, recoveryRead.StatusCode);
            using (var blockedMutation = await client.PutAsJsonAsync("/api/v1/shop/pricing", new
            {
                monoPerSideMinor = 100,
                colourPerSideMinor = 200,
                monoDuplexPerSheetMinor = 50,
                colourDuplexPerSheetMinor = 100,
                minimumChargeMinor = 100
            }))
                Assert.Equal(HttpStatusCode.Forbidden, blockedMutation.StatusCode);

            // Refresh rotation is single-use; logout revokes the replacement token.
            client.DefaultRequestHeaders.Authorization = null;
            using var refreshResponse = await client.PostAsJsonAsync(
                "/api/v1/auth/refresh",
                new { refreshToken = shop.RefreshToken });
            using var refreshed = await ReadExpectedJsonAsync(refreshResponse, HttpStatusCode.OK);
            var replacementRefreshToken = refreshed.RootElement.GetProperty("refreshToken").GetString();
            Assert.False(string.IsNullOrWhiteSpace(replacementRefreshToken));

            using (var replay = await client.PostAsJsonAsync(
                "/api/v1/auth/refresh",
                new { refreshToken = shop.RefreshToken }))
                Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
            using (var logout = await client.PostAsJsonAsync(
                "/api/v1/auth/logout",
                new { refreshToken = replacementRefreshToken }))
                Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            using (var revokedReplay = await client.PostAsJsonAsync(
                "/api/v1/auth/refresh",
                new { refreshToken = replacementRefreshToken }))
                Assert.Equal(HttpStatusCode.Unauthorized, revokedReplay.StatusCode);
        }
    }

    [Fact]
    public async Task Batch_review_and_checkout_exclude_rejected_files_from_payment_and_authorisation()
    {
        var database = await ApiIntegrationTestDatabase.TryCreateAsync();
        if (database is null)
            return;

        await using (database)
        {
            var storageRoot = Path.Combine(
                Path.GetTempPath(),
                "qrtoprint-api-batch-integration",
                database.DatabaseName);
            using var factory = new ApiIntegrationWebApplicationFactory(
                database.ConnectionString,
                storageRoot);
            using var client = factory.CreateClient();

            var shop = await RegisterShopAsync(client, "Batch Integration Shop");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", shop.AccessToken);

            using (var pricing = await client.PutAsJsonAsync("/api/v1/shop/pricing", new
            {
                monoPerSideMinor = 100,
                colourPerSideMinor = 200,
                monoDuplexPerSheetMinor = 50,
                colourDuplexPerSheetMinor = 100,
                minimumChargeMinor = 100
            }))
                Assert.Equal(HttpStatusCode.OK, pricing.StatusCode);

            using (var paymentSettings = await client.PutAsJsonAsync("/api/v1/shop/payment-settings", new
            {
                upiEnabled = true,
                upiId = "batch-integration@bank",
                payeeName = "Batch Integration Shop",
                cashEnabled = true,
                instantPaymentEnabled = false
            }))
                Assert.Equal(HttpStatusCode.OK, paymentSettings.StatusCode);

            using var printerResponse = await client.PostAsJsonAsync("/api/v1/shop/printers", new
            {
                displayName = "Batch Integration Printer",
                manufacturer = "Test Manufacturer",
                model = "Batch Queue",
                connectionType = "WindowsQueue",
                connectionAddress = (string?)null,
                queueName = "QRTP-BATCH-INTEGRATION",
                capabilitiesJson = "{}"
            });
            using var printerJson = await ReadExpectedJsonAsync(printerResponse, HttpStatusCode.Created);
            var printerId = printerJson.RootElement.GetProperty("id").GetGuid();

            using var stationResponse = await client.PostAsJsonAsync("/api/v1/shop/qr-stations", new
            {
                displayName = "Batch Integration Counter",
                printerDeviceId = printerId
            });
            using var stationJson = await ReadExpectedJsonAsync(stationResponse, HttpStatusCode.Created);
            var publicUrl = stationJson.RootElement.GetProperty("publicUrl").GetString();
            Assert.False(string.IsNullOrWhiteSpace(publicUrl));
            var publicCode = new Uri(publicUrl!).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();

            var customer = await RegisterCustomerAsync(client, "Batch Integration Customer");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", customer.AccessToken);
            using var sessionResponse = await client.PostAsync(
                $"/api/v1/public/stations/{Uri.EscapeDataString(publicCode)}/sessions",
                content: null);
            using var sessionJson = await ReadExpectedJsonAsync(sessionResponse, HttpStatusCode.Created);
            var sessionId = sessionJson.RootElement.GetProperty("id").GetGuid();
            var sessionToken = sessionJson.RootElement.GetProperty("sessionToken").GetString();
            Assert.False(string.IsNullOrWhiteSpace(sessionToken));

            var batchId = Guid.NewGuid();
            var acceptedJobId = Guid.NewGuid();
            var rejectedJobId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SmartPrintDbContext>();
                db.PrintJobs.AddRange(
                    new PrintJob
                    {
                        Id = acceptedJobId,
                        TenantId = shop.TenantId,
                        QrStationId = stationJson.RootElement.GetProperty("id").GetGuid(),
                        CustomerSessionId = sessionId,
                        BatchId = batchId,
                        BatchName = "Customer upload batch",
                        JobNumber = $"SP-BATCH-A-{Guid.NewGuid():N}"[..22],
                        IdempotencyKey = $"API-BATCH-A-{Guid.NewGuid():N}",
                        Status = "AWAITING_SHOP_APPROVAL",
                        PageCount = 2,
                        Copies = 1,
                        ColourMode = "Mono",
                        PaperSize = "A4",
                        IsDuplex = false,
                        Orientation = "AUTO",
                        PrintKind = "DOCUMENT",
                        QuoteExpiresUtc = now.AddMinutes(10),
                        Currency = "INR",
                        QuotedAmountMinor = 200,
                        CreatedUtc = now,
                        UpdatedUtc = now
                    },
                    new PrintJob
                    {
                        Id = rejectedJobId,
                        TenantId = shop.TenantId,
                        QrStationId = stationJson.RootElement.GetProperty("id").GetGuid(),
                        CustomerSessionId = sessionId,
                        BatchId = batchId,
                        BatchName = "Customer upload batch",
                        JobNumber = $"SP-BATCH-R-{Guid.NewGuid():N}"[..22],
                        IdempotencyKey = $"API-BATCH-R-{Guid.NewGuid():N}",
                        Status = "AWAITING_SHOP_APPROVAL",
                        PageCount = 2,
                        Copies = 1,
                        ColourMode = "Mono",
                        PaperSize = "A4",
                        IsDuplex = false,
                        Orientation = "AUTO",
                        PrintKind = "DOCUMENT",
                        QuoteExpiresUtc = now.AddMinutes(10),
                        Currency = "INR",
                        QuotedAmountMinor = 200,
                        CreatedUtc = now.AddMilliseconds(1),
                        UpdatedUtc = now
                    });
                await db.SaveChangesAsync();
            }

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", shop.AccessToken);
            using (var review = await client.PostAsJsonAsync(
                $"/api/v1/shop/print-batches/{batchId}/review",
                new
                {
                    reviewedJobIds = new[] { acceptedJobId, rejectedJobId },
                    acceptedJobIds = new[] { acceptedJobId },
                    pdfBrandingEnabled = false
                }))
            {
                using var json = await ReadExpectedJsonAsync(review, HttpStatusCode.OK);
                Assert.Equal(2, json.RootElement.GetProperty("reviewedCount").GetInt32());
                Assert.Equal(1, json.RootElement.GetProperty("acceptedCount").GetInt32());
                Assert.Equal(1, json.RootElement.GetProperty("rejectedCount").GetInt32());
            }

            // Checkout is public and scoped by the customer session, not by the shop bearer token.
            client.DefaultRequestHeaders.Authorization = null;
            using var checkout = await SendWithSessionJsonAsync(
                client,
                HttpMethod.Post,
                $"/api/v1/public/batches/{batchId}/payments",
                sessionToken!,
                new { method = "UPI" });
            using var checkoutJson = await ReadExpectedJsonAsync(checkout, HttpStatusCode.OK);
            var batchPaymentId = checkoutJson.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(200L, checkoutJson.RootElement.GetProperty("amountMinor").GetInt64());
            Assert.Equal(1, checkoutJson.RootElement.GetProperty("fileCount").GetInt32());

            using (var duplicateCheckout = await SendWithSessionJsonAsync(
                client,
                HttpMethod.Post,
                $"/api/v1/public/batches/{batchId}/payments",
                sessionToken!,
                new { method = "UPI" }))
            {
                using var json = await ReadExpectedJsonAsync(duplicateCheckout, HttpStatusCode.OK);
                Assert.Equal(batchPaymentId, json.RootElement.GetProperty("id").GetGuid());
                Assert.Equal(1, json.RootElement.GetProperty("fileCount").GetInt32());
            }

            using (var report = await SendWithSessionJsonAsync(
                client,
                HttpMethod.Post,
                $"/api/v1/public/batch-payments/{batchPaymentId}/report",
                sessionToken!,
                new { referenceNumber = "IT-BATCH-REFERENCE-1234" }))
                Assert.Equal(HttpStatusCode.OK, report.StatusCode);

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", shop.AccessToken);
            using (var verification = await client.PostAsync(
                $"/api/v1/shop/payments/{batchPaymentId}/verify",
                content: null))
                Assert.Equal(HttpStatusCode.OK, verification.StatusCode);

            // The accepted file is authorised by the single batch payment. The rejected
            // file remains excluded and can never be authorised by that payment.
            await using (var verificationScope = factory.Services.CreateAsyncScope())
            {
                var db = verificationScope.ServiceProvider.GetRequiredService<SmartPrintDbContext>();
                var batchPayment = await db.PrintBatchPayments.SingleAsync(item => item.Id == batchPaymentId);
                var acceptedJob = await db.PrintJobs.SingleAsync(item => item.Id == acceptedJobId);
                var rejectedJob = await db.PrintJobs.SingleAsync(item => item.Id == rejectedJobId);
                var filePayments = await db.PrintPayments
                    .Where(item => item.BatchPaymentId == batchPaymentId)
                    .ToListAsync();

                Assert.Equal("VERIFIED", batchPayment.Status);
                Assert.Equal("PAID_AUTHORISED", acceptedJob.Status);
                Assert.Equal("REJECTED", rejectedJob.Status);
                Assert.Single(filePayments);
                Assert.Equal(acceptedJobId, filePayments[0].PrintJobId);
                Assert.Equal(200L, filePayments[0].AmountMinor);
            }

            using (var postCheckoutReview = await client.PostAsJsonAsync(
                $"/api/v1/shop/print-batches/{batchId}/review",
                new
                {
                    reviewedJobIds = new[] { rejectedJobId },
                    acceptedJobIds = new[] { rejectedJobId },
                    pdfBrandingEnabled = false
                }))
            {
                using var json = await ReadExpectedJsonAsync(postCheckoutReview, HttpStatusCode.Conflict);
                Assert.Equal("batch_payment_already_started", json.RootElement.GetProperty("code").GetString());
            }
        }
    }

    [Fact]
    public async Task Agent_enrollment_heartbeat_printer_sync_and_paid_job_queue_remain_operational()
    {
        var database = await ApiIntegrationTestDatabase.TryCreateAsync();
        if (database is null)
            return;

        await using (database)
        {
            var storageRoot = Path.Combine(
                Path.GetTempPath(),
                "qrtoprint-api-agent-integration",
                database.DatabaseName);
            using var factory = new ApiIntegrationWebApplicationFactory(
                database.ConnectionString,
                storageRoot);
            using var client = factory.CreateClient();

            var shop = await RegisterShopAsync(client, "Agent Integration Shop");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", shop.AccessToken);

            using var enrollmentCodeResponse = await client.PostAsync(
                "/api/v1/shop/agent-enrollment-codes",
                content: null);
            using var enrollmentCodeJson = await ReadExpectedJsonAsync(
                enrollmentCodeResponse, HttpStatusCode.Created);
            var enrollmentCode = enrollmentCodeJson.RootElement.GetProperty("code").GetString();
            Assert.False(string.IsNullOrWhiteSpace(enrollmentCode));

            const string fingerprint = "integration-agent-computer-fingerprint-001";
            using var enrollmentResponse = await client.PostAsJsonAsync("/api/v1/agent/enroll", new
            {
                code = enrollmentCode,
                fingerprint,
                deviceName = "Integration Windows Computer"
            });
            using var enrollmentJson = await ReadExpectedJsonAsync(
                enrollmentResponse, HttpStatusCode.Created);
            var agentId = enrollmentJson.RootElement.GetProperty("agentDeviceId").GetGuid();
            var agentSecret = enrollmentJson.RootElement.GetProperty("agentSecret").GetString();
            Assert.False(string.IsNullOrWhiteSpace(agentSecret));

            using (var heartbeat = await SendWithAgentJsonAsync(
                client,
                "/api/v1/agent/heartbeat",
                agentId,
                agentSecret!,
                new
                {
                    version = "integration-test",
                    operatingSystem = "Windows",
                    localEndpoints = Array.Empty<string>()
                }))
            {
                using var json = await ReadExpectedJsonAsync(heartbeat, HttpStatusCode.OK);
                Assert.Equal(agentId, json.RootElement.GetProperty("id").GetGuid());
                Assert.Equal("Online", json.RootElement.GetProperty("status").GetString());
            }

            using (var printerSync = await SendWithAgentJsonAsync(
                client,
                "/api/v1/agent/printers/sync",
                agentId,
                agentSecret!,
                new
                {
                    printers = new[]
                    {
                        new
                        {
                            queueName = "QRTP-AGENT-INTEGRATION-QUEUE",
                            driverName = "Integration Test Driver",
                            portName = "INTEGRATION-PORT",
                            status = "Ready",
                            isDefault = true,
                            outputMode = "PHYSICAL",
                            capabilitiesJson = "{}"
                        }
                    }
                }))
            {
                using var json = await ReadExpectedJsonAsync(printerSync, HttpStatusCode.OK);
                Assert.Equal(1, json.RootElement.GetProperty("created").GetInt32());
                Assert.Equal(1, json.RootElement.GetProperty("discovered").GetInt32());
            }

            Guid printerId;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SmartPrintDbContext>();
                printerId = await db.PrinterDevices
                    .Where(item => item.TenantId == shop.TenantId && item.AgentDeviceId == agentId)
                    .Select(item => item.Id)
                    .SingleAsync();
            }

            using var stationResponse = await client.PostAsJsonAsync("/api/v1/shop/qr-stations", new
            {
                displayName = "Agent Integration Counter",
                printerDeviceId = printerId
            });
            using var stationJson = await ReadExpectedJsonAsync(stationResponse, HttpStatusCode.Created);
            var publicUrl = stationJson.RootElement.GetProperty("publicUrl").GetString();
            Assert.False(string.IsNullOrWhiteSpace(publicUrl));
            var publicCode = new Uri(publicUrl!).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();

            var customer = await RegisterCustomerAsync(client, "Agent Queue Customer");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", customer.AccessToken);
            using var sessionResponse = await client.PostAsync(
                $"/api/v1/public/stations/{Uri.EscapeDataString(publicCode)}/sessions",
                content: null);
            using var sessionJson = await ReadExpectedJsonAsync(sessionResponse, HttpStatusCode.Created);
            var sessionId = sessionJson.RootElement.GetProperty("id").GetGuid();

            var jobId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            const string documentSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SmartPrintDbContext>();
                db.PrintJobs.Add(new PrintJob
                {
                    Id = jobId,
                    TenantId = shop.TenantId,
                    QrStationId = stationJson.RootElement.GetProperty("id").GetGuid(),
                    PrinterDeviceId = printerId,
                    CustomerSessionId = sessionId,
                    JobNumber = $"SP-AGENT-IT-{Guid.NewGuid():N}"[..24],
                    IdempotencyKey = $"AGENT-INTEGRATION-{Guid.NewGuid():N}",
                    Status = "PAID_AUTHORISED",
                    PageCount = 1,
                    Copies = 1,
                    ColourMode = "Mono",
                    PaperSize = "A4",
                    IsDuplex = false,
                    Orientation = "AUTO",
                    Currency = "INR",
                    QuotedAmountMinor = 100,
                    CreatedUtc = now,
                    UpdatedUtc = now
                });
                db.PrintDocuments.Add(new PrintDocument
                {
                    TenantId = shop.TenantId,
                    PrintJobId = jobId,
                    OriginalFileName = "agent-integration.pdf",
                    MediaType = "application/pdf",
                    FileExtension = ".pdf",
                    SizeBytes = 1024,
                    Sha256 = documentSha,
                    StorageKey = $"agent-integration/{jobId:N}.pdf",
                    StorageMode = "CLOUD",
                    PageSizesJson = "[\"A4\"]",
                    CreatedUtc = now
                });
                db.PrintPayments.Add(new PrintPayment
                {
                    TenantId = shop.TenantId,
                    PrintJobId = jobId,
                    Method = "UPI",
                    Status = "VERIFIED",
                    AmountMinor = 100,
                    Currency = "INR",
                    PaymentReference = "AGENT-INTEGRATION-PAID",
                    VerifiedUtc = now,
                    CreatedUtc = now,
                    UpdatedUtc = now
                });
                await db.SaveChangesAsync();
            }

            using var claimResponse = await SendWithAgentAsync(
                client,
                "/api/v1/agent/jobs/claim",
                agentId,
                agentSecret!);
            using var claimJson = await ReadExpectedJsonAsync(claimResponse, HttpStatusCode.OK);
            var claimed = claimJson.RootElement.GetProperty("jobs").EnumerateArray().ToArray();
            var claimedJob = Assert.Single(claimed);
            Assert.Equal(jobId, claimedJob.GetProperty("id").GetGuid());
            Assert.Equal("LOCAL_QUEUE_CLAIMED", claimedJob.GetProperty("status").GetString());

            using (var received = await SendWithAgentJsonAsync(
                client,
                $"/api/v1/agent/jobs/{jobId}/received",
                agentId,
                agentSecret!,
                new { sha256 = documentSha, sizeBytes = 1024 }))
            {
                using var json = await ReadExpectedJsonAsync(received, HttpStatusCode.OK);
                Assert.Equal("LOCAL_QUEUE_READY", json.RootElement.GetProperty("status").GetString());
                Assert.False(json.RootElement.GetProperty("duplicateRequest").GetBoolean());
            }

            using (var duplicateReceived = await SendWithAgentJsonAsync(
                client,
                $"/api/v1/agent/jobs/{jobId}/received",
                agentId,
                agentSecret!,
                new { sha256 = documentSha, sizeBytes = 1024 }))
            {
                using var json = await ReadExpectedJsonAsync(duplicateReceived, HttpStatusCode.OK);
                Assert.True(json.RootElement.GetProperty("duplicateRequest").GetBoolean());
            }

            await using (var verificationScope = factory.Services.CreateAsyncScope())
            {
                var db = verificationScope.ServiceProvider.GetRequiredService<SmartPrintDbContext>();
                var job = await db.PrintJobs.SingleAsync(item => item.Id == jobId);
                var agent = await db.AgentDevices.SingleAsync(item => item.Id == agentId);
                var printer = await db.PrinterDevices.SingleAsync(item => item.Id == printerId);
                Assert.Equal("LOCAL_QUEUE_READY", job.Status);
                Assert.Equal(agentId, job.ClaimedByAgentDeviceId);
                Assert.NotNull(job.LocalQueueReceivedUtc);
                Assert.Equal("Online", agent.Status);
                Assert.True(printer.IsActive);
                Assert.Equal("QRTP-AGENT-INTEGRATION-QUEUE", printer.QueueName);
            }

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", shop.AccessToken);
            using (var revoke = await client.PatchAsync(
                $"/api/v1/shop/agent-devices/{agentId}/revoke",
                content: null))
                Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

            using (var revokedHeartbeat = await SendWithAgentJsonAsync(
                client,
                "/api/v1/agent/heartbeat",
                agentId,
                agentSecret!,
                new
                {
                    version = "integration-test",
                    operatingSystem = "Windows",
                    localEndpoints = Array.Empty<string>()
                }))
                Assert.Equal(HttpStatusCode.Unauthorized, revokedHeartbeat.StatusCode);
        }
    }

    private static async Task<ShopCredentials> RegisterShopAsync(HttpClient client, string businessName)
    {
        var email = $"owner-{Guid.NewGuid():N}@integration.example";
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register-shop", new
        {
            businessName,
            ownerName = "Integration Owner",
            email,
            password = "IntegrationOwner!2026"
        });
        using var json = await ReadExpectedJsonAsync(response, HttpStatusCode.Created);
        var tokens = json.RootElement.GetProperty("tokens");
        return new ShopCredentials(
            json.RootElement.GetProperty("tenantId").GetGuid(),
            tokens.GetProperty("accessToken").GetString()!,
            tokens.GetProperty("refreshToken").GetString()!);
    }

    private static async Task<CustomerCredentials> RegisterCustomerAsync(HttpClient client, string displayName)
    {
        var email = $"customer-{Guid.NewGuid():N}@integration.example";
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register-customer", new
        {
            displayName,
            email,
            password = "IntegrationCustomer!2026"
        });
        using var json = await ReadExpectedJsonAsync(response, HttpStatusCode.Created);
        return new CustomerCredentials(json.RootElement.GetProperty("accessToken").GetString()!);
    }

    private static async Task<string> CreatePlatformAdminTokenAsync(
        ApiIntegrationWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            TenantId = null,
            UserName = $"platform-admin-{Guid.NewGuid():N}@integration.example",
            Email = $"platform-admin-{Guid.NewGuid():N}@integration.example",
            DisplayName = "Integration Platform Administrator"
        };
        var created = await userManager.CreateAsync(user, "IntegrationAdmin!2026");
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(item => item.Description)));
        var claim = await userManager.AddClaimAsync(
            user,
            new Claim(PlatformAdminAuthorization.ClaimType, PlatformAdminAuthorization.ClaimValue));
        Assert.True(claim.Succeeded, string.Join("; ", claim.Errors.Select(item => item.Description)));

        var issuer = scope.ServiceProvider.GetRequiredService<TokenIssuer>();
        return (await issuer.CreateAsync(user)).AccessToken;
    }

    private static async Task<JsonDocument> ReadExpectedJsonAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == expectedStatus,
            $"Expected {(int)expectedStatus} {expectedStatus}, got {(int)response.StatusCode} {response.StatusCode}. Body: {body}");
        return JsonDocument.Parse(body);
    }

    private static Task<HttpResponseMessage> SendWithSessionAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string sessionToken)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Print-Session", sessionToken);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendWithSessionJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string sessionToken,
        object body)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-Print-Session", sessionToken);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendWithAgentAsync(
        HttpClient client,
        string path,
        Guid agentId,
        string agentSecret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-Print-Agent-Id", agentId.ToString());
        request.Headers.Add("X-Print-Agent-Secret", agentSecret);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendWithAgentJsonAsync(
        HttpClient client,
        string path,
        Guid agentId,
        string agentSecret,
        object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-Print-Agent-Id", agentId.ToString());
        request.Headers.Add("X-Print-Agent-Secret", agentSecret);
        return client.SendAsync(request);
    }

    private sealed record ShopCredentials(Guid TenantId, string AccessToken, string RefreshToken);
    private sealed record CustomerCredentials(string AccessToken);
}

internal sealed class ApiIntegrationWebApplicationFactory : WebApplicationFactory<Program>
{
    private const string SigningKey = "integration-test-signing-key-with-more-than-32-bytes";
    private readonly string connectionString;
    private readonly string storageRoot;
    private readonly Dictionary<string, string?> previousEnvironment = new(StringComparer.Ordinal);

    public ApiIntegrationWebApplicationFactory(string connectionString, string storageRoot)
    {
        this.connectionString = connectionString;
        this.storageRoot = storageRoot;

        // Minimal hosting evaluates required configuration in Program.cs before
        // WebApplicationFactory's later ConfigureAppConfiguration callback runs.
        // Supply only the test host's process environment before CreateClient starts it.
        SetEnvironment("ConnectionStrings__SmartPrint", connectionString);
        SetEnvironment("Authentication__SigningKey", SigningKey);
        SetEnvironment("Authentication__Issuer", "QRtoPrint.IntegrationTests");
        SetEnvironment("Authentication__Audience", "QRtoPrint.IntegrationTests");
        SetEnvironment("Database__ApplyMigrationsOnStartup", "true");
        SetEnvironment("Documents__EncryptionKey", Convert.ToBase64String(
            Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()));
        SetEnvironment("Documents__StorageRoot", storageRoot);
        SetEnvironment("PublicPortal__BaseUrl", "https://print.integration.example");
    }

    private void SetEnvironment(string key, string? value)
    {
        previousEnvironment[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;

        foreach (var item in previousEnvironment)
            Environment.SetEnvironmentVariable(item.Key, item.Value);
        previousEnvironment.Clear();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartPrint"] = connectionString,
                ["Authentication:SigningKey"] = SigningKey,
                ["Authentication:Issuer"] = "QRtoPrint.IntegrationTests",
                ["Authentication:Audience"] = "QRtoPrint.IntegrationTests",
                ["Database:ApplyMigrationsOnStartup"] = "true",
                ["Documents:EncryptionKey"] = Convert.ToBase64String(
                    Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
                ["Documents:StorageRoot"] = storageRoot,
                ["PublicPortal:BaseUrl"] = "https://print.integration.example",
                ["Cors:AllowedOrigins:0"] = "http://localhost:3000"
            });
        });
    }
}

internal sealed class ApiIntegrationTestDatabase(
    string connectionString,
    string databaseName) : IAsyncDisposable
{
    public string ConnectionString { get; } = connectionString;
    public string DatabaseName { get; } = databaseName;

    public static async Task<ApiIntegrationTestDatabase?> TryCreateAsync()
    {
        var configured = Environment.GetEnvironmentVariable("SMARTPRINT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        var databaseName = $"qrtoprint_api_it_{Guid.NewGuid():N}";
        var target = new NpgsqlConnectionStringBuilder(configured);
        var admin = new NpgsqlConnectionStringBuilder(configured)
        {
            Database = "postgres",
            Pooling = false
        };

        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await command.ExecuteNonQueryAsync();
        }

        target.Database = databaseName;
        target.Pooling = false;
        return new ApiIntegrationTestDatabase(
            target.ConnectionString,
            databaseName);
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        var options = new DbContextOptionsBuilder<SmartPrintDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        await using var db = new SmartPrintDbContext(options);
        await db.Database.EnsureDeletedAsync();
    }
}
