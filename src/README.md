# .NET services

- SmartPrint.Api: cloud control-plane API.
- SmartPrint.Agent: Windows background service and future local edge runtime.

The agent is intentionally a minimal service host at this stage. Printer discovery, secure local upload, durable queueing, cloud device identity, and spooler reconciliation are not yet implemented.
