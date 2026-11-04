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
  assert.match(app, /secureQueueFallbackEnabled = cloudAssistedPrintingEnabled \|\| !localAgent\?\.endpoints\.length/);
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
});
