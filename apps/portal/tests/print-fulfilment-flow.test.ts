import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const read = (path: string) => readFileSync(new URL(path, import.meta.url), "utf8");
const readApi = (name: string) => read(`../../../src/SmartPrint.Api/Endpoints/${name}`);

test("customer upload sends the selected file settings to both storage paths", () => {
  const api = read("../src/api.ts");
  assert.match(api, /form\.append\("colourMode", settings\.colourMode\)/);
  assert.match(api, /form\.append\("paperSize", settings\.paperSize\)/);
  assert.match(api, /form\.append\("isDuplex", String\(settings\.isDuplex\)\)/);
  assert.match(api, /form\.append\("copies", String\(settings\.copies\)\)/);
  assert.match(api, /form\.append\("orientation", settings\.orientation\)/);
  assert.match(api, /orientation: settings\.orientation/);
});

test("owner quote edits are sent through the existing print-job API", () => {
  const app = read("../src/App.tsx");
  const api = read("../src/api.ts");
  assert.match(app, /updateShopPrintJobSettings\(tokens\.accessToken,job\.id,settings\)/);
  assert.match(api, /\/api\/v1\/shop\/print-jobs\/\$\{jobId\}\/settings/);
  assert.match(app, /Revised quote .* sent to the customer for approval/);
  assert.match(app, /setPrintSettingsByJobId\(current=>/);
  assert.match(app, /copies:candidate\.copies/);
  assert.match(app, /paperSize,/);
  assert.match(app, /orientation,/);
});

test("paid jobs are staged while a physical printer is offline and checked at dispatch", () => {
  const claimSource = readApi("AgentJobEndpoints.cs");
  const claimService = read("../../../src/SmartPrint.Api/Services/AgentJobQueueService.cs");
  const dispatchSource = readApi("ShopPrintJobDispatchEndpoints.cs");
  const dispatchService = read("../../../src/SmartPrint.Api/Services/ShopPrintJobDispatchService.cs");
  const claimStart = claimSource.indexOf('agentApi.MapPost("/jobs/claim"');
  const claimEnd = claimSource.indexOf('agentApi.MapGet("/jobs/{jobId:guid}/document"', claimStart);
  const claimWorkflowStart = claimService.indexOf("public async Task<AgentJobClaimResult> ClaimAvailableAsync");
  const claimWorkflowEnd = claimService.indexOf("public async Task<AgentJobDocumentResult> GetDocumentAsync", claimWorkflowStart);
  const dispatchStart = dispatchSource.indexOf('shop.MapPost("/print-jobs/{jobId:guid}/dispatch"');
  const dispatchEnd = dispatchSource.indexOf('shop.MapPost("/print-jobs/{jobId:guid}/retry-pdf-export"', dispatchStart);
  assert.ok(claimStart >= 0 && claimEnd > claimStart);
  assert.ok(claimWorkflowStart >= 0 && claimWorkflowEnd > claimWorkflowStart);
  assert.ok(dispatchStart >= 0 && dispatchEnd > dispatchStart);
  const claimRoute = claimSource.slice(claimStart, claimEnd);
  const claimWorkflow = claimService.slice(claimWorkflowStart, claimWorkflowEnd);
  const dispatchRoute = dispatchSource.slice(dispatchStart, dispatchEnd);
  assert.match(claimRoute, /queue\.ClaimAvailableAsync/);
  assert.match(claimWorkflow, /printer\.IsActive/);
  assert.match(claimWorkflow, /printer\.QueueName != null/);
  assert.doesNotMatch(claimWorkflow, /printer\.Status == "Ready"/);
  assert.doesNotMatch(claimWorkflow, /printer\.LastSeenUtc/);
  assert.match(dispatchRoute, /dispatch\.DispatchAsync/);
  assert.match(dispatchService, /printer\.Status == "Ready"/);
  assert.match(dispatchService, /printer\.LastSeenUtc/);
});
test("plan-limited HTTPS fallback is only offered when the Agent is unavailable", () => {
  const app = read("../src/App.tsx");
  const api = read("../src/api.ts");
  const publicUploadSource = readApi("PublicJobCreationEndpoints.cs");
  const publicUploadService = read("../../../src/SmartPrint.Api/Services/PublicJobCreationService.cs");
  assert.match(app, /secureQueueFallbackEnabled = cloudAssistedPrintingEnabled \|\| !localAgent\?\.endpoints\.length \|\| localFallbackSuggested/);
  const directUploadStart = api.indexOf("export async function uploadCustomerDocument(");
  const directUploadEnd = api.indexOf("type CustomerLocalAgentApi", directUploadStart);
  const directUpload = api.slice(directUploadStart, directUploadEnd);
  assert.doesNotMatch(directUpload, /X-Print-Queue-Fallback/);
  assert.match(publicUploadSource, /jobCreation\.CreateAsync/);
  assert.match(publicUploadService, /!subscription\.CloudAssistedPrintingEnabled && localAgentAvailable/);
  assert.doesNotMatch(publicUploadService, /queueFallbackRequested/);
  assert.doesNotMatch(publicUploadSource, /X-Print-Queue-Fallback|queueFallbackRequested/);
  assert.doesNotMatch(api, /X-Print-Queue-Fallback|allowQueueFallback/);
  assert.match(app, /setCheckStatus\("unavailable"\);\s*onReady\(false\);[\s\S]*?onUnavailable\(\);/);
  assert.match(app, /const agentHeartbeatOffline=readiness\?\.heartbeatFresh!==true;[\s\S]*?if\(cloudAssistedPrintingEnabled\|\|agentHeartbeatOffline\)onUnavailable\(\)/);
});

test("pre-submission printer capability blocks are explained in the shop queue", () => {
  const app = read("../src/App.tsx");
  assert.match(app, /function printBlockMessage\(code:string\):string/);
  assert.match(app, /case "PAPER_SIZE_UNSUPPORTED"/);
  assert.match(app, /case "COLOUR_UNSUPPORTED"/);
  assert.match(app, /case "DUPLEX_UNSUPPORTED"/);
  assert.match(app, /job\.status==="LOCAL_QUEUE_READY"&&job\.failureCode/);
  assert.match(app, /Reassign this batch to a compatible printer/);
  assert.match(app, /explicitly approved before output/);
});

test("a pre-output capability block clears the attempted spooler name so the existing job can be reassigned", () => {
  const blockedSource = readApi("AgentSpoolerLifecycleEndpoints.cs");
  const blockedService = read("../../../src/SmartPrint.Api/Services/AgentSpoolerLifecycleService.cs");
  const assignmentSource = readApi("ShopPrintJobAssignmentEndpoints.cs");
  const assignmentService = read("../../../src/SmartPrint.Api/Services/ShopPrintJobAssignmentService.cs");
  const blockedStart = blockedSource.indexOf('agentApi.MapPost("/jobs/{jobId:guid}/print-blocked"');
  const blockedEnd = blockedSource.indexOf('agentApi.MapPost("/jobs/{jobId:guid}/output-unconfirmed"', blockedStart);
  const serviceStart = blockedService.indexOf("public async Task<AgentSpoolerLifecycleResult> ReportPrintBlockedAsync");
  const serviceEnd = blockedService.indexOf("public async Task<AgentSpoolerLifecycleResult> ReportOutputUnconfirmedAsync", serviceStart);
  const assignmentStart = assignmentSource.indexOf('shop.MapPatch("/print-jobs/{jobId:guid}/printer"');
  const assignmentWorkflowStart = assignmentService.indexOf("public async Task<ShopPrintJobAssignmentResult> AssignAsync");
  assert.ok(blockedStart >= 0 && blockedEnd > blockedStart);
  assert.ok(serviceStart >= 0 && serviceEnd > serviceStart);
  assert.ok(assignmentStart >= 0);
  assert.ok(assignmentWorkflowStart >= 0);
  const blocked = blockedSource.slice(blockedStart, blockedEnd);
  const blockedWorkflow = blockedService.slice(serviceStart, serviceEnd);
  const assignment = assignmentService.slice(assignmentWorkflowStart);
  assert.match(blocked, /spooler\.ReportPrintBlockedAsync/);
  assert.match(blockedWorkflow, /job\.Status = "LOCAL_QUEUE_READY"/);
  assert.match(blockedWorkflow, /job\.SpoolerJobName = null/);
  assert.match(blockedWorkflow, /job\.SpoolerJobId = null/);
  assert.match(blockedWorkflow, /job\.SpoolerStatus = "BLOCKED_BEFORE_SUBMISSION"/);
  assert.match(assignmentSource, /assignment\.AssignAsync/);
  assert.match(assignmentSource, /Id = result\.JobId/);
  assert.match(assignment, /var readyQueueCanBeReassigned = selectedJob\.Status == "LOCAL_QUEUE_READY"/);
  assert.match(assignment, /string\.IsNullOrWhiteSpace\(selectedJob\.SpoolerJobName\)/);
  assert.match(assignment, /needsQueueReset/);
});

test("reassigning a blocked queued job clears the previous printer failure", () => {
  const assignment = read("../../../src/SmartPrint.Api/Services/ShopPrintJobAssignmentService.cs");
  const resetStart = assignment.indexOf("if (needsQueueReset)");
  const failureReset = assignment.indexOf("batchJob.FailureCode = null;", resetStart);
  const spoolerReset = assignment.indexOf("batchJob.SpoolerStatus = null;", resetStart);
  assert.ok(resetStart >= 0 && failureReset > resetStart && spoolerReset > failureReset);
});

test("cloud upload retries must match the original file, batch and selected print settings", () => {
  const source = readApi("PublicJobCreationEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/PublicJobCreationService.cs");
  assert.match(source, /jobCreation\.CreateAsync/);
  assert.match(service, /var existing = await db\.PrintJobs\.AsNoTracking\(\)/);
  assert.match(service, /var batchCheckoutLocked = await db\.PrintBatchPayments/);
  assert.match(service, /SHA256\.HashDataAsync\(existingInput, cancellationToken\)/);
  for (const setting of ["Copies", "ColourMode", "PaperSize", "IsDuplex", "Orientation"])
    assert.ok(service.includes("existing."+setting+" != command."+setting), `Retry must compare ${setting}.`);
  assert.match(service, /existingDocument\.Sha256, suppliedSha/);
  assert.match(service, /existingDocument\.OriginalFileName != command\.OriginalName/);
  assert.match(service, /existingDocument\.SizeBytes != command\.File\.Length/);
  assert.match(service, /racedJob = await db\.PrintJobs/);
});

test("HTTPS recovery returns the original saved print settings on new and duplicate responses", () => {
  const source = readApi("PublicCloudRecoveryEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/PublicCloudRecoveryService.cs");
  const recoveryStart = source.indexOf('publicApi.MapPost("/sessions/current/local-upload-tickets/{ticketId:guid}/cloud-recovery"');
  const responseStart = source.indexOf("private static object BuildCloudRecoveryResponse");
  assert.ok(recoveryStart >= 0 && responseStart > recoveryStart);
  const recovery = source.slice(recoveryStart);
  const response = source.slice(responseStart);
  assert.match(recovery, /BuildCloudRecoveryResponse\(\s*preflight\.Job!/);
  assert.match(recovery, /BuildCloudRecoveryResponse\(\s*result\.Job!/);
  for (const property of ["ColourMode", "PaperSize", "IsDuplex", "Orientation"]) {
    assert.ok(response.includes(`job.${property}`), `Recovery response must return job.${property}.`);
    assert.ok(service.includes(`${property} = ticket.${property}`), `Recovery service must preserve ticket ${property}.`);
  }
  // PricingVersion belongs to the PrintJob quote snapshot; LocalPrintUploadTicket does not store it.
  assert.ok(response.includes("job.PricingVersion"), "Recovery response must return the job's pricing version.");
  assert.match(service, /Job: existingJob/);
  assert.match(service, /Document: existingDocument/);
});

test("local Agent completion validates and finalizes the claimed ticket through its service", () => {
  const source = readApi("AgentLocalUploadEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/AgentLocalUploadCompletionService.cs");
  const completeStart = source.indexOf('agentApi.MapPost("/local-uploads/{ticketId:guid}/complete"');
  const serviceStart = service.indexOf("public async Task<AgentLocalUploadCompletionResult> CompleteAsync");
  const transactionStart = service.indexOf("private async Task<AgentLocalUploadCompletionResult> CommitCompletionAsync");
  const jobStart = service.indexOf("var job = new PrintJob", transactionStart);
  const documentStart = service.indexOf("var printDocument = new PrintDocument", jobStart);
  assert.ok(completeStart >= 0 && serviceStart >= 0 && transactionStart > serviceStart);
  assert.ok(jobStart > transactionStart && documentStart > jobStart);
  assert.ok(source.includes("completionService.CompleteAsync("));
  assert.match(service, /LocalUploadGrantValidator\.IsValid/);
  assert.match(service, /LocalUploadCompletionRules\.MatchesTicket/);
  assert.match(service, /LocalUploadCompletionRules\.HasValidPageAnalysis/);
  assert.match(service, /ticket\.ClaimLeaseExpiresUtc <= now/);
  assert.match(service, /GetForTenantAsync/);
  assert.match(service, /PlanEntitlementRules\.AllowsDocument/);
  assert.match(service, /BeginTransactionAsync\(cancellationToken\)/);
  assert.match(service, /ExecuteUpdateAsync/);
  assert.match(service, /AcquireQuotaLockAsync/);
  assert.match(service, /CheckCapacityAsync/);
  const localJob = service.slice(jobStart, documentStart);
  assert.match(localJob, /Copies = ticket\.Copies/);
  assert.match(localJob, /ColourMode = ticket\.ColourMode/);
  assert.match(localJob, /PaperSize = ticket\.PaperSize/);
  assert.match(localJob, /IsDuplex = ticket\.IsDuplex/);
  assert.match(localJob, /Orientation = ticket\.Orientation/);
});

test("local upload ticket retries preserve batch identity and renew the same expired reservation", () => {
  const source = readApi("PublicUploadTicketEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/PublicLocalUploadTicketService.cs");
  const api = read("../src/api.ts");
  const app = read("../src/App.tsx");
  const entitlements = read("../../../src/SmartPrint.Api/Services/TenantEntitlementService.cs");
  const ticketStart = source.indexOf('publicApi.MapPost("/sessions/current/local-upload-tickets"');
  assert.ok(ticketStart >= 0);
  const ticketRoute = source.slice(ticketStart);
  assert.match(ticketRoute, /tickets\.CreateAsync/);
  assert.match(ticketRoute, /RequireRateLimiting\("document-upload"\)/);
  assert.match(service, /request\.BatchId is not Guid requestedBatchId \|\| existing\.BatchId == requestedBatchId/);
  assert.match(service, /requestedBatchName is null \|\| existing\.BatchName == requestedBatchName/);
  assert.match(service, /request\.BatchId is null \|\| racedTicket\.BatchId == request\.BatchId\.Value/);
  assert.match(service, /requestedBatchName is null \|\| racedTicket\.BatchName == requestedBatchName/);
  assert.match(service, /LocalUploadRecoveryRules\.CanRenewExpiredTicket/);
  assert.match(service, /existing = lockedTicket/);
  assert.match(service, /lockedTicket\.GrantHash == originalTicket\.GrantHash/);
  assert.match(service, /lockedTicket\.BatchId == originalTicket\.BatchId/);
  assert.match(service, /lockedTicket\.BatchGroupName == originalTicket\.BatchGroupName/);
  assert.match(service, /FOR UPDATE/);
  assert.match(service, /lockedTicket\.ExpiresUtc = renewalNow\.AddMinutes\(15\)/);
  assert.match(service, /station\.PrinterDeviceId == lockedTicket\.PrinterDeviceId/);
  assert.match(service, /printer\.AgentDeviceId == lockedTicket\.AgentDeviceId/);
  assert.match(service, /agent\.LastSeenUtc >= renewalNow\.AddMinutes\(-2\)/);
  assert.match(service, /item\.Status != "REJECTED"/);
  assert.match(service, /BatchLocalUploadRules\.FindAgentConflict/);
  assert.match(service, /PrintBatchCapacityRules\.CanAdd/);

  const expiredStart = api.indexOf("if (isUncompletedLocalUploadTicketExpired(ticket.completed, ticket.expiresUtc))");
  const expiredEnd = api.indexOf("if (ticket.completed)", expiredStart);
  assert.ok(expiredStart >= 0 && expiredEnd > expiredStart);
  const expiredRecovery = api.slice(expiredStart, expiredEnd);
  assert.match(expiredRecovery, /uploadCustomerLocalTicketViaCloud/);
  assert.match(expiredRecovery, /exception\.code === "local_upload_recovery_expired"/);
  assert.match(expiredRecovery, /exception\.code === "batch_upload_locked_after_payment_start"/);
  assert.match(expiredRecovery, /clearCustomerLocalUploadAttempt/);
  assert.match(expiredRecovery, /LocalUploadOutcomeUnknownError/);
  assert.match(app, /batchCheckoutLocked=exception instanceof LocalUploadPreflightError/);
  assert.match(app, /!batchCheckoutLocked&&canFallbackToCloudFromLocalFailure/);
  assert.match(app, /Checkout has already started for this batch/);
  assert.match(entitlements, /!db\.PrintBatchPayments\.Any\(payment =>[\s\S]*?payment\.Status != "REJECTED"/);
});

test("batch file review delegates to a service that keeps atomicity and payment/refund safeguards", () => {
  const endpoint = readApi("ShopPrintBatchReviewEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/ShopPrintBatchReviewService.cs");
  const rules = read("../../../src/SmartPrint.Api/Domain/PrintBatchReviewRules.cs");
  assert.match(endpoint, /IsShopOwner\(principal\)/);
  assert.match(endpoint, /batchReview\.ApplyAsync/);
  assert.match(service, /FOR UPDATE/);
  assert.match(service, /CustomerSessionId/);
  assert.match(service, /PrintBatchPayments/);
  assert.match(service, /PAYMENT_REJECTED/);
  assert.match(service, /PARTIALLY_REFUNDED/);
  assert.match(service, /SHOP_REJECTED_BATCH_FILE/);
  assert.match(service, /DbUpdateConcurrencyException/);
  assert.match(service, /FileRequiresRefundReview/);
  assert.match(rules, /reviewed\.Length is < 1 or > 20/);
  assert.match(rules, /accepted\.Except\(reviewed\)/);
});


test("customer job status remains scoped to the active customer session in its service", () => {
  const endpoint = readApi("PublicJobStatusEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/PublicJobStatusService.cs");
  assert.match(endpoint, /status\.GetAsync/);
  assert.match(service, /item\.TenantId == session\.TenantId/);
  assert.match(service, /item\.CustomerSessionId == session\.Id/);
  assert.match(service, /item\.RevokedUtc == null/);
  assert.match(service, /item\.ExpiresUtc > now/);
  assert.match(service, /PrintJobEvents/);
  assert.match(service, /PrintPayments/);
  assert.match(service, /PrintBatchPayments/);
  assert.match(service, /BuildUpiUri/);
});


test("batch review defaults every uploaded file to accepted until the shopkeeper rejects it", () => {
  const app = read("../src/App.tsx");
  assert.match(app, /const acceptedIds=jobs\.filter\(job=>decisions\[job\.id\]\?\?true\)\.map\(job=>job\.id\)/);
  assert.match(app, /onDecision:\(jobId:string,accepted:boolean\)=>void/);
  assert.match(app, /decisions\[job\.id\]\?\?true/);
});

test("customer session lifecycle remains behind thin route adapters", () => {
  const endpoint = readApi("PublicSessionEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/PublicCustomerSessionService.cs");
  assert.match(endpoint, /sessions\.RenewAsync/);
  assert.match(endpoint, /sessions\.ResumeAsync/);
  assert.match(endpoint, /sessions\.StartAsync/);
  assert.match(endpoint, /sessions\.GetCurrentAsync/);
  assert.match(endpoint, /\/sessions\/current\/renew/);
  assert.match(endpoint, /\/sessions\/current\/resume/);
  assert.match(endpoint, /\/stations\/\{publicCode\}\/sessions/);
  assert.match(endpoint, /\/sessions\/current/);
  assert.match(service, /CustomerUserId = customerUserId/);
  assert.match(service, /ExpiresUtc <= now\.AddMinutes\(10\)/);
  assert.match(service, /CloudAssistedPrintingEnabled/);
  assert.match(service, /PhotoPricing/);
});

test("photo quotes support multi-page sheets and page-level refund allocation", () => {
  const endpoint = readApi("PublicJobQuoteEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/PublicJobQuoteService.cs");
  const request = read("../../../src/SmartPrint.Api/Contracts/ApiContracts.cs");
  const rules = read("../../../src/SmartPrint.Api/Domain/PrintJobQuoteRules.cs");
  const pricing = read("../../../src/SmartPrint.Api/Pricing/PricingCalculator.cs");
  assert.match(request, /int\? PhotosPerPage = null/);
  assert.match(endpoint, /quotes\.CreateQuoteAsync/);
  assert.match(service, /request\.PhotosPerPage/);
  assert.match(service, /PricingCalculator\.PhotoPageCount/);
  assert.match(service, /PricingCalculator\.CalculatePhotoPageRefundAllocations/);
  assert.match(service, /PageRefundAllocationsJson = JsonSerializer\.Serialize\(pageRefundAllocations\)/);
  assert.match(rules, /PricingCalculator\.MaximumPhotosPerA4Sheet/);
  assert.match(pricing, /public static long\[\] CalculatePhotoPageRefundAllocations/);
});

test("public quote acceptance remains row-locked, expiry-aware and idempotent", () => {
  const endpoint = readApi("PublicJobQuoteEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/PublicJobQuoteService.cs");
  assert.match(endpoint, /quotes\.AcceptQuoteAsync/);
  assert.match(service, /FOR UPDATE/);
  assert.match(service, /PrintJobQuoteRules\.EvaluateAcceptance/);
  assert.match(service, /AlreadyAccepted/);
  assert.match(service, /QUOTE_EXPIRED_BEFORE_ACCEPTANCE/);
  assert.match(service, /CUSTOMER_ACCEPTED_QUOTE/);
  assert.match(service, /DuplicateRequest = true/);
});

test("shop photo revision protects the original document and locks editing after payment", () => {
  const endpoint = readApi("ShopPhotoRevisionEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/ShopPhotoRevisionService.cs");
  assert.match(endpoint, /MapShopPhotoRevisionEndpoints/);
  assert.match(endpoint, /photo-revision-grant/);
  assert.match(endpoint, /photo-revision/);
  assert.match(service, /Status != "AWAITING_SHOP_APPROVAL"/);
  assert.match(service, /item\.Status != "REJECTED" && item\.Status != "REFUNDED"/);
  assert.match(service, /document\.MediaType is not \("image\/jpeg" or "image\/png"\)/);
  assert.match(service, /CalculatePhotoPageRefundAllocations/);
  assert.match(service, /newRevisionIsCommitted/);
});

test("local Agent photo revision is scoped to its grant and idempotent after commit", () => {
  const endpoint = readApi("AgentLocalPhotoRevisionEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/AgentLocalPhotoRevisionService.cs");
  const grant = read("../../../src/SmartPrint.Api/Security/LocalDocumentPhotoRevisionGrant.cs");
  assert.match(endpoint, /AgentAuthentication\.AuthenticateAsync/);
  assert.match(endpoint, /revisions\.AuthorizeAsync/);
  assert.match(endpoint, /revisions\.CompleteAsync/);
  assert.match(service, /LocalDocumentPhotoRevisionGrant\.IsFor/);
  assert.match(service, /item\.TenantId == device\.TenantId/);
  assert.match(service, /document\.AgentDeviceId != device\.Id/);
  assert.match(service, /MatchesCommittedRevision/);
  assert.match(service, /DuplicateRequest = true/);
  assert.match(grant, /photo_revision_agent_id/);
  assert.match(grant, /TimeSpan\.FromMinutes\(2\)/);
});

test("customer local transfer remains visible with a clear unavailable state when no Agent endpoint exists",()=>{
 const app=read("../src/App.tsx");
 const start=app.indexOf("function CustomerLocalConnection("),end=app.indexOf("type CustomerGroupSettings",start);
 const connection=app.slice(start,end);
 assert.match(connection,/if\(!localAgent\?\.endpoints\.length\)[\s\S]*?setCheckStatus\("unavailable"\)/);
 assert.doesNotMatch(connection,/if\(!localAgent\?\.endpoints\.length\)return null/);
 assert.match(connection,/Direct transfer is temporarily unavailable/);
 assert.match(app,/customer-upload-mode-unavailable/);
});

test("owner preview selects the persisted supported MIME type before the blob response type",()=>{
 const app=read("../src/App.tsx");
 assert.match(app,/const storedType=\(job\.document\.mediaType\|\|""\)/);
 assert.match(app,/supportedTypes\.has\(storedType\)[\s\S]*?supportedTypes\.has\(responseType\)/);
 assert.match(app,/Preview format is not supported/);
});

test("customer web upload accepts drag and drop and contains no JavaScript browser alert dialogs",()=>{
 const app=read("../src/App.tsx");
 assert.match(app,/onDragOver=\{event=>\{if\(!batchLockedForPayment\)event\.preventDefault\(\);\}\}/);
 assert.match(app,/onDrop=\{event=>\{event\.preventDefault\(\);if\(batchLockedForPayment\)return;chooseFiles\(Array\.from\(event\.dataTransfer\.files\|\|\[\]\)\);\}\}/);
 assert.doesNotMatch(app,/window\.(alert|confirm|prompt)\s*\(/);
});

test("photo studio's selected photo quantity creates one multi-page file with one photo-count record",()=>{
 const app=read("../src/App.tsx");
 const studio=read("../src/PhotoStudio.tsx");
 assert.match(studio,/const pages=Math\.ceil\(parsedCopies\/photosPerPage\)/);
 assert.match(studio,/onApply\(\[\{file:output,sizeCode:size\.id,photoCount:parsedCopies,photosPerPage\}\]\)/);
 assert.match(studio,/buildPhotoPdf\(jpegPages\)/);
 assert.match(app,/const generated=sheets\.map\(sheet=>sheet\.file\)/);
 assert.match(app,/photos\[fp\]=\{sizeCode:sheet\.sizeCode,photoCount:sheet\.photoCount,photosPerPage:sheet\.photosPerPage\}/);
});

test("Shop Wi-Fi option remains visible when HTTPS fallback is disabled",()=>{
 const app=read("../src/App.tsx");
 const start=app.indexOf('<div className="customer-upload-modes">');
 const picker=app.indexOf('<div className="customer-upload-picker-grid"',start);
 assert.ok(start>=0&&picker>start);
 const modes=app.slice(start,picker);
 assert.match(modes,/customer-upload-mode-unavailable/);
 assert.match(modes,/secureQueueFallbackEnabled&&/);
 assert.doesNotMatch(modes,/\? <div className="customer-upload-modes"/);
});

test("cloud shop document preview retries the binary fetch with a refreshed access token",()=>{
 const api=read("../src/api.ts");
 const start=api.indexOf("export async function downloadShopDocument(");
 const end=api.indexOf("export function createAgentEnrollmentCode(",start);
 assert.ok(start>=0&&end>start);
 const preview=api.slice(start,end);
 assert.match(preview,/if \(response\.status === 401\)\s*\{\s*const refreshed = await refreshStoredSession\(token\)/);
 assert.match(preview,/if \(refreshed\) response = await request\(refreshed\.accessToken\)/);
 assert.match(preview,/Your shop session has expired/);
});

test("Photo Studio uses only the uploaded source and offers no edited-photo export action",()=>{
 const studio=read("../src/PhotoStudio.tsx");
 assert.match(studio,/source:\s*File/);
 assert.doesNotMatch(studio,/<input[^>]+type=["']file/i);
 assert.doesNotMatch(studio,/<a[^>]+download/i);
 assert.match(studio,/setDesktopContentProtection\(true\)/);
 assert.match(studio,/previewProtection/);
 const studioCss=read("../src/PhotoStudio.module.css");
 assert.match(studioCss,/\.previewProtection::before/);
 assert.match(studio,/mode === "shop" \? "Save sheet to this job" : "Prepare A4 sheet"/);
 assert.match(studio,/This prepared multi-page A4 photo PDF replaces it in your batch/);
 assert.match(studio,/type="date"/);
 assert.match(studio,/formatDobForPrint/);
});

test("photo quantities support up to 100 copies and automatically paginate over A4 sheets",()=>{
 const studio=read("../src/PhotoStudio.tsx");
 assert.match(studio,/const MAX_PHOTO_COUNT = 100/);
 assert.match(studio,/const maxCopies=MAX_PHOTO_COUNT/);
 assert.match(studio,/const sheetCount = Math\.ceil\(copies \/ photosPerPage\)/);
 assert.match(studio,/Photos per A4 page/);
 assert.match(studio,/Final A4 page:/);
});

test("cloud shop photo revisions require an unpaid review-stage image and validate every A4 page",()=>{
 const endpoint=readApi("ShopPhotoRevisionEndpoints.cs");
 const service=read("../../../src/SmartPrint.Api/Services/ShopPhotoRevisionService.cs");
 assert.match(endpoint,/MapPost\("\/print-jobs\/\{jobId:guid\}\/photo-revision"/);
 assert.match(service,/job\.Status != "AWAITING_SHOP_APPROVAL"/);
 assert.match(service,/string\.Equals\(document\.StorageMode, "CLOUD"/);
 assert.match(service,/document\.MediaType is not \("image\/jpeg" or "image\/png"\)/);
 assert.match(service,/HasPaymentAsync/);
 assert.match(service,/job\.LocalQueueReceivedUtc is not null/);
 assert.match(service,/job\.ClaimedByAgentDeviceId is not null/);
 assert.match(service,/PricingCalculator\.PhotoPageCount\(photoCount, photoSizeCode, photosPerPage\)/);
 assert.match(service,/stored\.Analysis\.PageCount != expectedPageCount/);
 assert.match(service,/SHOP_PHOTO_ENHANCED/);
});

test("customer photo previews do not expose the raw image or prepared PDF viewer",()=>{
 const app=read("../src/App.tsx");
 const previewStart=app.indexOf("{previewUrl&&file&&");
 const previewEnd=app.indexOf("{files.length>0&&<button className=\"customer-flow-button\"",previewStart);
 assert.ok(previewStart>=0&&previewEnd>previewStart);
 const preview=app.slice(previewStart,previewEnd);
 assert.match(preview,/Photo held in the shop workflow/);
 assert.match(preview,/Photo sheet prepared for this shop/);
 assert.doesNotMatch(preview,/<img src=\{previewUrl\}/);
 assert.doesNotMatch(preview,/photoSettingsByFileFingerprint\[[^\]]+\]\?<iframe/);
});

test("the exact same Photo Studio is opened from the shop image review",()=>{
 const app=read("../src/App.tsx");
 assert.match(app,/setShopPhotoStudio\(\{source,job:preview\.job,returnPreview\}\)/);
 assert.match(app,/replaceShopPhotoDocument\(tokens\.accessToken,editor\.job\.id,sheet\.file,sheet\.sizeCode,sheet\.photoCount,sheet\.photosPerPage\)/);
 assert.match(app,/shopPhotoStudio && <PhotoStudio source=\{shopPhotoStudio\.source\}/);
 assert.match(app,/Enhance photo for printing/);
});

test("native Windows content capture protection covers the photo editor and image preview",()=>{
 const studio=read("../src/PhotoStudio.tsx");
 const app=read("../src/App.tsx");
 const bridge=read("../src/desktop-bridge.ts");
 const permissions=JSON.parse(read("../../desktop/src-tauri/capabilities/default.json"));
 assert.match(studio,/setDesktopContentProtection\(true\)/);
 assert.match(app,/setDesktopContentProtection\(true\)/);
 assert.match(bridge,/getCurrentWindow\(\)\.setContentProtected\(enabled\)/);
 assert.ok(permissions.permissions.includes("core:window:allow-set-content-protected"));
});

test("shop owner can open the same editor for cloud and local Agent images",()=>{
 const app=read("../src/App.tsx");
 const api=read("../src/api.ts");
 assert.match(app,/preview\.job\.storageMode==="CLOUD"\|\|preview\.job\.storageMode==="LOCAL_AGENT"/);
 assert.match(app,/editor\.job\.storageMode==="LOCAL_AGENT"[\s\S]*?replaceShopPhotoDocumentLocally/);
 assert.match(app,/replaceShopPhotoDocument\(tokens\.accessToken,editor\.job\.id,sheet\.file,sheet\.sizeCode,sheet\.photoCount,sheet\.photosPerPage\)/);
 assert.match(api,/\/api\/v1\/shop\/print-jobs\/\$\{jobId\}\/photo-revision-grant/);
 assert.match(api,/\/api\/v1\/local\/documents\/\$\{encodeURIComponent\(jobId\)\}\/photo-revision/);
});

test("local photo revisions use a purpose-bound grant and preserve local storage",()=>{
 const shop=read("../../../src/SmartPrint.Api/Services/ShopPhotoRevisionService.cs");
 const endpoint=readApi("AgentLocalPhotoRevisionEndpoints.cs");
 const service=read("../../../src/SmartPrint.Api/Services/AgentLocalPhotoRevisionService.cs");
 const agent=read("../../../src/SmartPrint.Agent/LocalAgentApiHostedService.cs");
 const queue=read("../../../src/SmartPrint.Agent/AgentLocalQueueStore.cs");
 const grant=read("../../../src/SmartPrint.Api/Security/LocalDocumentPhotoRevisionGrant.cs");
 assert.match(shop,/LocalDocumentPhotoRevisionGrant\.Issue\([\s\S]*?actorUserId/);
 assert.match(endpoint,/X-Print-Photo-Revision-Grant/);
 assert.match(service,/LocalDocumentPhotoRevisionGrant\.TryValidate/);
 assert.match(service,/document\.StorageMode = "LOCAL_AGENT"/);
 assert.match(service,/ActorId = grant\.ActorUserId/);
 assert.match(agent,/X-Print-Photo-Revision-Grant/);
 assert.match(agent,/ReplaceStagedPhotoDocumentAsync/);
 assert.match(agent,/local-document-photo-revisions\/\{jobId\}\/complete/);
 assert.match(queue,/await commitServerRevisionAsync\(\)/);
 assert.match(queue,/File\.Move\(backupPath, entry\.DocumentPath\)/);
 assert.match(grant,/local_document_photo_revision/);
});

test("local photo revision rejects paid, handed-off and changed originals",()=>{
 const service=read("../../../src/SmartPrint.Api/Services/AgentLocalPhotoRevisionService.cs");
 assert.match(service,/job\.Status != "AWAITING_SHOP_APPROVAL"/);
 assert.match(service,/job\.LocalQueueReceivedUtc is not null/);
 assert.match(service,/job\.ClaimedByAgentDeviceId is not null/);
 assert.match(service,/document\.StorageMode != "LOCAL_AGENT"/);
 assert.match(service,/document\.AgentDeviceId != device\.Id/);
 assert.match(service,/HasPaymentAsync/);
 assert.match(service,/document\.Sha256, request\.ExpectedOriginalSha256/);
 assert.match(service,/request\.PhotosPerPage > PricingCalculator\.MaximumPhotosPerA4Sheet/);
 assert.match(service,/MatchesCommittedRevision/);
});

test("shop-owner Photo Studio supports cloud and local storage without changing a local job into cloud storage",()=>{
 const app=read("../src/App.tsx");
 const api=read("../src/api.ts");
 const shop=read("../../../src/SmartPrint.Api/Services/ShopPhotoRevisionService.cs");
 const agent=read("../../../src/SmartPrint.Api/Services/AgentLocalPhotoRevisionService.cs");
 assert.match(app,/preview\.job\.storageMode==="CLOUD"\|\|preview\.job\.storageMode==="LOCAL_AGENT"/);
 assert.match(app,/editor\.job\.storageMode==="LOCAL_AGENT"[\s\S]*?replaceShopPhotoDocumentLocally/);
 assert.match(api,/\/api\/v1\/shop\/print-jobs\/\$\{jobId\}\/photo-revision-grant/);
 assert.match(api,/\/api\/v1\/local\/documents\/\$\{encodeURIComponent\(jobId\)\}\/photo-revision/);
 assert.match(shop,/document\.StorageMode = "CLOUD"/);
 assert.match(agent,/document\.StorageMode = "LOCAL_AGENT"/);
});

test("native Android protects Photo Studio from screenshots through the origin-pinned WebView bridge",()=>{
 const studio=read("../src/PhotoStudio.tsx");
 const activity=read("../../android/app/src/main/java/com/quantaedge/smartprint/MainActivity.kt");
 assert.match(studio,/postMessage\("setSensitivePreview\|on"\)/);
 assert.match(studio,/postMessage\("setSensitivePreview\|off"\)/);
 assert.match(activity,/sourceOrigin\.scheme == trustedPage\.scheme/);
 assert.match(activity,/sourceOrigin\.host\.equals\(trustedPage\.host, true\)/);
 assert.match(activity,/isMainFrame/);
 assert.match(activity,/parts\[0\] == "setSensitivePreview"/);
 assert.match(activity,/window\.addFlags\(WindowManager\.LayoutParams\.FLAG_SECURE\)/);
 assert.match(activity,/window\.clearFlags\(WindowManager\.LayoutParams\.FLAG_SECURE\)/);
});


test("PDF export preview grants require a verified local Agent-owned export", () => {
  const shop = readApi("ShopPrintJobReadEndpoints.cs");
  const agent = readApi("AgentDocumentPreviewEndpoints.cs");
  const grant = read("../../../src/SmartPrint.Api/Security/LocalDocumentPreviewGrant.cs");

  assert.match(shop, /context\.Request\.Query\["variant"\]/);
  assert.match(shop, /output\.Status != "COMPLETED"/);
  assert.match(shop, /output\.OutputType != "PDF_EXPORT"/);
  assert.match(shop, /output\.OutputSha256 is not \{ Length: 64 \}/);
  assert.match(shop, /output\.ClaimedByAgentDeviceId is not Guid outputAgentId/);
  assert.match(shop, /LocalDocumentPreviewGrant\.Issue/);
  assert.match(agent, /LocalDocumentPreviewGrant\.TryValidate/);
  assert.match(agent, /job\.ClaimedByAgentDeviceId == device\.Id/);
  assert.match(agent, /output\.OutputType != "PDF_EXPORT"/);
  assert.match(agent, /MediaType = "application\/pdf"/);
  assert.match(grant, /local_document_preview/);
});


test("cloud upload keeps encrypted storage when the transaction commit outcome cannot be confirmed", () => {
  const service = read("../../../src/SmartPrint.Api/Services/PublicJobCreationService.cs");
  assert.match(service, /ReconcileStoredDocumentAfterUnknownCommitAsync/);
  assert.match(service, /item\.StorageKey == storageKey/);
  assert.match(service, /if \(committed\)\s*return/);
  assert.match(service, /Retain the encrypted object when the DB outcome cannot be checked/);
  assert.match(service, /logger\.LogError/);
});


test("Agent job document downloads are limited to the active device lease and preserve private response headers", () => {
  const endpoint = readApi("AgentJobEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/AgentJobQueueService.cs");
  assert.match(endpoint, /queue\.GetDocumentAsync/);
  assert.match(endpoint, /Cache-Control.*private, no-store/);
  assert.match(endpoint, /X-Content-Type-Options.*nosniff/);
  assert.match(endpoint, /X-Document-SHA256/);
  assert.match(service, /job\.TenantId == device\.TenantId/);
  assert.match(service, /job\.ClaimedByAgentDeviceId == device\.Id/);
  assert.match(service, /job\.AgentLeaseExpiresUtc > now/);
  assert.match(service, /AgentJobDocumentFailure\.StoredOnLocalAgent/);
});

test("Agent local receipt acknowledgement stays idempotent and checks the original document hash and lease", () => {
  const endpoint = readApi("AgentJobEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/AgentJobQueueService.cs");
  const rules = read("../../../src/SmartPrint.Api/Domain/AgentJobReceivedRules.cs");
  assert.match(endpoint, /queue\.AcknowledgeReceivedAsync/);
  assert.match(service, /AgentJobReceivedRules\.TryNormalize/);
  assert.match(service, /document\.Sha256, sha/);
  assert.match(service, /document\.SizeBytes != request\.SizeBytes/);
  assert.match(service, /job\.ClaimedByAgentDeviceId != device\.Id/);
  assert.match(service, /AgentJobReceiptFailure\.LeaseExpired/);
  assert.match(service, /DuplicateRequest: true/);
  assert.match(service, /AGENT_CONFIRMED_LOCAL_QUEUE_RECEIPT/);
  assert.match(rules, /MaximumDocumentBytes = 20L \* 1024 \* 1024/);
});


test("Agent heartbeat and printer synchronization remain behind connectivity workflow services", () => {
  const endpoint = readApi("AgentConnectivityEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/AgentConnectivityService.cs");
  const rules = read("../../../src/SmartPrint.Api/Domain/AgentPrinterSyncRules.cs");
  assert.match(endpoint, /connectivity\.HeartbeatAsync/);
  assert.match(endpoint, /connectivity\.SyncPrintersAsync/);
  assert.match(service, /AgentLocalEndpointRules\.Normalize/);
  assert.match(service, /device\.LastSeenUtc = now/);
  assert.match(service, /AgentPrinterSyncRules\.Validate/);
  assert.match(service, /BeginTransactionAsync/);
  assert.match(service, /AcquireQuotaLockAsync/);
  assert.match(service, /CheckCapacityAsync/);
  assert.match(service, /previousAgent\.RevokedUtc != null/);
  assert.match(service, /printer\.Status = "Unavailable"/);
  assert.match(rules, /ValidStatuses/);
  assert.match(rules, /GroupBy\(printer => printer\.QueueName\.Trim\(\), StringComparer\.OrdinalIgnoreCase\)/);
});


test("shop owner account and profile mutations remain tenant scoped thin adapters", () => {
  const endpoint = readApi("ShopAccountEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/ShopAccountService.cs");
  assert.match(endpoint, /accounts\.UpdateOwnerAccountAsync/);
  assert.match(endpoint, /accounts\.UpdateProfileAsync/);
  assert.match(endpoint, /IsShopOwner\(principal\)/);
  assert.match(endpoint, /TryGetTenantId\(principal, out var tenantId\)/);
  assert.match(endpoint, /RequireRateLimiting\("authentication"\)/);
  assert.match(service, /user\.TenantId != tenantId/);
  assert.match(service, /CheckPasswordSignInAsync/);
  assert.match(service, /FindByEmailAsync/);
  assert.match(service, /BeginTransactionAsync/);
  assert.match(service, /UserManager<AppUser>/);
  assert.match(service, /Uri\.UriSchemeHttps/);
  assert.match(service, /plan\.WhiteLabelEnabled/);
  assert.match(service, /ShopProfileRules\.IsHexColor/);
  assert.match(service, /ShopOwnerAccountRules\.TryNormalizeEmail/);
});


test("shop and customer registration routes delegate bootstrap and identity creation to one service boundary", () => {
  const endpoint = readApi("AccountRegistrationEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/AccountRegistrationService.cs");
  const rules = read("../../../src/SmartPrint.Api/Domain/AccountRegistrationRules.cs");
  assert.match(endpoint, /registration\.RegisterShopAsync/);
  assert.match(endpoint, /registration\.RegisterCustomerAsync/);
  assert.match(endpoint, /AllowAnonymous/);
  assert.match(service, /TenantSlugPolicy\.Create/);
  assert.match(service, /ShopPaymentProfiles\.Add/);
  assert.match(service, /ShopPricingProfiles\.Add/);
  assert.match(service, /TenantSubscriptions\.Add/);
  assert.match(service, /BeginTransactionAsync/);
  assert.match(service, /AddClaimAsync\(user, new Claim\("shop_role", "ShopOwner"\)\)/);
  assert.match(service, /TenantId = null/);
  assert.match(service, /AddClaimAsync\(user, new Claim\("customer_role", "Customer"\)\)/);
  assert.match(service, /HashRefreshToken/);
  assert.match(rules, /MailAddress\.TryCreate/);
});


test("Agent enrollment keeps one-use codes, physical-computer identity locking and quota checks in its service", () => {
  const endpoint = readApi("AgentEnrollmentEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/AgentEnrollmentService.cs");
  const rules = read("../../../src/SmartPrint.Api/Domain/AgentEnrollmentRules.cs");
  assert.match(endpoint, /enrollment\.EnrollAsync/);
  assert.match(service, /HashOpaqueToken\(code\)/);
  assert.match(service, /pg_advisory_xact_lock/);
  assert.match(service, /FingerprintAlreadyEnrolled/);
  assert.match(service, /AcquireQuotaLockAsync/);
  assert.match(service, /item\.UsedUtc == null && item\.ExpiresUtc > now/);
  assert.match(service, /ExecuteUpdateAsync/);
  assert.match(service, /RandomNumberGenerator\.GetBytes\(32\)/);
  assert.match(rules, /request\.Fingerprint/);
});

test("shop Agent pairing code creation and revocation are isolated in the management service", () => {
  const endpoint = readApi("ShopAgentManagementEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/ShopAgentManagementService.cs");
  assert.match(endpoint, /management\.CreateEnrollmentCodeAsync/);
  assert.match(endpoint, /management\.RevokeAsync/);
  assert.match(service, /AddMinutes\(10\)/);
  assert.match(service, /HashOpaqueToken\(code\)/);
  assert.match(service, /device\.TenantId == tenantId/);
  assert.match(service, /printer\.Status, "AgentRevoked"/);
  assert.match(service, /device\.RevokedUtc is null/);
});

test("shop printer creation and activation retain quota and active-output protections in the service", () => {
  const endpoint = readApi("ShopPrinterEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/ShopPrinterService.cs");
  const rules = read("../../../src/SmartPrint.Api/Domain/ShopPrinterRules.cs");
  assert.match(endpoint, /printers\.CreateAsync/);
  assert.match(endpoint, /printers\.SetActiveAsync/);
  assert.match(service, /AcquireQuotaLockAsync/);
  assert.match(service, /CheckCapacityAsync/);
  assert.match(service, /LOCAL_QUEUE_CLAIMED/);
  assert.match(service, /SPOOLER_ACCEPTED/);
  assert.match(service, /PRINTING/);
  assert.match(rules, /JsonDocument\.Parse/);
  assert.match(rules, /MaximumRateMinor|ConnectionTypes/);
});

test("QR station creation and code rotation keep public URL validation and secret generation in the service", () => {
  const endpoint = readApi("ShopStationEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/ShopStationService.cs");
  const rules = read("../../../src/SmartPrint.Api/Domain/ShopStationRules.cs");
  assert.match(endpoint, /stations\.CreateAsync/);
  assert.match(endpoint, /stations\.RotateCodeAsync/);
  assert.match(endpoint, /stations\.SetEnabledAsync/);
  assert.match(service, /AcquireQuotaLockAsync/);
  assert.match(service, /HashOpaqueToken\(publicCode\)/);
  assert.match(service, /RandomNumberGenerator\.GetBytes\(24\)/);
  assert.match(service, /printer\.TenantId == tenantId/);
  assert.match(rules, /Uri\.UriSchemeHttps/);
  assert.match(rules, /uri\.AbsolutePath != "\/"/);
});

test("shop pricing updates keep validated paise bounds, versioning and persistence in their service", () => {
  const endpoint = readApi("ShopPricingEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/ShopPricingService.cs");
  const rules = read("../../../src/SmartPrint.Api/Domain/ShopPricingRules.cs");
  assert.match(endpoint, /pricingService\.UpdateAsync/);
  assert.match(service, /ShopPricingRules\.IsValid/);
  assert.match(service, /pricing\.Version\+\+/);
  assert.match(service, /pricing\.IsConfigured = true/);
  assert.match(service, /SaveChangesAsync/);
  assert.match(rules, /MaximumRateMinor = 100_000/);
  assert.match(rules, /MaximumMinimumChargeMinor = 10_000_000/);
});

test("platform tenant status mutations remain locked, audit-backed and block permanent deactivation with unresolved work", () => {
  const endpoint = readApi("PlatformTenantStatusEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/PlatformTenantStatusService.cs");
  const rules = read("../../../src/SmartPrint.Api/Domain/TenantStatusChangeRules.cs");
  assert.match(endpoint, /statuses\.ChangeAsync/);
  assert.match(service, /FOR UPDATE/);
  assert.match(service, /TenantStatusRules\.CanTransition/);
  assert.match(service, /unresolvedJobs/);
  assert.match(service, /pendingPayments/);
  assert.match(service, /outstandingRejectedRefunds/);
  assert.match(service, /TenantStatusAudits\.Add/);
  assert.match(service, /DuplicateRequest: true/);
  assert.match(rules, /reason\.Length is >= 5 and <= 500/);
});


test("single-job shop decisions preserve row-locked quote expiry, approval, rejection and audit events in a service", () => {
  const endpoint = readApi("ShopPrintJobDecisionEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/ShopPrintJobDecisionService.cs");
  assert.match(endpoint, /decisions\.ApproveAsync/);
  assert.match(endpoint, /decisions\.RejectAsync/);
  assert.match(service, /FOR UPDATE/);
  assert.match(service, /QuoteExpiresUtc/);
  assert.match(service, /QUOTE_EXPIRED_BEFORE_SHOP_APPROVAL/);
  assert.match(service, /SHOP_APPROVED_AWAITING_PAYMENT/);
  assert.match(service, /SHOP_REJECTED/);
  assert.match(service, /DuplicateRequest: true/);
});

test("shop payment method updates keep UPI validation and payment availability rules in their service", () => {
  const endpoint = readApi("ShopPaymentSettingsEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/ShopPaymentSettingsService.cs");
  const rules = read("../../../src/SmartPrint.Api/Domain/ShopPaymentSettingsRules.cs");
  assert.match(endpoint, /settingsService\.UpdateAsync/);
  assert.match(service, /ShopPaymentSettingsRules\.Validate/);
  assert.match(service, /settings\.InstantPaymentEnabled = request\.InstantPaymentEnabled/);
  assert.match(service, /settings\.UpiId = request\.UpiEnabled \? upiId : null/);
  assert.match(rules, /IsValidUpiId/);
  assert.match(rules, /NoPaymentMethodEnabled/);
});

test("authentication session routes delegate customer login, platform/shop login, refresh rotation and logout", () => {
  const endpoint = readApi("AuthenticationSessionEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/AuthenticationSessionService.cs");
  assert.match(endpoint, /sessions\.CustomerLoginAsync/);
  assert.match(endpoint, /sessions\.LoginAsync/);
  assert.match(endpoint, /sessions\.RefreshAsync/);
  assert.match(endpoint, /sessions\.LogoutAsync/);
  assert.match(service, /CustomerIdentity\.IsCustomer/);
  assert.match(service, /PlatformAdminAuthorization\.IsPlatformAdmin/);
  assert.match(service, /CheckPasswordSignInAsync/);
  assert.match(service, /ExecuteUpdateAsync/);
  assert.match(service, /existing\.ReplacedByTokenId = replacement\.Id/);
  assert.match(service, /HashRefreshToken/);
});

test("explicit Agent print-output claims preserve local-document proof, same-batch, payment, heartbeat and lease gates", () => {
  const endpoint = readApi("AgentPrintClaimEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/AgentPrintClaimService.cs");
  const rules = read("../../../src/SmartPrint.Api/Domain/AgentPrintClaimRules.cs");
  assert.match(endpoint, /claims\.ClaimAsync/);
  assert.match(service, /AgentClaimAccess\.GetAgentClaimAccessAsync/);
  assert.match(service, /document\.Sha256 == proof\.Sha256/);
  assert.match(service, /document\.SizeBytes == proof\.SizeBytes/);
  assert.match(service, /other\.PrinterDeviceId != job\.PrinterDeviceId/);
  assert.match(service, /payment\.Status == "VERIFIED"/);
  assert.match(service, /printer\.Status == "Ready" \|\| printer\.Status == "Busy"/);
  assert.match(service, /PrintDispatchLeaseExpiresUtc/);
  assert.match(service, /AGENT_RECLAIMED_EXPIRED_PRINT_LEASE/);
  assert.match(rules, /proof\.JobId != Guid.Empty/);
});
