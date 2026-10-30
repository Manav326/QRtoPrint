# Windows Print Agent

This is a Windows Service. It pairs with one shop using a single-use enrollment code, stores the returned credential with Windows DPAPI (LocalMachine), heartbeats to the Smart Print API, and synchronizes Windows-installed printer queue inventory every 30 seconds.

## Pair and install

1. In the shop workspace, open Print Agent and generate a one-time pairing code. It expires after 10 minutes.
2. Build or extract the SmartPrint.Agent Windows release on the shop computer.
3. Open PowerShell as Administrator and pair the computer:

   .\scripts\pair-agent.ps1 -ApiBaseUrl "https://YOUR-SMART-PRINT-API-HOST" -AgentExePath ".\SmartPrint.Agent.exe"

4. Install and start the Windows Service:

   .\scripts\install-agent-service.ps1 -AgentExePath ".\SmartPrint.Agent.exe"

5. Return to the Print Agent screen in the shop workspace. The computer should report its next heartbeat and installed printer queues.

To remove the Windows Service, run scripts\uninstall-agent-service.ps1 from an elevated PowerShell. This leaves the encrypted machine identity intact; revoke the agent in the shop workspace before disposing of or replacing the computer.

## Current capabilities

- Queries Win32_Printer through WMI for installed queue name, driver, port, status and default queue.
- Sends printer inventory to the paired shop tenant using agent-specific authentication headers.
- Refreshes agent heartbeat and printer status every 30 seconds (configurable from 10 to 300 seconds).
- Marks previously discovered queues unavailable when they disappear from that computer's WMI inventory.
- Stores the agent secret encrypted using DPAPI LocalMachine and restricts the identity directory/file ACL to SYSTEM and local Administrators.
- Does not submit or re-submit print jobs. Spooler execution and uncertain-output recovery remain blocked until payment authorisation and a durable local print queue are implemented.
