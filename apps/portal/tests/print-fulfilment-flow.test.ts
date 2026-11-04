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
  const dispatchSource = readApi("ShopPrintJobDispatchEndpoints.cs");
  const dispatchService = read("../../../src/SmartPrint.Api/Services/ShopPrintJobDispatchService.cs");
  const claimStart = claimSource.indexOf('agentApi.MapPost("/jobs/claim"');
  const claimEnd = claimSource.indexOf('agentApi.MapGet("/jobs/{jobId:guid}/document"', claimStart);
  const dispatchStart = dispatchSource.indexOf('shop.MapPost("/print-jobs/{jobId:guid}/dispatch"');
  const dispatchEnd = dispatchSource.indexOf('shop.MapPost("/print-jobs/{jobId:guid}/retry-pdf-export"', dispatchStart);
  assert.ok(claimStart >= 0 && claimEnd > claimStart);
  assert.ok(dispatchStart >= 0 && dispatchEnd > dispatchStart);
  const claimRoute = claimSource.slice(claimStart, claimEnd);
  const dispatchRoute = dispatchSource.slice(dispatchStart, dispatchEnd);
  assert.match(claimRoute, /printer\.IsActive/);
  assert.match(claimRoute, /printer\.QueueName != null/);
  assert.doesNotMatch(claimRoute, /printer\.Status == "Ready"/);
  assert.doesNotMatch(claimRoute, /printer\.LastSeenUtc/);
  assert.match(dispatchRoute, /dispatch\.DispatchAsync/);
  assert.match(dispatchService, /printer\.Status == "Ready"/);
  assert.match(dispatchService, /printer\.LastSeenUtc/);
});

test("plan-limited HTTPS fallback is only offered when the Agent is unavailable", () => {
  const app = read("../src/App.tsx");
  const api = read("../src/api.ts");
  const publicUploadSource = readApi("PublicJobCreationEndpoints.cs");
  assert.match(app, /secureQueueFallbackEnabled = cloudAssistedPrintingEnabled \|\| !localAgent\?\.endpoints\.length \|\| localFallbackSuggested/);
  const directUploadStart = api.indexOf("export async function uploadCustomerDocument(");
  const directUploadEnd = api.indexOf("type CustomerLocalAgentApi", directUploadStart);
  const directUpload = api.slice(directUploadStart, directUploadEnd);
  assert.doesNotMatch(directUpload, /X-Print-Queue-Fallback/);
  const publicUploadStart = publicUploadSource.indexOf('publicApi.MapPost("/sessions/current/jobs"');
  assert.ok(publicUploadStart >= 0);
  const publicUpload = publicUploadSource.slice(publicUploadStart);
  assert.match(publicUpload, /!subscription\.CloudAssistedPrintingEnabled && localAgentAvailable/);
  assert.doesNotMatch(publicUpload, /queueFallbackRequested/);
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
  const blockedStart = blockedSource.indexOf('agentApi.MapPost("/jobs/{jobId:guid}/print-blocked"');
  const blockedEnd = blockedSource.indexOf('agentApi.MapPost("/jobs/{jobId:guid}/output-unconfirmed"', blockedStart);
  const serviceStart = blockedService.indexOf("public async Task<AgentSpoolerLifecycleResult> ReportPrintBlockedAsync");
  const serviceEnd = blockedService.indexOf("public async Task<AgentSpoolerLifecycleResult> ReportOutputUnconfirmedAsync", serviceStart);
  const assignmentStart = assignmentSource.indexOf('shop.MapPatch("/print-jobs/{jobId:guid}/printer"');
  assert.ok(blockedStart >= 0 && blockedEnd > blockedStart);
  assert.ok(serviceStart >= 0 && serviceEnd > serviceStart);
  assert.ok(assignmentStart >= 0);
  const blocked = blockedSource.slice(blockedStart, blockedEnd);
  const blockedWorkflow = blockedService.slice(serviceStart, serviceEnd);
  const assignment = assignmentSource.slice(assignmentStart);
  assert.match(blocked, /spooler\.ReportPrintBlockedAsync/);
  assert.match(blockedWorkflow, /job\.Status = "LOCAL_QUEUE_READY"/);
  assert.match(blockedWorkflow, /job\.SpoolerJobName = null/);
  assert.match(blockedWorkflow, /job\.SpoolerJobId = null/);
  assert.match(blockedWorkflow, /job\.SpoolerStatus = "BLOCKED_BEFORE_SUBMISSION"/);
  assert.match(assignment, /var readyQueueCanBeReassigned = selectedJob\.Status == "LOCAL_QUEUE_READY"/);
  assert.match(assignment, /string\.IsNullOrWhiteSpace\(selectedJob\.SpoolerJobName\)/);
  assert.match(assignment, /needsQueueReset/);
});

test("reassigning a blocked queued job clears the previous printer failure", () => {
  const assignment = readApi("ShopPrintJobAssignmentEndpoints.cs");
  const resetStart = assignment.indexOf("if (needsQueueReset)");
  const failureReset = assignment.indexOf("batchJob.FailureCode = null;", resetStart);
  const spoolerReset = assignment.indexOf("batchJob.SpoolerStatus = null;", resetStart);
  assert.ok(resetStart >= 0 && failureReset > resetStart && spoolerReset > failureReset);
});

