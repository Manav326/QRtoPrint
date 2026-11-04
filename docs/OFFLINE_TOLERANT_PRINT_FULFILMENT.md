# Offline-tolerant print fulfilment

This note describes the existing QRtoPrint print-job lifecycle when a shop printer or its print worker is temporarily unavailable. It extends the existing PrintJob, LocalPrintUploadTicket and Agent queue; it does not introduce a second queue.

## Customer intake

The customer keeps the general choices visible: colour or black-and-white, single- or double-sided, copies, and A4. Advanced paper-size and orientation controls are opt-in. Folder defaults are applied to each file in that folder; a file can opt into its own custom values without changing sibling files or another folder.

Each file's final settings are stored with its own print job and are sent through both the local-ticket and HTTPS upload paths. Retries retain the same idempotency identity and recover the same job rather than creating a duplicate. An expired 15-minute LAN claim keeps that same ticket and one-use grant during its 24-hour recovery window: when the original Agent is still paired and online, the API renews the same ticket only after the old claim lease ends and before checkout starts; the Agent refreshes the matching protected-queue lease. When the Agent is unavailable, the client attempts HTTPS recovery against that same ticket instead of creating a second reservation. Checkout and renewal share the batch lock; after checkout starts, unfinished files are rejected as a new-batch request and their abandoned reservation is released from monthly quota usage.

## Secure staging and readiness

Local Agent intake requires an initialized protected queue and a fresh authenticated Agent heartbeat. It does not require the print worker to be running or a fresh printer-inventory scan. The local listener can therefore accept and encrypt a document while printer discovery or output execution is recovering.

A local upload ticket still binds to the shop's assigned printer and paired Agent identity, but ticket creation does not require the physical printer to report Ready or Busy. The queue name and printer assignment must exist so the same Agent can stage the request.

The existing HTTPS storage path remains the fallback when the paired Agent is unavailable. For shops without the cloud-assisted entitlement, the API checks Agent availability itself before accepting a new HTTPS upload or recovering an existing local ticket; the client cannot override that decision. When recovery is allowed, it remains bound to the original session, ticket ID, file hash/size and one-use grant.

## Owner review and dispatch

The Agent's paid-job claim secures the document and its final settings locally. It is not a print command. The shop owner reviews the request and explicitly starts output from the existing Print Queue. The dispatch path then checks the live Agent and physical printer state before authorising the Agent's print-claim operation.

At output, the Windows Agent asks the driver to apply the requested paper, orientation, colour and duplex settings. An explicit negative capability report blocks that option. A missing or failed capability report is treated as unknown: the Agent tries the requested DEVMODE and validates the driver's returned settings. It does not silently substitute a different setting. Orientation must be confirmed exactly before output begins.

A pre-submission block is reported to the API as a blocked-before-output event. The job returns to the owner queue as ready, with the failure code visible. The original protected document and customer settings are preserved; StartDoc/page submission is not reached for that attempt.

## Recovery and reassignment

The owner can assign a compatible printer to a paid job that has not started output. Local-only documents remain pinned to their original Agent computer; another printer on that same computer can be selected. Cloud-stored documents can be claimed by another paired Agent and downloaded from the existing encrypted storage path.

When an already-staged cloud document is reassigned on the same computer, the Agent updates the existing protected queue manifest with the authorised printer, queue and final settings. This applies to both acknowledgement-pending and acknowledged entries; it does not download or upload the document again. A successful reassignment clears the previous pre-submission failure so the new printer is not shown a stale warning.

## Quote and payment guardrails

Before payment starts, a shop-side settings change is recalculated into a revised quote, updates the settings shown to the customer, and requires the customer's explicit acceptance before checkout and output can proceed.

Once a payment attempt exists, the settings-revision endpoint rejects in-place changes rather than silently changing a paid amount or reusing an old payment. For a paid job blocked by printer capability, the supported immediate recovery is reassignment to a compatible printer. Any financial change after payment must be resolved through the shop's payment/refund process; it must not be treated as an ordinary pre-payment quote edit.
