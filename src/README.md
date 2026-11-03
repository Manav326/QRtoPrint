# .NET services

- SmartPrint.Api: cloud control-plane API for tenant onboarding, shop authentication, QR sessions, protected document upload and quotes, shop review, audit history, shop-owned UPI/cash payment verification, tenant-scoped reporting, agent enrollment, secure job handoff, print dispatch and output reconciliation.
- SmartPrint.Agent: Windows background service paired to one shop. It stores its device secret and received documents with DPAPI LocalMachine, reports printer inventory and heartbeat, claims authorised jobs, executes supported PDF/JPEG/PNG jobs through the installed Windows print driver, monitors spooler state, and marks uncertain output for operator reconciliation.

## Windows agent setup

See src/SmartPrint.Agent/README.md and the scripts below:

- scripts/pair-agent.ps1
- scripts/install-agent-service.ps1
- scripts/uninstall-agent-service.ps1

The current agent supports a protected local queue and explicitly authorised Windows spooler submission. It never treats spooler acceptance as proof of physical paper output and never automatically resubmits a job after an uncertain submission. The shop operator must inspect the paper and confirm output in the shop portal. Actual device/driver compatibility still requires physical validation.