test("cloud upload retries must match the original file, batch and selected print settings", () => {
  const source = readApi("PublicJobCreationEndpoints.cs");
  const routeStart = source.indexOf('publicApi.MapPost("/sessions/current/jobs"');
  assert.ok(routeStart >= 0);
  const route = source.slice(routeStart);
  const formRead = route.indexOf("await context.Request.ReadFormAsync(ct)");
  const existingLookup = route.indexOf("var existing = await db.PrintJobs.AsNoTracking()");
  const batchPaymentGate = route.indexOf("var batchCheckoutLocked =");
  assert.ok(formRead >= 0 && existingLookup > formRead);
  assert.ok(batchPaymentGate > existingLookup);
  assert.match(route, /SHA256\.HashDataAsync\(existingInput, ct\)/);
  for (const setting of ["Copies", "ColourMode", "PaperSize", "IsDuplex", "Orientation"])
    assert.ok(route.includes("existing."+setting), `Retry must compare ${setting}.`);
  assert.match(route, /existingDocument\.Sha256, suppliedSha/);
  assert.match(route, /existingDocument\.OriginalFileName != originalName/);
  assert.match(route, /existingDocument\.SizeBytes != file\.Length/);
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

test("local Agent completion creates the job with the customer's ticket settings", () => {
  const source = readApi("AgentLocalUploadEndpoints.cs");
  const service = read("../../../src/SmartPrint.Api/Services/AgentLocalUploadCompletionService.cs");
  const completeStart = source.indexOf('agentApi.MapPost("/local-uploads/{ticketId:guid}/complete"');
  const serviceStart = service.indexOf("public async Task<AgentLocalUploadCompletionResult> CompleteAsync");
  const jobStart = service.indexOf("var job = new PrintJob", serviceStart);
  const documentStart = service.indexOf("var printDocument = new PrintDocument", jobStart);
  assert.ok(completeStart >= 0 && serviceStart >= 0 && jobStart > serviceStart && documentStart > jobStart);
  assert.ok(source.includes("completionService.CompleteAsync(ticket, device, request, now, ct)"));
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
  const api = read("../src/api.ts");
  const app = read("../src/App.tsx");
  const entitlements = read("../../../src/SmartPrint.Api/Services/TenantEntitlementService.cs");
  const ticketStart = source.indexOf('publicApi.MapPost("/sessions/current/local-upload-tickets"');
  assert.ok(ticketStart >= 0);
  const ticketRoute = source.slice(ticketStart);
  assert.match(ticketRoute, /request\.BatchId is Guid requestedBatchId && existing\.BatchId != requestedBatchId/);
  assert.match(ticketRoute, /requestedBatchName is not null && existing\.BatchName != requestedBatchName/);
  assert.match(ticketRoute, /request\.BatchId is null \|\| racedTicket\.BatchId == request\.BatchId\.Value/);
  assert.match(ticketRoute, /requestedBatchName is null \|\| racedTicket\.BatchName == requestedBatchName/);
  assert.match(ticketRoute, /LocalUploadRecoveryRules\.CanRenewExpiredTicket/);
  assert.match(ticketRoute, /existing = lockedTicket/);
  assert.match(ticketRoute, /lockedTicket\.GrantHash == originalTicket\.GrantHash/);
  assert.match(ticketRoute, /lockedTicket\.BatchId == originalTicket\.BatchId/);
  assert.match(ticketRoute, /lockedTicket\.BatchGroupName == originalTicket\.BatchGroupName/);
  assert.match(ticketRoute, /FOR UPDATE/);
  assert.match(ticketRoute, /lockedTicket\.ExpiresUtc = renewalNow\.AddMinutes\(15\)/);
  assert.match(ticketRoute, /station\.PrinterDeviceId == lockedTicket\.PrinterDeviceId/);
  assert.match(ticketRoute, /printer\.AgentDeviceId == lockedTicket\.AgentDeviceId/);
  assert.match(ticketRoute, /agent\.LastSeenUtc >= renewalNow\.AddMinutes\(-2\)/);

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
  const request = read("../../../src/SmartPrint.Api/Contracts/ApiContracts.cs");
  const rules = read("../../../src/SmartPrint.Api/Domain/PrintJobQuoteRules.cs");
  const pricing = read("../../../src/SmartPrint.Api/Pricing/PricingCalculator.cs");
  assert.match(request, /int\? PhotosPerPage = null/);
  assert.match(endpoint, /request\.PhotosPerPage/);
  assert.match(endpoint, /PricingCalculator\.PhotoPageCount/);
  assert.match(endpoint, /PricingCalculator\.CalculatePhotoPageRefundAllocations/);
  assert.match(rules, /PricingCalculator\.MaximumPhotosPerA4Sheet/);
  assert.match(pricing, /public static long\[\] CalculatePhotoPageRefundAllocations/);
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
