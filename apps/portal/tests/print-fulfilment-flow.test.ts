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
  assert.match(app, /setCheckStatus\("unavailable"\);\s*onReady\(false\);[\s\S]*?onUnavailable\(\);/);
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
