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

    private sealed record ShopCredentials(Guid TenantId, string AccessToken, string RefreshToken);
    private sealed record CustomerCredentials(string AccessToken);
}

internal sealed class ApiIntegrationWebApplicationFactory(
    string connectionString,
    string storageRoot) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmartPrint"] = connectionString,
                ["Authentication:SigningKey"] = "integration-test-signing-key-with-more-than-32-bytes",
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
