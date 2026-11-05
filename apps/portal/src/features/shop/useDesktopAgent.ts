import { useEffect, useRef, useState } from "react";
import type { Dispatch, MutableRefObject, SetStateAction } from "react";
import {
  ApiError, createAgentEnrollmentCode, loadShopData, revokeAgentDevice,
  type AgentDevice, type PrinterDevice
} from "../../api";
import { enrollLocalAgent, getLocalAgentStatus, isDesktopApp, type DesktopAgentStatus } from "../../desktop-bridge";

type AgentEnrollmentIssue = { code: string | null; message: string };
type UseDesktopAgentOptions = {
  accessToken: string;
  onSignOut: () => Promise<void>;
  confirmInApp: (message: string, title?: string, confirmLabel?: string) => Promise<boolean>;
  agentDevicesRef: MutableRefObject<AgentDevice[]>;
  setAgentDevices: Dispatch<SetStateAction<AgentDevice[]>>;
  setPrinters: Dispatch<SetStateAction<PrinterDevice[]>>;
  reload: () => Promise<void>;
  setError: Dispatch<SetStateAction<string>>;
  setToast: Dispatch<SetStateAction<string>>;
};

export function useDesktopAgent({
  accessToken,onSignOut,confirmInApp,agentDevicesRef,setAgentDevices,setPrinters,reload,setError,setToast
}:UseDesktopAgentOptions) {
  const desktopAgentSyncTimer = useRef<number | null>(null);
  const desktopAgentSyncInFlight = useRef(false);
  const desktopAgentSyncAttempts = useRef(0);
  const desktopConnectInFlight = useRef(false);
  const locallyAuthorizedAgentId = useRef<string | null>(null);
  const [localAgentStatus, setLocalAgentStatus] = useState<DesktopAgentStatus | null>(null);
  const [localAgentConflict, setLocalAgentConflict] = useState(false);
  const [localAgentRevoked, setLocalAgentRevoked] = useState(false);
  const [localAgentNeedsRepair, setLocalAgentNeedsRepair] = useState(false);
  const [localAgentEnrollmentIssue, setLocalAgentEnrollmentIssue] = useState<{ code: string | null; message: string } | null>(null);
  const localAgentEnrollmentIssueRef = useRef<{ code: string | null; message: string } | null>(null);
  const [localAgentOwnershipChecking, setLocalAgentOwnershipChecking] = useState(false);
  const [enrollmentCode, setEnrollmentCode] = useState<{ code: string; expiresUtc: string } | null>(null);
  const [agentBusy, setAgentBusy] = useState(false);
  async function generateAgentCode() {
    setAgentBusy(true);
    setError("");
    setEnrollmentCode(null);
    try {
      const code = await createAgentEnrollmentCode(accessToken);
      setEnrollmentCode(code);
      setToast("One-time pairing code generated. It expires in 10 minutes.");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not generate an agent pairing code.");
    } finally {
      setAgentBusy(false);
    }
  }

  async function connectLocalComputer(
    silent = false,
    knownAgents: AgentDevice[] = agentDevicesRef.current,
    allowPreviousShopReconnection = false
  ) {
    if (desktopConnectInFlight.current) return;
    desktopConnectInFlight.current = true;
    if (!silent) {
      setAgentBusy(true);
      setError("");
      localAgentEnrollmentIssueRef.current = null;
      setLocalAgentEnrollmentIssue(null);
    }
    try {
      const status = await getLocalAgentStatus();
      setLocalAgentStatus(status);
      setLocalAgentConflict(false);
      setLocalAgentOwnershipChecking(false);
      setLocalAgentRevoked(status.status === "REVOKED");
      setLocalAgentNeedsRepair(status.status === "IDENTITY_UNAVAILABLE");

      if (!status.serviceReachable) {
        if (!silent) setError("The SmartPrint background service is not responding. Restart Windows or repair SmartPrint, then retry.");
        return;
      }

      if (status.paired) {
        const currentShopDevice = knownAgents.find(agent =>
          !agent.revokedUtc && agent.id === status.agentDeviceId
        );
        const revokedCurrentShopDevice = knownAgents.find(agent =>
          agent.revokedUtc && agent.id === status.agentDeviceId
        );
        if (!currentShopDevice) {
          if (revokedCurrentShopDevice) {
            // This shop revoked this exact device. Only an explicit reconnect
            // action may create a fresh authorization; background sync never does.
            setLocalAgentRevoked(true);
            if (silent) return;
          } else {
            setLocalAgentConflict(true);
            if (!allowPreviousShopReconnection) {
              if (!silent) setError("This computer is still paired with another shop. Its owner must revoke it before you can connect it here.");
              return;
            }

            const confirmed = await confirmInApp(
              "Continue only after the previous shop owner has revoked this computer from that shop's Print Agent page. SmartPrint will verify the revocation with the server before changing this computer's connection.",
              "Reconnect this computer?",
              "Check and reconnect"
            );
            if (!confirmed) return;
          }
        } else {
          setLocalAgentRevoked(false);
          setToast("This computer is already connected to your shop.");
          return;
        }
      }

      const authorization = await createAgentEnrollmentCode(accessToken);
      const enrolled = await enrollLocalAgent(authorization.code);
      if (!enrolled.success) {
        const message = enrolled.message || "This computer could not be connected to the shop.";
        if (enrolled.code) throw new ApiError(message, 409, enrolled.code);
        throw new Error(message);
      }
      locallyAuthorizedAgentId.current = enrolled.agentDeviceId;
      let postEnrollmentStatus = status;
      try {
        // Read back the local listener state after enrollment. Failure here must
        // not undo a successful cloud pairing or interrupt the owner's workflow.
        postEnrollmentStatus = await getLocalAgentStatus();
      } catch {
        // Keep the last known listener status; the background health loop retries.
      }
      setLocalAgentStatus({
        serviceReachable: true,
        localApiReachable: postEnrollmentStatus.localApiReachable,
        apiBaseUrl: postEnrollmentStatus.apiBaseUrl,
        paired: true,
        agentDeviceId: enrolled.agentDeviceId,
        status: "CONNECTED"
      });
      setLocalAgentConflict(false);
      setLocalAgentRevoked(false);
      setLocalAgentNeedsRepair(false);
      localAgentEnrollmentIssueRef.current = null;
      setLocalAgentEnrollmentIssue(null);
      setLocalAgentOwnershipChecking(false);

      // Enrollment creates the AgentDevice in the cloud immediately. Refresh its
      // shop-scoped record so the UI recognizes this computer as connected on the
      // first run instead of waiting for the next manual reload.
      try {
        const refreshedSnapshot = await loadShopData(accessToken);
        agentDevicesRef.current = refreshedSnapshot.agentDevices;
        setAgentDevices(refreshedSnapshot.agentDevices);
        setPrinters(refreshedSnapshot.printers);
      } catch {
        // Keep the successful local enrollment state; the next normal refresh
        // will synchronize the cloud inventory without enrolling a second time.
      }

      setToast("This computer is connected. SmartPrint is finding its printers.");
      if (!silent) await reload();
    } catch (exception) {
      if (exception instanceof ApiError && exception.status === 401) {
        try { await onSignOut(); }
        catch (signOutError) { setError(signOutError instanceof Error ? signOutError.message : "Could not clear the secure desktop session."); }
      } else if (exception instanceof ApiError && exception.status === 409) {
        const issue = { code: exception.code, message: exception.message };
        localAgentEnrollmentIssueRef.current = issue;
        setLocalAgentEnrollmentIssue(issue);
        setLocalAgentConflict(exception.code === "agent_already_enrolled");
        setLocalAgentRevoked(false);
        setLocalAgentNeedsRepair(false);
        setLocalAgentOwnershipChecking(false);
        if (!silent) setError(exception.message);
      } else if (!silent) {
        const message = exception instanceof Error ? exception.message : "Could not connect this computer to the shop.";
        setError(message);
      }
    } finally {
      desktopConnectInFlight.current = false;
      if (!silent) setAgentBusy(false);
    }
  }

  function nextDesktopAgentRetryDelay(): number {
    const exponent = Math.min(desktopAgentSyncAttempts.current, 4);
    desktopAgentSyncAttempts.current += 1;
    return Math.min(2_000 * (2 ** exponent), 30_000);
  }

  function scheduleDesktopAgentSync(delayMs: number): void {
    if (!isDesktopApp()) return;
    if (desktopAgentSyncTimer.current !== null) {
      window.clearTimeout(desktopAgentSyncTimer.current);
    }
    desktopAgentSyncTimer.current = window.setTimeout(() => {
      desktopAgentSyncTimer.current = null;
      void synchronizeDesktopAgent();
    }, delayMs);
  }

  async function synchronizeDesktopAgent(): Promise<void> {
    if (!isDesktopApp() || desktopAgentSyncInFlight.current) return;
    desktopAgentSyncInFlight.current = true;
    try {
      const status = await getLocalAgentStatus();
      setLocalAgentStatus(status);

      if (!status.serviceReachable) {
        setLocalAgentConflict(false);
        setLocalAgentOwnershipChecking(false);
        scheduleDesktopAgentSync(nextDesktopAgentRetryDelay());
        return;
      }

      // A server-revoked identity is intentionally not auto-enrolled again.
      // The shop owner can reconnect it with one explicit click, without typing a code.
      if (status.status === "REVOKED") {
        setLocalAgentRevoked(true);
        setLocalAgentNeedsRepair(false);
        setLocalAgentConflict(false);
        setLocalAgentOwnershipChecking(false);
        scheduleDesktopAgentSync(60_000);
        return;
      }

      if (status.status === "IDENTITY_UNAVAILABLE") {
        // Don't create endless one-time enrollment codes while the old cloud
        // device may still be active. Let the owner review/revoke it first.
        setLocalAgentNeedsRepair(true);
        setLocalAgentRevoked(false);
        setLocalAgentConflict(false);
        setLocalAgentOwnershipChecking(false);
        scheduleDesktopAgentSync(60_000);
        return;
      }

      if (localAgentEnrollmentIssueRef.current) {
        // The API has already explained why enrollment cannot proceed. Do not
        // keep creating short-lived one-time authorizations in the background.
        setLocalAgentEnrollmentIssue(localAgentEnrollmentIssueRef.current);
        setLocalAgentConflict(localAgentEnrollmentIssueRef.current.code === "agent_already_enrolled");
        setLocalAgentOwnershipChecking(false);
        return;
      }

      setLocalAgentNeedsRepair(false);
      if (status.paired && status.agentDeviceId) {
        let deviceRecord = agentDevicesRef.current.find(agent =>
          agent.id === status.agentDeviceId
        );

        if (deviceRecord?.revokedUtc) {
          setLocalAgentRevoked(true);
          setLocalAgentConflict(false);
          setLocalAgentOwnershipChecking(false);
          scheduleDesktopAgentSync(60_000);
          return;
        }

        if (!deviceRecord) {
          // Verify cloud ownership before declaring another-shop pairing.
          setLocalAgentOwnershipChecking(true);
          try {
            const refreshedSnapshot = await loadShopData(accessToken);
            agentDevicesRef.current = refreshedSnapshot.agentDevices;
            setAgentDevices(refreshedSnapshot.agentDevices);
            setPrinters(refreshedSnapshot.printers);
            deviceRecord = refreshedSnapshot.agentDevices.find(agent =>
              agent.id === status.agentDeviceId
            );
          } catch {
            scheduleDesktopAgentSync(nextDesktopAgentRetryDelay());
            return;
          }
        }

        if (deviceRecord?.revokedUtc) {
          setLocalAgentRevoked(true);
          setLocalAgentConflict(false);
          setLocalAgentOwnershipChecking(false);
          scheduleDesktopAgentSync(60_000);
          return;
        }

        if (!deviceRecord && locallyAuthorizedAgentId.current === status.agentDeviceId) {
          // Enrollment succeeded but its shop record has not appeared yet.
          // Keep checking; do not mislabel it as another shop or re-enroll it.
          setLocalAgentRevoked(false);
          setLocalAgentConflict(false);
          setLocalAgentOwnershipChecking(true);
          scheduleDesktopAgentSync(nextDesktopAgentRetryDelay());
          return;
        }

        const belongsToCurrentShop = Boolean(deviceRecord && !deviceRecord.revokedUtc);
        setLocalAgentOwnershipChecking(false);
        setLocalAgentRevoked(false);
        setLocalAgentConflict(!belongsToCurrentShop);
        if (belongsToCurrentShop) {
          desktopAgentSyncAttempts.current = 0;
          scheduleDesktopAgentSync(15_000);
        } else {
          // Never move a computer across shops without the previous owner revoking it.
          scheduleDesktopAgentSync(60_000);
        }
        return;
      }

      setLocalAgentRevoked(false);
      setLocalAgentConflict(false);
      setLocalAgentOwnershipChecking(false);
      await connectLocalComputer(true, agentDevicesRef.current);
      const verifiedStatus = await getLocalAgentStatus();
      setLocalAgentStatus(verifiedStatus);

      if (verifiedStatus.status === "REVOKED") {
        setLocalAgentRevoked(true);
        setLocalAgentNeedsRepair(false);
        setLocalAgentConflict(false);
        setLocalAgentOwnershipChecking(false);
        scheduleDesktopAgentSync(60_000);
        return;
      }

      if (verifiedStatus.serviceReachable && verifiedStatus.paired && verifiedStatus.agentDeviceId) {
        const verifiedRecord = agentDevicesRef.current.find(agent =>
          agent.id === verifiedStatus.agentDeviceId
        );
        if (verifiedRecord?.revokedUtc) {
          setLocalAgentRevoked(true);
          setLocalAgentConflict(false);
          setLocalAgentOwnershipChecking(false);
          scheduleDesktopAgentSync(60_000);
          return;
        }
        if (verifiedRecord && !verifiedRecord.revokedUtc) {
          setLocalAgentRevoked(false);
          setLocalAgentOwnershipChecking(false);
          setLocalAgentConflict(false);
          desktopAgentSyncAttempts.current = 0;
          scheduleDesktopAgentSync(15_000);
          return;
        }
        if (locallyAuthorizedAgentId.current === verifiedStatus.agentDeviceId) {
          setLocalAgentRevoked(false);
          setLocalAgentConflict(false);
          setLocalAgentOwnershipChecking(true);
          scheduleDesktopAgentSync(nextDesktopAgentRetryDelay());
          return;
        }
        setLocalAgentRevoked(false);
        setLocalAgentConflict(true);
        setLocalAgentOwnershipChecking(false);
      }

      scheduleDesktopAgentSync(nextDesktopAgentRetryDelay());
    } catch {
      // Startup, network and temporary pipe failures recover without owner action.
      scheduleDesktopAgentSync(nextDesktopAgentRetryDelay());
    } finally {
      desktopAgentSyncInFlight.current = false;
    }
  }

  async function revokeAgent(agent: AgentDevice) {
    if (!(await confirmInApp(`Revoke the Print Agent on ${agent.deviceName}? It will no longer be able to report printers or connect to this shop.`, "Revoke Print Agent", "Revoke agent"))) return;
    setAgentBusy(true);
    setError("");
    try {
      await revokeAgentDevice(accessToken, agent.id);
      if (localAgentStatus?.agentDeviceId === agent.id) {
        setLocalAgentRevoked(true);
        setLocalAgentConflict(false);
        setLocalAgentOwnershipChecking(false);
      }
      setToast("Agent access revoked.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not revoke this agent.");
    } finally {
      setAgentBusy(false);
    }
  }
  useEffect(() => () => {
    if (desktopAgentSyncTimer.current !== null) {
      window.clearTimeout(desktopAgentSyncTimer.current);
      desktopAgentSyncTimer.current = null;
    }
  }, []);

  return {
    localAgentStatus,localAgentConflict,localAgentRevoked,localAgentNeedsRepair,
    localAgentEnrollmentIssue,localAgentOwnershipChecking,enrollmentCode,agentBusy,
    generateAgentCode,connectLocalComputer,revokeAgent,scheduleDesktopAgentSync
  };
}
