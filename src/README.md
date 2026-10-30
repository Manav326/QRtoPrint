# .NET services

- SmartPrint.Api: cloud control-plane API, tenant onboarding, shop authentication, QR sessions, protected document upload/quotes, job review/audit history, agent enrollment and printer inventory sync.
- SmartPrint.Agent: Windows background service. It pairs to one shop, stores its device secret with DPAPI LocalMachine, sends heartbeats and reports Windows-installed printer queues.

## Windows agent setup

See src/SmartPrint.Agent/README.md and the scripts below:

- scripts/pair-agent.ps1
- scripts/install-agent-service.ps1
- scripts/uninstall-agent-service.ps1

The current agent release discovers printer inventory and health only. It does not submit physical print jobs. Payment verification, durable local queueing, spooler submission and uncertain-output reconciliation are separate workstreams.
