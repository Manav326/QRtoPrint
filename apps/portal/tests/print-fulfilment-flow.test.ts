import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const read = (path: string) => readFileSync(new URL(path, import.meta.url), "utf8");

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
  const program = read("../../../src/SmartPrint.Api/Program.cs");
  const claimStart = program.indexOf('agentApi.MapPost("/jobs/claim"');
  const claimEnd = program.indexOf('agentApi.MapGet("/jobs/{jobId:guid}/document"', claimStart);
  const dispatchStart = program.indexOf('shop.MapPost("/print-jobs/{jobId:guid}/dispatch"');
  const dispatchEnd = program.indexOf('shop.MapPost("/print-jobs/{jobId:guid}/retry-pdf-export"', dispatchStart);
  assert.ok(claimStart >= 0 && claimEnd > claimStart);
  assert.ok(dispatchStart >= 0 && dispatchEnd > dispatchStart);
  const claimRoute = program.slice(claimStart, claimEnd);
  const dispatchRoute = program.slice(dispatchStart, dispatchEnd);
  assert.match(claimRoute, /printer\.IsActive/);
  assert.match(claimRoute, /printer\.QueueName != null/);
  assert.doesNotMatch(claimRoute, /printer\.Status == "Ready"/);
  assert.doesNotMatch(claimRoute, /printer\.LastSeenUtc/);
  assert.match(dispatchRoute, /printer\.Status == "Ready"/);
  assert.match(dispatchRoute, /printer\.LastSeenUtc/);
});

test("plan-limited HTTPS fallback is only offered when the Agent is unavailable", () => {
  const app = read("../src/App.tsx");
  const api = read("../src/api.ts");
  const program = read("../../../src/SmartPrint.Api/Program.cs");
  assert.match(app, /secureQueueFallbackEnabled = cloudAssistedPrintingEnabled \|\| !localAgent\?\.endpoints\.length \|\| localFallbackSuggested/);
  const directUploadStart = api.indexOf("export async function uploadCustomerDocument(");
  const directUploadEnd = api.indexOf("type CustomerLocalAgentApi", directUploadStart);
  const directUpload = api.slice(directUploadStart, directUploadEnd);
  assert.doesNotMatch(directUpload, /X-Print-Queue-Fallback/);
  const publicUploadStart = program.indexOf('publicApi.MapPost("/sessions/current/jobs"');
  const publicUploadEnd = program.indexOf('publicApi.MapPost("/sessions/current/local-upload-tickets"', publicUploadStart);
  assert.ok(publicUploadStart >= 0 && publicUploadEnd > publicUploadStart);
  const publicUpload = program.slice(publicUploadStart, publicUploadEnd);
  assert.match(publicUpload, /!subscription\.CloudAssistedPrintingEnabled && localAgentAvailable/);
  assert.doesNotMatch(publicUpload, /queueFallbackRequested/);
  assert.doesNotMatch(program, /X-Print-Queue-Fallback|queueFallbackRequested/);
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
  const program = read("../../../src/SmartPrint.Api/Program.cs");
  const blockedStart = program.indexOf('agentApi.MapPost("/jobs/{jobId:guid}/print-blocked"');
  const blockedEnd = program.indexOf('agentApi.MapPost("/jobs/{jobId:guid}/output-unconfirmed"', blockedStart);
  const assignmentStart = program.indexOf('shop.MapPatch("/print-jobs/{jobId:guid}/printer"');
  const assignmentEnd = program.indexOf('shop.MapPatch("/print-jobs/{jobId:guid}/settings"', assignmentStart);
  assert.ok(blockedStart >= 0 && blockedEnd > blockedStart);
  assert.ok(assignmentStart >= 0 && assignmentEnd > assignmentStart);
  const blocked = program.slice(blockedStart, blockedEnd);
  const assignment = program.slice(assignmentStart, assignmentEnd);
  assert.match(blocked, /job\.Status = "LOCAL_QUEUE_READY"/);
  assert.match(blocked, /job\.SpoolerJobName = null/);
  assert.match(blocked, /job\.SpoolerJobId = null/);
  assert.match(blocked, /job\.SpoolerStatus = "BLOCKED_BEFORE_SUBMISSION"/);
  assert.match(assignment, /var readyQueueCanBeReassigned = selectedJob\.Status == "LOCAL_QUEUE_READY"/);
  assert.match(assignment, /string\.IsNullOrWhiteSpace\(selectedJob\.SpoolerJobName\)/);
  assert.match(assignment, /needsQueueReset/);
});

