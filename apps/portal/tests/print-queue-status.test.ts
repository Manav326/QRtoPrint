import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";

const read = (path: string) => readFileSync(new URL(path, import.meta.url), "utf8");
import { printBlockMessage, spoolerStatusWarning } from "../src/features/shop/queue/print-queue-status.ts";

test("queue warnings explain printer state without suggesting duplicate output", () => {
  assert.match(spoolerStatusWarning("OFFLINE", "Ready"), /Do not start a duplicate/);
  assert.match(spoolerStatusWarning("PAPEROUT", "Ready"), /Refill it/);
  assert.match(spoolerStatusWarning("PAUSED", "Ready"), /Resume the existing job/);
  assert.equal(spoolerStatusWarning("COMPLETED", "Ready"), "");
});

test("pre-submission print capability blocks explain the failed setting", () => {
  assert.match(printBlockMessage("PAPER_SIZE_UNSUPPORTED"), /paper size/);
  assert.match(printBlockMessage("COLOUR_REJECTED"), /colour output/);
  assert.match(printBlockMessage("DUPLEX_UNSUPPORTED"), /double-sided/);
  assert.match(printBlockMessage("UNKNOWN"), /could not confirm/);
});

test("queue rows expose Photo Studio only for supported image originals", () => {
  const queue = read("../src/features/shop/queue/PrintQueuePage.tsx");
  const actions = read("../src/features/shop/useShopPrintActions.ts");
  assert.match(queue, /onOpenPhotoStudio/);
  assert.match(queue, /Photo Studio/);
  assert.match(actions, /openShopPhotoStudioForJob/);
  assert.match(actions, /downloadShopDocument\(accessToken,job\.id,"original"\)/);
  assert.match(actions, /new File\(\[sourceBlob\],job\.document\.originalFileName,\{type:mediaType/);
});