test("reassigning a blocked queued job clears the previous printer failure", () => {
  const program = read("../../../src/SmartPrint.Api/Program.cs");
  const resetStart = program.indexOf("if (needsQueueReset)");
  const failureReset = program.indexOf("batchJob.FailureCode = null;", resetStart);
  const spoolerReset = program.indexOf("batchJob.SpoolerStatus = null;", resetStart);
  assert.ok(resetStart >= 0 && failureReset > resetStart && spoolerReset > failureReset);
});

test("cloud upload retries must match the original file, batch and selected print settings", () => {
  const program = read("../../../src/SmartPrint.Api/Program.cs");
  const routeStart = program.indexOf('publicApi.MapPost("/sessions/current/jobs"');
  const routeEnd = program.indexOf('publicApi.MapPost("/sessions/current/local-upload-tickets"', routeStart);
  assert.ok(routeStart >= 0 && routeEnd > routeStart);
  const route = program.slice(routeStart, routeEnd);
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
  const program = read("../../../src/SmartPrint.Api/Program.cs");
  const recoveryStart = program.indexOf('publicApi.MapPost("/sessions/current/local-upload-tickets/{ticketId:guid}/cloud-recovery"');
  const recoveryEnd = program.indexOf('publicApi.MapPost("/jobs/{jobId:guid}/quote"', recoveryStart);
  assert.ok(recoveryStart >= 0 && recoveryEnd > recoveryStart);
  const recovery = program.slice(recoveryStart, recoveryEnd);
  for (const entity of ["preflightJob", "existingJob", "job"]) {
    for (const property of ["ColourMode", "PaperSize", "IsDuplex", "Orientation", "PricingVersion"])
      assert.ok(recovery.includes(`${entity}.${property}`), `Recovery must return ${entity}.${property}.`);
  }
});

test("local Agent completion creates the job with the customer's ticket settings", () => {
  const program = read("../../../src/SmartPrint.Api/Program.cs");
  const completeStart = program.indexOf('agentApi.MapPost("/local-uploads/{ticketId:guid}/complete"');
  const jobStart = program.indexOf("var job = new PrintJob", completeStart);
  const documentStart = program.indexOf("var printDocument = new PrintDocument", jobStart);
  assert.ok(completeStart >= 0 && jobStart > completeStart && documentStart > jobStart);
  const localJob = program.slice(jobStart, documentStart);
  assert.match(localJob, /Copies = ticket\.Copies/);
  assert.match(localJob, /ColourMode = ticket\.ColourMode/);
  assert.match(localJob, /PaperSize = ticket\.PaperSize/);
  assert.match(localJob, /IsDuplex = ticket\.IsDuplex/);
  assert.match(localJob, /Orientation = ticket\.Orientation/);
});

test("local upload ticket retries preserve batch identity and renew the same expired reservation", () => {
  const program = read("../../../src/SmartPrint.Api/Program.cs");
  const api = read("../src/api.ts");
  const app = read("../src/App.tsx");
  const entitlements = read("../../../src/SmartPrint.Api/Services/TenantEntitlementService.cs");
  const ticketStart = program.indexOf('publicApi.MapPost("/sessions/current/local-upload-tickets"');
  const ticketEnd = program.indexOf('publicApi.MapPost("/sessions/current/local-upload-tickets/{ticketId:guid}/cloud-recovery"', ticketStart);
  assert.ok(ticketStart >= 0 && ticketEnd > ticketStart);
  const ticketRoute = program.slice(ticketStart, ticketEnd);
  assert.match(ticketRoute, /request\.BatchId is Guid requestedBatchId && existing\.BatchId != requestedBatchId/);
  assert.match(ticketRoute, /requestedBatchName is not null && existing\.BatchName != requestedBatchName/);
  assert.match(ticketRoute, /request\.BatchId is null \|\| racedTicket\.BatchId == request\.BatchId\.Value/);
  assert.match(ticketRoute, /requestedBatchName is null \|\| racedTicket\.BatchName == requestedBatchName/);
  assert.match(ticketRoute, /LocalUploadRecoveryRules\.CanRenewExpiredTicket/);
  assert.match(ticketRoute, /existing = lockedTicket/);
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
