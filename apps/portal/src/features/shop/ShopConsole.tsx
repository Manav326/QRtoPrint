import { useEffect, useRef, useState } from "react";
import { useShopData } from "./useShopData";
import { Activity, ArrowRight, Check, Clock3, CreditCard, FileText, FolderOpen, LayoutDashboard, ChevronDown, LifeBuoy, LogOut, Menu, Monitor, Plus, Printer, QrCode, Settings, Image as ImageIcon, UserRound, ShieldCheck, Users, Wallet, X, PanelLeftClose, PanelLeftOpen, RotateCcw } from "lucide-react";
import PhotoStudio, { type PreparedPhotoSheet } from "../../PhotoStudio";
import { shopPathForSection, shopSectionFromPath } from "../../portal-routing";
import { ApiError, applyShopPrintBatchReview, approveShopPrintJob, assignShopPrintJobPrinter, confirmShopPrintOutput, dispatchShopPrintJob, createAgentEnrollmentCode, createPrinter, createStation, downloadShopDocument, replaceShopPhotoDocument, replaceShopPhotoDocumentLocally, getShopJobEvents, getShopPrintBatchJobs, loadShopData, logout, rejectShopPrintJob, rejectShopPayment, revokeAgentDevice, rotateStationCode, storeTokens, updatePrinterActive, updateShopPaymentSettings, updateShopPricing, updateShopPrintJobSettings, updateShopOwnerAccount, updateShopProfile, updateStationEnabled, verifyShopPayment, retryShopPdfExport, recordShopPrintRefund, previewShopPrintRefund, type ShopPayment, type ShopPaymentSettings, type ShopRefund, type AgentDevice, type AuthTokens, type PrinterDevice, type ShopJobEvent, type QrStation, type ShopPricingProfile, type ShopPrintJob, type ShopProfile, type ShopSubscriptionSummary, type CustomerPrintRequestSettings } from "../../api";
import { enrollLocalAgent, getLocalAgentStatus, isDesktopApp, setDesktopContentProtection, type DesktopAgentStatus } from "../../desktop-bridge";
import { readSidebarCollapsedPreference, saveSidebarCollapsedPreference } from "../../ui-preferences";
import OrderJourneyPage from "../../OrderJourneyPage";
import SubscriptionPage from "../subscription/SubscriptionPage";
import Status from "../../shared/ui/Status";
import PrintersPage from "./printers/PrintersPage";
import StationsPage from "./stations/StationsPage";
import ReportsPage from "./reports/ReportsPage";
import MetricCard from "../../shared/ui/MetricCard";
import CustomersPage from "./customers/CustomersPage";
import Modal from "../../shared/ui/Modal";
import PrintKindTag from "../../shared/ui/PrintKindTag";
import { formatMoney, prettyStatus } from "../../shared/lib/formatting";
import PricingPage from "./pricing/PricingPage";
import SettingsPage from "./settings/SettingsPage";
import PrintAgentPage from "./agent/PrintAgentPage";
import PaymentsPage from "./payments/PaymentsPage";
import PrintQueuePage from "./queue/PrintQueuePage";
import InfoTooltip from "../../shared/ui/InfoTooltip";
import { isPrinterAvailable } from "../../shared/lib/printers";
import { IST_TIME_ZONE, formatIstDateTime, istDateKey } from "../../date-time";

type Section = "Order Journey" | "Overview" | "Print Queue" | "Printers" | "QR Stations" | "Print Agent" | "Pricing" | "Subscription" | "Payments" | "Customers" | "Reports" | "Settings";
type Modal = "printer" | "station" | null;

const sections: { label: Section; icon: typeof LayoutDashboard }[] = [
  { label: "Order Journey", icon: Activity },
  { label: "Overview", icon: LayoutDashboard },
  { label: "Print Queue", icon: FileText },
  { label: "Printers", icon: Printer },
  { label: "QR Stations", icon: QrCode },
  { label: "Print Agent", icon: Monitor },
  { label: "Pricing", icon: Wallet },
  { label: "Subscription", icon: ShieldCheck },
  { label: "Payments", icon: CreditCard },
  { label: "Customers", icon: Users },
  { label: "Reports", icon: Activity },
  { label: "Settings", icon: Settings }
];

const sectionGroups: { label: string; items: Section[] }[] = [
  { label: "DAILY WORK", items: ["Order Journey", "Overview", "Print Queue", "Customers"] },
  { label: "DEVICES & CONNECTIVITY", items: ["Printers", "QR Stations", "Print Agent"] },
  { label: "BUSINESS", items: ["Pricing", "Payments"] },
  { label: "INSIGHTS", items: ["Reports"] }
];

export default function ShopConsole({ tokens, onSignOut, dark, onToggleTheme }: { tokens: AuthTokens; onSignOut: () => Promise<void>; dark: boolean; onToggleTheme: () => void }) {
  const [active, setActive] = useState<Section>(() => shopSectionFromPath(window.location.pathname));
  const {
    cloudStatus, profile, setProfile, printers, setPrinters, stations, setStations, pricing, setPricing,
    printJobs, setPrintJobs, journeyJobs, journeyPayments, journeyRefreshing,
    hasMoreQueueJobs, loadingOlderQueueJobs, queueApiOffset, queueSearch, agentDevices, setAgentDevices,
    agentDevicesRef, paymentSettings, setPaymentSettings, payments, refunds, subscription,
    apiConnectionState, setApiConnectionState, loading, setLoading, error, setError,
    reload, handleShopApiError, refreshPrintQueue, loadOlderQueueJobs, searchPrintQueue, refreshOrderJourney
  } = useShopData({
    accessToken: tokens.accessToken,
    onSignOut,
    active,
    onDesktopAgentSync: () => scheduleDesktopAgentSync(0)
  });

  useEffect(() => {
    const syncSectionFromPath = () => setActive(shopSectionFromPath(window.location.pathname));
    window.addEventListener("popstate", syncSectionFromPath);
    return () => window.removeEventListener("popstate", syncSectionFromPath);
  }, []);

  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(readSidebarCollapsedPreference);
  const [accountMenuOpen, setAccountMenuOpen] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);
  const [ownerAccountOpen, setOwnerAccountOpen] = useState(false);
  const accountMenuRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!accountMenuOpen) return;
    const closeOnOutsidePointer = (event: PointerEvent) => {
      if (!accountMenuRef.current?.contains(event.target as Node)) setAccountMenuOpen(false);
    };
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === "Escape") setAccountMenuOpen(false);
    };
    document.addEventListener("pointerdown", closeOnOutsidePointer);
    window.addEventListener("keydown", closeOnEscape);
    return () => {
      document.removeEventListener("pointerdown", closeOnOutsidePointer);
      window.removeEventListener("keydown", closeOnEscape);
    };
  }, [accountMenuOpen]);
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
  const [modal, setModal] = useState<Modal>(null);
  const [busy, setBusy] = useState(false);
  const [toast, setToast] = useState("");
  const [preview, setPreview] = useState<{ name: string; url: string; blob: Blob; mediaType: string; job: ShopPrintJob; readOnly: boolean; isSavedOutput?: boolean } | null>(null);
  const [focusShopPhotoEdit,setFocusShopPhotoEdit]=useState(false);
  const [shopPhotoStudio,setShopPhotoStudio]=useState<{
    source:File;
    job:ShopPrintJob;
    returnPreview:{name:string;blob:Blob;mediaType:string;job:ShopPrintJob;readOnly:boolean};
  }|null>(null);
  const [refundJob, setRefundJob] = useState<ShopPrintJob | null>(null);
  const [batchReviewJobs, setBatchReviewJobs] = useState<ShopPrintJob[] | null>(null);
  const [batchReviewDecisions, setBatchReviewDecisions] = useState<Record<string, boolean>>({});
  const [batchReviewRetry, setBatchReviewRetry] = useState(false);
  const [batchReviewRetryIds, setBatchReviewRetryIds] = useState<string[]>([]);
  const [batchReviewLoadingId, setBatchReviewLoadingId] = useState<string | null>(null);
  type ActionDialogState = { title: string; message: string; inputLabel?: string; confirmLabel: string; resolve: (value: string | null) => void };
  const [actionDialog, setActionDialog] = useState<ActionDialogState | null>(null);
  const [actionDialogValue, setActionDialogValue] = useState("");
  const [busyJobId, setBusyJobId] = useState<string | null>(null);
  const [busyResourceId, setBusyResourceId] = useState<string | null>(null);
  const [eventHistory, setEventHistory] = useState<{ job: ShopPrintJob; events: ShopJobEvent[] } | null>(null);

  function requestActionDialog(options: { title: string; message: string; inputLabel?: string; confirmLabel?: string }): Promise<string | null> {
    return new Promise(resolve => {
      setActionDialogValue("");
      setActionDialog({ ...options, confirmLabel: options.confirmLabel || "Confirm", resolve });
    });
  }

  async function confirmInApp(message: string, title = "Please confirm", confirmLabel = "Confirm"): Promise<boolean> {
    return (await requestActionDialog({ title, message, confirmLabel })) !== null;
  }

  async function promptInApp(message: string, title = "Enter details", inputLabel = "Reason"): Promise<string | null> {
    return requestActionDialog({ title, message, inputLabel, confirmLabel: "Continue" });
  }

  function closeActionDialog() {
    const current = actionDialog;
    setActionDialog(null);
    current?.resolve(null);
  }

  function submitActionDialog() {
    const current = actionDialog;
    setActionDialog(null);
    current?.resolve(actionDialogValue);
  }

  function toggleSidebarCollapsed() {
    setSidebarCollapsed(current => {
      const next = !current;
      saveSidebarCollapsedPreference(next);
      return next;
    });
  }



  useEffect(() => () => {
    if (desktopAgentSyncTimer.current !== null) {
      window.clearTimeout(desktopAgentSyncTimer.current);
      desktopAgentSyncTimer.current = null;
    }
  }, []);



  useEffect(() => {
    if (active !== "Order Journey") return;
    void refreshOrderJourney();
    const timer = window.setInterval(() => void refreshOrderJourney(), 15_000);
    return () => window.clearInterval(timer);
    // The refresh callback is scoped to the active shop session.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active, tokens.accessToken]);

  const activeAgentCount = agentDevices.filter(agent => !agent.revokedUtc).length;
  const onlineAgentIds = new Set(agentDevices.filter(agent =>
    !agent.revokedUtc && agent.status === "Online" && !!agent.lastSeenUtc &&
    Date.parse(agent.lastSeenUtc) > Date.now() - 2 * 60 * 1000
  ).map(agent => agent.id));
  const onlineAgentCount = onlineAgentIds.size;
  const usablePrinterCount = printers.filter(printer => isPrinterAvailable(printer, onlineAgentIds)).length;
  const offlinePrinterCount = printers.filter(printer =>
    printer.isActive && !isPrinterAvailable(printer, onlineAgentIds)
  ).length;
  const today = new Intl.DateTimeFormat("en-IN", { timeZone: IST_TIME_ZONE, weekday: "long", day: "numeric", month: "long" }).format(new Date());

  async function signOut() {
    try { await logout(tokens.accessToken); } catch { /* Local sign-out still clears the session. */ }
    try { await onSignOut(); }
    catch (exception) { setError(exception instanceof Error ? exception.message : "Could not clear the secure desktop session."); }
  }

  async function submitPrinter(values: {
    displayName: string; manufacturer: string; model: string; connectionType: string;
    connectionAddress?: string; queueName?: string;
  }) {
    setBusy(true);
    try {
      await createPrinter(tokens.accessToken, values);
      setModal(null);
      setToast("Printer added to your shop.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not add printer.");
    } finally {
      setBusy(false);
    }
  }

  async function submitStation(values: { displayName: string; printerDeviceId?: string }) {
    setBusy(true);
    try {
      const created = await createStation(tokens.accessToken, values);
      setStations(current => [...current, created]);
      setModal(null);
      setToast("QR station created. Save or print its QR code now.");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not create QR station.");
    } finally {
      setBusy(false);
    }
  }

  async function togglePrinter(printer: PrinterDevice) {
    if (busyResourceId) return;
    if (printer.isActive && !(await confirmInApp(
      `Disable ${printer.displayName}? New jobs can no longer be assigned to it. A Windows job already accepted by the spooler is not cancelled; an active job may prevent disabling until it is resolved.`,
      "Disable this printer?", "Disable printer"
    ))) return;
    setBusyResourceId(printer.id);
    setError("");
    try {
      await updatePrinterActive(tokens.accessToken, printer.id, !printer.isActive);
      setToast(printer.isActive ? "Printer disabled. Existing Windows output is not cancelled." : "Printer enabled for new assignments.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not update printer.");
    } finally {
      setBusyResourceId(null);
    }
  }

  async function toggleStation(station: QrStation) {
    if (busyResourceId) return;
    if (station.isEnabled && !(await confirmInApp(
      `Disable the QR station “${station.displayName}”? Customers will not be able to start new sessions from this QR until you enable it again. Previously started customer sessions are not recalled.`,
      "Disable this QR station?", "Disable station"
    ))) return;
    setBusyResourceId(station.id);
    setError("");
    try {
      const updated = await updateStationEnabled(tokens.accessToken, station.id, !station.isEnabled);
      setStations(current => current.map(item => item.id === station.id ? { ...item, isEnabled: updated.isEnabled } : item));
      setToast(updated.isEnabled ? "QR station enabled." : "QR station disabled. Existing customer sessions are unchanged.");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not update QR station.");
    } finally {
      setBusyResourceId(null);
    }
  }

  async function createFreshStationCode(station: QrStation) {
    if (busyResourceId) return;
    const confirmed = await confirmInApp("Generating a replacement QR will invalidate every previously printed sign for this station. Continue only when you intend to replace them.", "Replace this station's QR?", "Replace QR");
    if (!confirmed) return;
    setBusyResourceId(station.id);
    setError("");
    try {
      const result = await rotateStationCode(tokens.accessToken, station.id);
      setStations(current => current.map(item => item.id === station.id
        ? { ...item, publicUrl: result.publicUrl, publicCodeHint: result.publicCodeHint }
        : item));
      setToast("New QR code generated. The previous code is now invalid.");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not generate a QR code.");
    } finally {
      setBusyResourceId(null);
    }
  }

  async function generateAgentCode() {
    setAgentBusy(true);
    setError("");
    setEnrollmentCode(null);
    try {
      const code = await createAgentEnrollmentCode(tokens.accessToken);
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

      const authorization = await createAgentEnrollmentCode(tokens.accessToken);
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
        const refreshedSnapshot = await loadShopData(tokens.accessToken);
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
            const refreshedSnapshot = await loadShopData(tokens.accessToken);
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
      await revokeAgentDevice(tokens.accessToken, agent.id);
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



  async function assignPrinter(job: ShopPrintJob, printerDeviceId: string) {
    if (!printerDeviceId || printerDeviceId === job.printerDeviceId) return;
    setBusyJobId(job.id);
    setError("");
    try {
      const assignment=await assignShopPrintJobPrinter(tokens.accessToken, job.id, printerDeviceId);
      setToast(`${assignment.printerName} assigned to ${assignment.assignedJobCount} file(s) in this batch. The paired computer can now receive the documents.`);
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not assign this printer.");
    } finally {
      setBusyJobId(null);
    }
  }


  async function updatePrintJobSettings(job:ShopPrintJob,settings:CustomerPrintRequestSettings) {
    setBusyJobId(job.id);
    setError("");
    try {
      const revised=await updateShopPrintJobSettings(tokens.accessToken,job.id,settings);
      setToast(`Revised quote ${formatMoney(revised.quotedAmountMinor,revised.currency)} sent to the customer for approval. Printing remains paused until approval and release.`);
      await reload();
    } catch(exception) {
      setError(exception instanceof Error?exception.message:"Could not revise this print request.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function dispatchPrintJob(job: ShopPrintJob) {
    const pdfExport = job.outputMode === "PDF_EXPORT";
    const ok = await confirmInApp(pdfExport
      ? `Save a PDF for ${job.jobNumber} in the shop's QRtoPrint Exports folder? No Windows printer dialog will be opened.`
      : `Start printing ${job.jobNumber} on ${job.printerName || "the assigned printer"}? The document will be submitted once to Windows. Spooler acceptance is not proof of paper output.`, pdfExport ? "Save PDF export?" : "Start printing?", pdfExport ? "Save PDF" : "Start printing");
    if (!ok) return;
    setBusyJobId(job.id); setError("");
    try {
      await dispatchShopPrintJob(tokens.accessToken, job.id);
      setToast(pdfExport
        ? "PDF export authorised. The Windows Agent will create the PDF in the shop's export folder."
        : "Print authorised. The Windows agent will submit this job once.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not start printing.");
    } finally { setBusyJobId(null); }
  }

  async function retryPdfExport(job: ShopPrintJob) {
    const ok = await confirmInApp(
      `Confirm that no PDF file was saved for ${job.jobNumber}? The Agent will create the PDF in the shop's QRtoPrint Exports folder. Do not continue if a file from this job already exists.`,
      "Retry PDF export?", "Retry export"
    );
    if (!ok) return;
    setBusyJobId(job.id);
    setError("");
    try {
      await retryShopPdfExport(tokens.accessToken, job.id);
      await dispatchShopPrintJob(tokens.accessToken, job.id);
      setToast("PDF export retry authorised. The Windows Agent will save the PDF locally.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not retry this PDF export.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function confirmPrintedOutput(job: ShopPrintJob) {
    const ok = await confirmInApp(`Have you physically checked all requested pages for ${job.jobNumber}? Only confirm after checking the actual paper output.`, "Confirm paper output", "Confirm output");
    if (!ok) return;
    setBusyJobId(job.id); setError("");
    try {
      await confirmShopPrintOutput(tokens.accessToken, job.id);
      setToast("Physical output confirmed by the shop operator.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not confirm physical output.");
    } finally { setBusyJobId(null); }
  }

  async function savePaymentSettings(values: {
    upiEnabled: boolean; upiId: string | null; payeeName: string | null; cashEnabled: boolean; instantPaymentEnabled: boolean;
  }) {
    setBusy(true);
    setError("");
    try {
      const updated = await updateShopPaymentSettings(tokens.accessToken, values);
      setPaymentSettings(updated);
      setToast("Shop payment settings saved. Customer print revenue continues to go directly to your shop.");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not save shop payment settings.");
    } finally {
      setBusy(false);
    }
  }

  async function verifyPayment(payment: ShopPayment) {
    const isBatch = Boolean(payment.batchPaymentId);
    const confirmation = payment.method === "UPI"
      ? isBatch
        ? `Have you independently confirmed the full batch UPI amount in your shop's bank/UPI app? Reference: ${payment.paymentReference || "not supplied"}`
        : `Have you independently confirmed this UPI transaction in your shop's bank/UPI app? Reference: ${payment.paymentReference || "not supplied"}`
      : isBatch
        ? "Have you physically received the full cash amount for this batch?"
        : "Have you physically received the cash for this print job?";
    if (!(await confirmInApp(confirmation, "Verify payment received", isBatch ? "Verify batch payment" : "Verify payment"))) return;

    setBusyJobId(payment.printJobId);
    setError("");
    try {
      await verifyShopPayment(tokens.accessToken, payment.id);
      setToast(isBatch
        ? "Batch payment verified. Every accepted file is now authorised; assign and dispatch each file through the print queue."
        : "Payment verified. The job is paid/authorised; printing still requires printer assignment and an explicit dispatch.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not verify this payment.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function rejectPayment(payment: ShopPayment) {
    const cancellationWarning = payment.method === "UPI" &&
      ["AWAITING_CUSTOMER_PAYMENT","CUSTOMER_REPORTED"].includes(payment.status)
      ? "Before rejecting this UPI attempt, check your bank/UPI ledger against the full amount and the customer's reference. A submitted or missing UTR does not prove that money was or was not received. If the credit is present, verify this attempt instead of rejecting it.\n\n"
      : "";
    const reason = await promptInApp(
      cancellationWarning + (payment.batchPaymentId
        ? "Explain why this full batch payment is being rejected."
        : "Explain why this payment report is being rejected."),
      payment.batchPaymentId ? "Reject batch payment" : "Reject payment report",
      "Rejection reason"
    );
    if (reason === null) return;
    if (reason.trim().length < 3) {
      setError("A rejection reason of at least 3 characters is required.");
      return;
    }

    setBusyJobId(payment.printJobId);
    setError("");
    try {
      await rejectShopPayment(tokens.accessToken, payment.id, reason.trim());
      setToast(payment.batchPaymentId
        ? "Batch payment rejected. The customer can retry checkout for the accepted files."
        : "Payment report rejected. The customer can choose a new payment attempt.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not reject this payment.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function saveProfile(values: { displayName: string; logoUrl: string | null; accentColor: string }) {
    setBusy(true);
    try {
      await updateShopProfile(tokens.accessToken, values);
      setToast("Shop identity updated.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not update shop identity.");
    } finally {
      setBusy(false);
    }
  }

  async function saveOwnerAccount(values: { ownerName: string; email: string; currentPassword?: string }) {
    setBusy(true);
    try {
      const updated = await updateShopOwnerAccount(tokens.accessToken, values);
      setProfile(current => current ? {
        ...current,
        ownerName: updated.ownerName,
        ownerEmail: updated.ownerEmail
      } : current);
      setOwnerAccountOpen(false);
      setToast(updated.emailChanged
        ? "Personal details saved. Your new email is now your sign-in address."
        : "Personal details updated.");
    } finally {
      setBusy(false);
    }
  }

  async function savePricing(values: {
    monoPerSideMinor: number;
    colourPerSideMinor: number;
    monoDuplexPerSheetMinor: number;
    colourDuplexPerSheetMinor: number;
    minimumChargeMinor: number;
    photo35x45Minor:number; photo25x35Minor:number; photo2x2Minor:number; photo4x6Minor:number;
  }) {
    setBusy(true);
    try {
      const updated = await updateShopPricing(tokens.accessToken, values);
      setPricing(updated);
      setToast("Print rate card saved. New quotes will use this pricing version.");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not save print prices.");
    } finally {
      setBusy(false);
    }
  }

  async function previewJob(
    job: ShopPrintJob,
    readOnly = false,
    variant: "original" | "output" = "original"
  ) {
    setBusyJobId(job.id);
    setError("");
    try {
      const blob = await downloadShopDocument(tokens.accessToken, job.id, variant);
      const isSavedOutput = variant === "output";
      const supportedTypes = new Set(["application/pdf", "image/jpeg", "image/png"]);
      const storedType=(job.document.mediaType||"").split(";")[0].trim().toLowerCase();
      const responseType = (blob.type || "").split(";")[0].trim().toLowerCase();
      const extension = job.document.originalFileName.toLowerCase().split(".").pop();
      const extensionType = extension === "pdf" ? "application/pdf" : extension === "jpg" || extension === "jpeg" ? "image/jpeg" : extension === "png" ? "image/png" : "";
      // Trust the persisted MIME type, then response headers, then the validated file extension.
      const mediaType = isSavedOutput ? "application/pdf" : supportedTypes.has(storedType) ? storedType : supportedTypes.has(responseType) ? responseType : extensionType;
      const previewBlob = mediaType && blob.type !== mediaType ? new Blob([blob], { type: mediaType }) : blob;
      const url = URL.createObjectURL(previewBlob);
      setPreview({
        name: isSavedOutput ? job.outputFileName || `${job.jobNumber}_SmartPrint.pdf` : job.document.originalFileName,
        url,
        blob: previewBlob,
        mediaType,
        job,
        readOnly: readOnly || isSavedOutput,
        isSavedOutput
      });
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not open the document.");
    } finally {
      setBusyJobId(null);
    }
  }

  function openShopPhotoStudio(){
    if(!preview||preview.readOnly||preview.mediaType!=="image/jpeg"&&preview.mediaType!=="image/png")return;
    const source=new File([preview.blob],preview.name,{type:preview.mediaType,lastModified:Date.now()});
    const returnPreview={name:preview.name,blob:preview.blob,mediaType:preview.mediaType,job:preview.job,readOnly:preview.readOnly};
    URL.revokeObjectURL(preview.url);
    setPreview(null);
    setFocusShopPhotoEdit(false);
    setShopPhotoStudio({source,job:preview.job,returnPreview});
  }

  function cancelShopPhotoStudio(){
    if(!shopPhotoStudio){setShopPhotoStudio(null);return;}
    const restored=shopPhotoStudio.returnPreview;
    setPreview({...restored,url:URL.createObjectURL(restored.blob)});
    setFocusShopPhotoEdit(true);
    setShopPhotoStudio(null);
  }

  async function applyShopPhotoStudio(sheets:PreparedPhotoSheet[]){
    const editor=shopPhotoStudio;
    if(!editor||sheets.length!==1)throw new Error("Prepare one multi-page A4 photo PDF before saving this job.");
    const sheet=sheets[0];
    setBusyJobId(editor.job.id);
    try{
      if(editor.job.storageMode==="LOCAL_AGENT") {
        await replaceShopPhotoDocumentLocally(tokens.accessToken,editor.job.id,sheet.file,sheet.sizeCode,sheet.photoCount,sheet.photosPerPage);
      } else {
        await replaceShopPhotoDocument(tokens.accessToken,editor.job.id,sheet.file,sheet.sizeCode,sheet.photoCount,sheet.photosPerPage);
      }
    }catch(exception){
      setError(exception instanceof Error?exception.message:"Could not save the enhanced photo sheet.");
      throw exception;
    }

    setShopPhotoStudio(null);
    setFocusShopPhotoEdit(false);
    setToast("The enhanced multi-page A4 photo PDF is saved to the same print job and its photo price has been recalculated.");
    try{
      await reload();
    }catch{
      setError("The A4 photo PDF was saved to the existing print job, but the queue could not refresh. Refresh the queue before approving or printing.");
    }finally{
      setBusyJobId(null);
    }
  }

  async function viewJobHistory(job: ShopPrintJob) {
    setBusyJobId(job.id);
    setError("");
    try {
      const events = await getShopJobEvents(tokens.accessToken, job.id);
      setEventHistory({ job, events });
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not load job history.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function approveJob(job: ShopPrintJob) {
    if (!(await confirmInApp(`Approve ${job.jobNumber} for payment? It will not be sent to a printer yet.`, "Accept this file", "Accept file"))) return;
    setBusyJobId(job.id);
    setError("");
    try {
      const result = await approveShopPrintJob(tokens.accessToken, job.id);
      setToast(result.nextStep || "Shop approval saved. This job is awaiting verified payment.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not approve this job.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function rejectJob(job: ShopPrintJob) {
    if (!(await confirmInApp(`Reject ${job.jobNumber}? The customer will see that the shop declined the request.`, "Reject this file?", "Reject file"))) return;
    setBusyJobId(job.id);
    setError("");
    try {
      await rejectShopPrintJob(tokens.accessToken, job.id);
      setToast("The print request was rejected.");
      await reload();
      if (preview?.job.id === job.id) {
        URL.revokeObjectURL(preview.url);
        setPreview(null);
      }
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not reject this job.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function applyBatchReview(batchJobs: ShopPrintJob[], acceptedJobIds: string[], pdfBrandingEnabled: boolean) {
    const batchId=batchJobs.find(job=>job.batchId)?.batchId;
    if(!batchId||batchJobs.length===0)return;
    const accepted=new Set(acceptedJobIds);
    const acceptCount=batchJobs.filter(job=>accepted.has(job.id)).length;
    const rejectCount=batchJobs.length-acceptCount;
    if (!(await confirmInApp(
      `Accept ${acceptCount} file(s) and reject ${rejectCount} file(s)? Previously rejected files you tick will be restored only if no payment has started. Expired quotes must be recalculated before checkout. PDF footer branding will be ${pdfBrandingEnabled ? "enabled on safe blank margins" : "off"} for this batch. Original uploads and normal paper prints remain unchanged. The decisions are applied together.`,
      "Confirm batch review",
      "Apply review"
    ))) return;

    setBusyJobId("batch-review");
    setError("");
    try {
      const result=await applyShopPrintBatchReview(
        tokens.accessToken,
        batchId,
        batchJobs.map(job=>job.id),
        acceptedJobIds,
        pdfBrandingEnabled
      );
      await reload();
      setBatchReviewJobs(null);
      setBatchReviewRetry(false);
      setBatchReviewRetryIds(current=>current.filter(id=>!batchJobs.some(job=>job.id===id)));
      setBatchReviewDecisions(current=>{
        const next={...current};
        for(const job of batchJobs)delete next[job.id];
        return next;
      });
      setToast(result.quoteRefreshRequiredCount>0
        ?`Batch review saved: ${acceptCount} accepted and ${rejectCount} rejected. ${result.quoteRefreshRequiredCount} accepted file(s) need a fresh quote before checkout.`
        :`Batch review saved: ${acceptCount} accepted and ${rejectCount} rejected. All accepted files remain part of one checkout.`);
    } catch(exception) {
      await reload().catch(()=>undefined);
      setError(exception instanceof Error?exception.message:"Could not apply this batch review. The decisions were not changed; refresh and retry the same review.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function recordFileRefund(job: ShopPrintJob, values: { refundMethod: "UPI" | "CASH"; refundReference: string | null; reason: string; rejectedPageNumbers: number[]; idempotencyKey: string }) {
    setBusyJobId(job.id);
    setError("");
    try {
      const result = await recordShopPrintRefund(tokens.accessToken, job.id, {
        ...values, confirmedSent: true
      });
      setToast(`Refund of ${formatMoney(result.amountMinor, result.currency)} recorded for ${job.document.originalFileName}. The return was logged under the signed-in shopkeeper.`);
      setRefundJob(null);
      if (preview?.job.id === job.id) {
        URL.revokeObjectURL(preview.url);
        setPreview(null);
      }
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not record this refund.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function openBatchReview(batchId: string) {
    setBatchReviewLoadingId(batchId);
    setError("");
    try {
      // Always load the complete batch from the tenant-scoped API. The queue
      // may currently be filtered or paginated, so its visible rows are not
      // a safe source for per-file acceptance/rejection decisions.
      const allBatchJobs = await getShopPrintBatchJobs(tokens.accessToken, batchId);
      const reviewableStatuses=new Set(["AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT","PAYMENT_REJECTED","REJECTED"]);
      const waiting = allBatchJobs.filter(job => reviewableStatuses.has(job.status));
      if (waiting.length === 0) {
        setBatchReviewJobs(null);
        setToast("This batch has no files eligible for pre-checkout review.");
        return;
      }
      setBatchReviewDecisions(current => {
        const next = { ...current };
        for (const job of waiting) {
          if (next[job.id] === undefined) next[job.id] = job.status !== "REJECTED";
        }
        return next;
      });
      setBatchReviewRetry(waiting.some(job => batchReviewRetryIds.includes(job.id)));
      setBatchReviewJobs(waiting);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not load every file in this batch. No review decisions were changed.");
    } finally {
      setBatchReviewLoadingId(current => current === batchId ? null : current);
    }
  }

  const navAction = (section: Section) => {
    const nextPath = shopPathForSection(section);
    if (window.location.pathname !== nextPath) {
      window.history.pushState({}, "", nextPath);
    }
    setActive(section);
    setSidebarOpen(false);
    setAccountMenuOpen(false);
    setError("");
  };

  const activePageTitle = active === "Settings" ? "Shop profile" : active;

  if (loading && !profile) {
    return <div className={dark ? "app-shell dark boot-screen" : "app-shell light boot-screen"}><div className="boot-card"><div className="brand-mark"><QrCode size={22}/></div><div className="boot-title">Preparing your workspace</div><div className="boot-subtitle">Connecting securely to your shop…</div><div className="button-spinner boot-spinner"/></div></div>;
  }

  return (
    <div className={`${dark ? "app-shell dark" : "app-shell light"}${sidebarCollapsed ? " sidebar-collapsed" : ""}`}>
      <aside className={sidebarOpen ? "sidebar sidebar-open" : "sidebar"}>
        <div className="brand-lockup">
          <div className="brand-mark"><QrCode size={21} strokeWidth={2.4}/></div>
          <div><div className="brand-name">smart<span>print</span></div><div className="brand-caption">BY QUANTAEDGE</div></div>
          <button className="icon-button sidebar-close" aria-label="Close navigation" onClick={() => setSidebarOpen(false)}><X size={18}/></button>
        </div>
        <div className="workspace-switcher">
          <div className="shop-avatar">{(profile?.displayName || "S").slice(0,1).toUpperCase()}</div>
          <div className="workspace-copy"><strong>{profile?.displayName || "Your shop"}</strong><span>{profile?.businessName || "Shop workspace"}</span></div>
          <span className="tenant-status-dot" title="Tenant workspace active"/>
        </div>
        <nav className="primary-nav" aria-label="Shop navigation">
          {sectionGroups.map(({ label: groupLabel, items }) => (
            <div className="nav-group" key={groupLabel}>
              <div className="nav-label">{groupLabel}</div>
              <div className="nav-group-items">
                {items.map(label => {
                  const section = sections.find(item => item.label === label);
                  if (!section) return null;
                  const Icon = section.icon;
                  return (
                    <button key={label} title={label} aria-label={label}
                      className={active === label ? "nav-item active" : "nav-item"}
                      onClick={() => navAction(label)}>
                      <Icon size={18} strokeWidth={1.8}/><span>{label}</span>
                    </button>
                  );
                })}
              </div>
            </div>
          ))}
        </nav>
      </aside>
      {sidebarOpen && <button className="mobile-scrim" aria-label="Close navigation overlay" onClick={() => setSidebarOpen(false)}/>}

      <main className="main-panel">
        <header className="topbar">
          <div className="topbar-left">
            <button className="icon-button sidebar-toggle" aria-label={sidebarCollapsed ? "Expand navigation" : "Collapse navigation"} title={sidebarCollapsed ? "Expand navigation" : "Collapse navigation"} onClick={toggleSidebarCollapsed}>{sidebarCollapsed ? <PanelLeftOpen size={18}/> : <PanelLeftClose size={18}/>}</button>
            <button className="icon-button mobile-menu" aria-label="Open navigation" onClick={() => setSidebarOpen(true)}><Menu size={20}/></button>
            <div className="breadcrumb"><span>Shop workspace</span><span className="crumb-slash">/</span><strong>{activePageTitle}</strong></div>
          </div>
          <div className="topbar-actions">
            <div className="connection-pill" role="status" aria-live="polite" title={cloudStatus === "connected" ? "The latest shop data refresh succeeded." : cloudStatus === "checking" ? "Checking your shop connection." : "The latest shop data refresh could not reach the cloud service."}>
              <span className="live-dot" style={{ opacity: cloudStatus === "unavailable" ? 0.45 : 1 }}/>
              <span>{cloudStatus === "connected" ? "Cloud connected" : cloudStatus === "checking" ? "Checking cloud" : "Cloud unavailable"}</span>
            </div>
            <ThemeToggleButton dark={dark} onToggle={onToggleTheme} className="theme-toggle-compact"/>
            <div className="service-status-row" aria-label="Live shop system status">
              <span className={"service-status "+apiConnectionState} title="API connection"><i/>API {apiConnectionState === "connected" ? "Connected" : apiConnectionState === "degraded" ? "Degraded" : apiConnectionState === "offline" ? "Offline" : "Checking"}</span>
              <span className={"service-status "+(onlineAgentCount > 0 ? "connected" : activeAgentCount > 0 ? "offline" : "checking")} title="Windows Agent heartbeat"><i/>Agent {onlineAgentCount > 0 ? onlineAgentCount+" online" : activeAgentCount > 0 ? "Offline" : "Not paired"}</span>
              <span className={"service-status "+(usablePrinterCount > 0 ? "connected" : offlinePrinterCount > 0 ? "offline" : "checking")} title="Active printer availability"><i/>Printers {usablePrinterCount > 0 ? usablePrinterCount+" available" : offlinePrinterCount > 0 ? offlinePrinterCount+" unavailable" : "Unavailable"}</span>
            </div>
            <div className="topbar-divider"/>
            <span className="topbar-product-tag"><span className="mode-indicator"/> {isDesktopApp() ? "Desktop workspace" : "Shop console"}</span>
            <div className="account-menu" ref={accountMenuRef}>
              <button type="button" className="account-menu-trigger"
                aria-label={`Account options for ${profile?.ownerName || "shop owner"}`}
                aria-expanded={accountMenuOpen} aria-controls="shop-account-menu"
                title="Account and shop settings"
                onClick={() => setAccountMenuOpen(value => !value)}>
                <span className="account-avatar">{initials(profile?.ownerName || "Shop owner")}</span>
                <span className="account-menu-trigger-copy">
                  <strong>{profile?.ownerName || "Shop owner"}</strong>
                  <small>Account</small>
                </span>
                <ChevronDown size={14} aria-hidden="true"/>
              </button>
              {accountMenuOpen && (
                <div className="account-menu-popover" id="shop-account-menu" aria-label="Account options">
                  <div className="account-menu-heading">
                    <span className="account-avatar">{initials(profile?.ownerName || "Shop owner")}</span>
                    <div>
                      <strong>{profile?.ownerName || "Shop owner"}</strong>
                      <span>{profile?.businessName || "Your shop"}</span>
                    </div>
                  </div>
                  <div className="account-menu-divider"/>
                  <button type="button" className="account-menu-item" onClick={() => { setAccountMenuOpen(false); setOwnerAccountOpen(true); }}>
                    <UserRound size={16}/>Personal details
                  </button>
                  <button type="button" className="account-menu-item" onClick={() => navAction("Settings")}>
                    <Settings size={16}/>Shop profile &amp; branding
                  </button>
                  <button type="button" className="account-menu-item" onClick={() => navAction("Subscription")}>
                    <ShieldCheck size={16}/>Trial &amp; subscription
                  </button>
                  <button type="button" className="account-menu-item" onClick={() => { setAccountMenuOpen(false); setHelpOpen(true); }}>
                    <LifeBuoy size={16}/>Help &amp; support
                  </button>
                  <div className="account-menu-divider"/>
                  <button type="button" className="account-menu-item account-menu-signout" onClick={() => { setAccountMenuOpen(false); void signOut(); }}>
                    <LogOut size={16}/>Sign out
                  </button>
                </div>
              )}
            </div>
          </div>
        </header>

        <div className="content-wrap">
          <div className="page-heading">
            <div><div className="eyebrow"><span className="eyebrow-line"/>SHOP WORKSPACE</div><h1>{active === "Overview" ? `Welcome, ${(profile?.ownerName || "there").split(" ")[0]}` : activePageTitle}</h1><p>{active === "Overview" ? "Your shop setup and operations, all in one place." : pageDescription(active)}</p></div>
            <div className="heading-actions"><span className="date-chip"><Clock3 size={15}/>{today}</span>{active === "Printers" && <button className="primary-button" onClick={() => setModal("printer")}><Plus size={16}/> Add printer</button>}{active === "QR Stations" && <button className="primary-button" onClick={() => setModal("station")}><Plus size={16}/> Create QR station</button>}</div>
          </div>

          {error && <div className="inline-error" role="alert"><span>{error}</span><button onClick={() => setError("")} aria-label="Dismiss error"><X size={15}/></button></div>}
          {toast && <div className="inline-success" role="status"><Check size={15}/><span>{toast}</span><button onClick={() => setToast("")} aria-label="Dismiss message"><X size={15}/></button></div>}

          {active === "Order Journey" && <OrderJourneyPage
            jobs={journeyJobs}
            payments={journeyPayments}
            printers={printers}
            agents={agentDevices}
            refreshing={journeyRefreshing}
            onRefresh={() => void refreshOrderJourney()}
            onNavigate={navAction}
            onReviewBatch={openBatchReview}
            onVerifyPayment={verifyPayment}
          />}
          {active === "Overview" && <Overview profile={profile} printers={printers} stations={stations} printJobs={printJobs} pricing={pricing} agentDevices={agentDevices} paymentSettings={paymentSettings} onNavigate={navAction}/>}
          {active === "Printers" && <PrintersPage printers={printers} busyResourceId={busyResourceId} onAdd={() => setModal("printer")} onToggle={togglePrinter}/>}
          {active === "QR Stations" && <StationsPage stations={stations} profile={profile} busyResourceId={busyResourceId} onAdd={() => setModal("station")} onToggle={toggleStation} onGenerate={createFreshStationCode} onCopyLink={value=>void copyText(value)}/>}
          {active === "Print Agent" && <PrintAgentPage agents={agentDevices} enrollmentCode={enrollmentCode} busy={agentBusy} onGenerate={generateAgentCode} onRevoke={revokeAgent} desktop={isDesktopApp()} localAgentStatus={localAgentStatus} localAgentConflict={localAgentConflict} localAgentRevoked={localAgentRevoked} localAgentNeedsRepair={localAgentNeedsRepair} localAgentEnrollmentIssue={localAgentEnrollmentIssue} localAgentOwnershipChecking={localAgentOwnershipChecking} onConnect={()=>void connectLocalComputer()} onReconnectAfterRevocation={()=>void connectLocalComputer(false,agentDevices,true)} onCopyCode={value=>void copyText(value)}/>}
          {active === "Pricing" && pricing && <PricingPage pricing={pricing} onSave={savePricing} busy={busy}/>}
          {active === "Subscription" && subscription && <SubscriptionPage summary={subscription}/>}
          {active === "Payments" && paymentSettings && <PaymentsPage settings={paymentSettings} payments={payments} refunds={refunds} onSave={savePaymentSettings} onVerify={verifyPayment} onReject={rejectPayment} busy={busy} busyJobId={busyJobId}/>}
          {active === "Settings" && profile && <SettingsPage profile={profile} onSave={saveProfile} busy={busy}/>}
          {active === "Print Queue" && <PrintQueuePage jobs={printJobs} printers={printers} agentDevices={agentDevices} searchQuery={queueSearch} loadingBatchId={batchReviewLoadingId} onSearch={searchPrintQueue} onPreview={previewJob} onPreviewOutput={job=>void previewJob(job,true,"output")} onApprove={approveJob} onReject={rejectJob} onRefund={job=>setRefundJob(job)} onReviewBatch={openBatchReview} onHistory={viewJobHistory} onAssignPrinter={assignPrinter} onUpdateSettings={updatePrintJobSettings} onDispatch={dispatchPrintJob} onConfirmOutput={confirmPrintedOutput} onRetryPdfExport={retryPdfExport} onRefresh={refreshPrintQueue} onLoadOlder={loadOlderQueueJobs} hasMoreJobs={hasMoreQueueJobs} loadingOlderJobs={loadingOlderQueueJobs} busyJobId={busyJobId}/>} 
          {active === "Customers" && <CustomersPage token={tokens.accessToken}/>}
          {active === "Reports" && <ReportsPage token={tokens.accessToken}/>} 

          <footer className="app-footer"><span>© 2026 SmartPrint by QuantaEdge</span><span><span className="footer-status"/> API session authenticated</span><span>Made for print shops, with care.</span></footer>
        </div>
      </main>

      {ownerAccountOpen && profile && <ShopOwnerAccountModal profile={profile} busy={busy} onClose={() => setOwnerAccountOpen(false)} onSave={saveOwnerAccount}/>}
      {helpOpen && <HelpSupportModal onClose={() => setHelpOpen(false)} onNavigate={section => { setHelpOpen(false); navAction(section); }}/>}
      {modal === "printer" && <PrinterModal busy={busy} onClose={() => setModal(null)} onSubmit={submitPrinter}/>}
      {modal === "station" && <StationModal busy={busy} printers={printers.filter(printer => printer.isActive)} onClose={() => setModal(null)} onSubmit={submitStation}/>}
            {batchReviewJobs && <BatchReviewModal jobs={batchReviewJobs} busy={busyJobId==="batch-review"} decisions={batchReviewDecisions} retrying={batchReviewRetry} onDecision={(jobId,accepted)=>setBatchReviewDecisions(current=>({...current,[jobId]:accepted}))} onClose={()=>setBatchReviewJobs(null)} onPreview={job=>void previewJob(job,true)} onApply={(acceptedIds,pdfBrandingEnabled)=>void applyBatchReview(batchReviewJobs,acceptedIds,pdfBrandingEnabled)}/>} 
      {preview && <DocumentPreviewModal name={preview.name} url={preview.url} mediaType={preview.mediaType} job={preview.job} readOnly={preview.readOnly} isSavedOutput={preview.isSavedOutput} focusEditOnMount={focusShopPhotoEdit} onEditPhoto={preview.mediaType.startsWith("image/")&&(preview.job.storageMode==="CLOUD"||preview.job.storageMode==="LOCAL_AGENT")&&preview.job.status==="AWAITING_SHOP_APPROVAL"&&[pricing?.photo35x45Minor,pricing?.photo25x35Minor,pricing?.photo2x2Minor,pricing?.photo4x6Minor].some(rate=>(rate??0)>0)?openShopPhotoStudio:undefined} onApprove={()=>void approveJob(preview.job)} onReject={()=>void rejectJob(preview.job)} onRefund={()=>setRefundJob(preview.job)} onClose={() => { URL.revokeObjectURL(preview.url); setPreview(null); setFocusShopPhotoEdit(false); }}/>} 
      {shopPhotoStudio && <PhotoStudio source={shopPhotoStudio.source} mode="shop" dark={dark} photoPrices={{"35x45":pricing?.photo35x45Minor??0,"25x35":pricing?.photo25x35Minor??0,"2x2":pricing?.photo2x2Minor??0,"4x6":pricing?.photo4x6Minor??0}} onCancel={cancelShopPhotoStudio} onApply={applyShopPhotoStudio}/>} 
      {eventHistory && <JobHistoryModal job={eventHistory.job} events={eventHistory.events} onClose={() => setEventHistory(null)}/>}
      {refundJob && <RefundModal job={refundJob} token={tokens.accessToken} busy={busyJobId===refundJob.id} onClose={()=>setRefundJob(null)} onSubmit={values=>void recordFileRefund(refundJob,values)}/>} 
      {actionDialog && <Modal title={actionDialog.title} subtitle="This action will be recorded against your shop account." onClose={closeActionDialog}>
        <div className="in-app-dialog-body"><p>{actionDialog.message}</p>{actionDialog.inputLabel&&<label className="dialog-input-label">{actionDialog.inputLabel}<textarea autoFocus rows={3} maxLength={300} value={actionDialogValue} onChange={event=>setActionDialogValue(event.target.value)} placeholder="Enter at least 3 characters"/></label>}</div>
        <div className="modal-actions"><button className="secondary-button" onClick={closeActionDialog}>Cancel</button><button className="primary-button" disabled={!!actionDialog.inputLabel&&actionDialogValue.trim().length<3} onClick={submitActionDialog}>{actionDialog.confirmLabel}</button></div>
      </Modal>}
    </div>
  );
}

function Overview({ profile, printers, stations, printJobs, pricing, agentDevices, paymentSettings, onNavigate }: {
  profile: ShopProfile | null; printers: PrinterDevice[]; stations: QrStation[];
  printJobs: ShopPrintJob[]; pricing: ShopPricingProfile | null;
  agentDevices: AgentDevice[]; paymentSettings: ShopPaymentSettings | null;
  onNavigate: (section: Section) => void;
}) {
  const activePrinters = printers.filter(printer => printer.isActive);
  const todayKey = istDateKey(new Date());
  const jobsToday = printJobs.filter(job => istDateKey(job.createdUtc) === todayKey);
  const quotesAwaiting = printJobs.filter(job => job.status === "AWAITING_SHOP_APPROVAL");
  const onlineAgentIds = new Set(agentDevices
    .filter(agent => !agent.revokedUtc && agent.status === "Online" &&
      !!agent.lastSeenUtc && Date.parse(agent.lastSeenUtc) > Date.now() - 2 * 60 * 1000)
    .map(agent => agent.id));
  const hasOnlineAgent = onlineAgentIds.size > 0;
  const hasReachablePrinter = printers.some(printer => isPrinterAvailable(printer, onlineAgentIds));
  const hasPaymentMethod = paymentSettings?.cashEnabled === true ||
    (paymentSettings?.upiEnabled === true &&
      !!paymentSettings.upiId?.trim() && !!paymentSettings.payeeName?.trim());
  const setupChecks: { complete: boolean; title: string; detail: string; target: Section }[] = [
    { complete: !!profile?.displayName, title: "Create your shop identity", detail: "Your shop name and customer-facing brand.", target: "Settings" },
    { complete: activePrinters.length > 0, title: "Register your printers", detail: "Add and enable the printers available at your counter.", target: "Printers" },
    { complete: hasOnlineAgent, title: "Connect the shop computer", detail: "The Windows Agent must be paired and recently online.", target: "Print Agent" },
    { complete: hasReachablePrinter, title: "Verify a live printer", detail: "At least one enabled output must be visible to the connected computer.", target: "Printers" },
    { complete: stations.some(station => station.isEnabled), title: "Enable a QR station", detail: "At least one customer-facing QR station must be enabled.", target: "QR Stations" },
    { complete: !!pricing?.isConfigured, title: "Set your print prices", detail: "Configure mono, colour and duplex rates.", target: "Pricing" },
    { complete: hasPaymentMethod, title: "Enable a payment method", detail: "Configure cash at counter or a complete UPI payee profile.", target: "Payments" }
  ];
  return (
    <>
      <div className="stat-grid">
        <MetricCard icon={<Printer size={18}/>} label="Registered printers" value={String(printers.length)} note={`${activePrinters.length} enabled for use`} accent="blue"/>
        <MetricCard icon={<QrCode size={18}/>} label="QR stations" value={String(stations.length)} note={`${stations.filter(station=>station.isEnabled).length} currently enabled`} accent="violet"/>
        <MetricCard icon={<FileText size={18}/>} label="Print jobs today" value={String(jobsToday.length)} note="Real customer uploads" accent="amber"/>
        <MetricCard icon={<Wallet size={18}/>} label="Quotes awaiting review" value={String(quotesAwaiting.length)} note="Quotes are not collected payments" accent="mint"/>
      </div>
      <div className="dashboard-grid">
        <section className="panel activity-empty-panel">
          <div className="panel-heading"><div><h2>Customer activity</h2><p>Recent uploads and quote requests from your QR stations</p></div><span className="neutral-chip">{printJobs.length} recent jobs</span></div>
          {printJobs.length===0 ? <div className="activity-empty-content"><div className="activity-empty-art"><div className="activity-art-sheet"><FileText size={25}/><i/><i/><i/></div><div className="activity-art-printer"><Printer size={27}/></div><span className="activity-art-spark">✦</span></div><h3>Ready when your first customer is</h3><p>Set your prices and share a QR station. Customer uploads will appear here as soon as they arrive.</p><button className="text-action" onClick={() => onNavigate(printers.length ? "QR Stations" : "Printers")}>{printers.length ? "Open QR stations" : "Add your first printer"} <span>→</span></button></div> :
            <div className="activity-job-list">
              {printJobs.slice(0,4).map(job=><div className="activity-job-row" key={job.id}><div className="activity-job-icon"><FileText size={16}/></div><div className="activity-job-copy"><strong>{job.jobNumber}</strong><span>{job.document.originalFileName} · {job.printKind==="PHOTO" ? `${job.photoCount??0} photos · ${job.photoSizeCode??""} · ${job.pageCount} A4 pages` : `${job.pageCount} pages`}</span></div><div className="activity-job-status"><Status label={prettyStatus(job.status)} tone={job.status==="AWAITING_CUSTOMER_APPROVAL"||job.status==="AWAITING_SHOP_APPROVAL"?"amber":"blue"}/><small>{formatIstTime(job.createdUtc)}</small></div></div>)}
              <button className="text-action" onClick={()=>onNavigate("Print Queue")}>Open print queue <span>→</span></button>
            </div>}
        </section>
        <section className="panel printer-panel">
          <div className="panel-heading"><div><h2>Printer setup</h2><p>Registered shop devices</p></div><button className="text-action" onClick={() => onNavigate("Printers")}>Manage <span>→</span></button></div>
          <div className="printer-summary"><div className="printer-summary-icon"><Printer size={19}/></div><div><strong>{printers.length} registered</strong><span>Live hardware status awaits the local agent</span></div><div className="health-score neutral-score">—</div></div>
          <div className="device-list">{printers.slice(0,4).map(printer=><Device key={printer.id} name={printer.displayName} detail={`${printer.manufacturer} · ${printer.connectionType}`} state={printer.status} tone={printer.status.toLowerCase()==="ready"?"green":"slate"}/>)}{printers.length===0 && <div className="mini-empty">No printers registered yet.</div>}</div>
          <button className="text-action full-action" onClick={() => onNavigate("Printers")}>View all printers <span>→</span></button>
        </section>
      </div>
      <section className="panel setup-panel">
        <div className="panel-heading"><div><h2>Shop setup checklist</h2><p>Complete these steps before accepting customer print jobs.</p></div><span className="setup-count">{setupChecks.filter(check => check.complete).length} / {setupChecks.length} complete</span></div>
        <div className="setup-checks">
          {setupChecks.map(check => <SetupCheck key={check.title} complete={check.complete} title={check.title} detail={check.detail} onClick={() => onNavigate(check.target)}/>)}
        </div>
      </section>
    </>
  );
}



function BatchReviewModal({jobs,busy,decisions,retrying,onDecision,onClose,onPreview,onApply}:{
  jobs:ShopPrintJob[];
  busy:boolean;
  decisions:Record<string,boolean>;
  retrying:boolean;
  onDecision:(jobId:string,accepted:boolean)=>void;
  onClose:()=>void;
  onPreview:(job:ShopPrintJob)=>void;
  onApply:(acceptedJobIds:string[],pdfBrandingEnabled:boolean)=>void;
}) {
  const [pdfBrandingEnabled,setPdfBrandingEnabled]=useState(()=>jobs.some(job=>job.pdfBrandingEnabled===true));
  const acceptedIds=jobs.filter(job=>decisions[job.id]??true).map(job=>job.id);
  const acceptedCount=acceptedIds.length;
  const rejectedCount=jobs.length-acceptedCount;
  const acceptedJobs=jobs.filter(job=>decisions[job.id]??true);
  const quoteExpired=(job:ShopPrintJob)=>!job.quoteExpiresUtc||
    !Number.isFinite(Date.parse(job.quoteExpiresUtc))||Date.parse(job.quoteExpiresUtc)<=Date.now();
  const quoteRefreshRequiredCount=acceptedJobs.filter(quoteExpired).length;
  const acceptedTotal=acceptedJobs.filter(job=>!quoteExpired(job))
    .reduce((sum,job)=>sum+Math.max(0,job.quotedAmountMinor),0);
  const acceptedRequestedPages=acceptedJobs.reduce((sum,job)=>sum+job.pageCount*job.copies,0);
  const acceptedMonoPages=acceptedJobs.reduce((sum,job)=>sum+(job.colourMode==="Mono"?job.pageCount*job.copies:0),0);
  const acceptedColourPages=acceptedJobs.reduce((sum,job)=>sum+(job.colourMode==="Colour"?job.pageCount*job.copies:0),0);
  const folders=Array.from(new Set(jobs.map(job=>job.batchGroupName??"Loose files")));
  return <Modal title={retrying?"Retry batch review":"Review batch"} subtitle={retrying?"Retry review. Rejected files stay excluded until restored.":"Files start accepted. Uncheck a file to exclude it; recheck it to restore it."} onClose={onClose}>
    <div className="batch-review-list">{folders.map(folderName=><section className="batch-review-folder" key={folderName}>
      <div className="batch-review-folder-head"><FolderOpen size={15}/><strong>{folderName}</strong><small>{jobs.filter(job=>(job.batchGroupName??"Loose files")===folderName).length} files</small></div>
      {jobs.filter(job=>(job.batchGroupName??"Loose files")===folderName).map((job,index)=>{
        const accepted=decisions[job.id]??true;
        const needsFreshQuote=accepted&&quoteExpired(job);
        return <div className={accepted?"batch-review-row accepted":"batch-review-row rejected"} key={job.id}>
          <label className="batch-review-checkbox"><input type="checkbox" checked={accepted} disabled={busy} onChange={event=>onDecision(job.id,event.target.checked)}/></label>
          <span className="batch-review-index">{index+1}</span>
          <div className="batch-review-file"><div className="queue-batch-file-title"><strong>{job.document.originalFileName}</strong><PrintKindTag printKind={job.printKind}/></div><small>{job.printKind==="PHOTO" ? `${job.photoCount??0} photos · ${job.photoSizeCode??""} · ${job.pageCount} A4 pages` : `${job.pageCount} pages × ${job.copies} copies = ${job.pageCount*job.copies} pages requested · ${job.colourMode} · ${job.isDuplex?"Double-sided":"Single-sided"}`} · {needsFreshQuote?"Previous quote expired · fresh quote required":formatMoney(job.quotedAmountMinor,job.currency)}</small><Status label={!accepted?"Marked for rejection":needsFreshQuote?"Accepted · re-quote needed":"Accepted"} tone={!accepted?"red":needsFreshQuote?"amber":"green"}/></div>
          <button className="subtle-button" type="button" disabled={busy} onClick={()=>onPreview(job)}>Preview</button>
        </div>;
      })}
    </section>)}</div>
    <div className="batch-branding-setting">
      <div className="batch-branding-copy">
        <strong>SmartPrint PDF footer <InfoTooltip label="PDF footer details"><span>Footer only appears on PDFs with a verified blank bottom margin.</span><span>Digitally signed or interactive PDFs remain unchanged.</span><span>Original uploads and physical prints are never altered.</span><span>Image-to-PDF exports currently remain unbranded.</span><span>Branding off preserves a single-copy full-document PDF byte-for-byte.</span><span>Multi-copy or reformatted exports may be flattened.</span></InfoTooltip></strong>
        <small>Add a footer only to eligible PDFs with clear bottom margins. New batches start off.</small>
      </div>
      <label className="batch-branding-toggle">
        <input type="checkbox" checked={pdfBrandingEnabled} disabled={busy} onChange={event=>setPdfBrandingEnabled(event.currentTarget.checked)} aria-label="Add SmartPrint footer to PDF exports in this batch"/>
        <span>{pdfBrandingEnabled?"On for this batch":"Off · default"}</span>
      </label>
    </div>
    <div className="batch-review-summary"><span><strong>{acceptedCount}</strong> accepted</span><span><strong>{rejectedCount}</strong> rejected</span><span><strong>{acceptedRequestedPages}</strong> pages requested</span><span><strong>{acceptedMonoPages}</strong> B&W · <strong>{acceptedColourPages}</strong> Colour</span><span><strong>{formatMoney(acceptedTotal,jobs[0]?.currency??"INR")}</strong> accepted value with valid quotes</span>{quoteRefreshRequiredCount>0&&<span><strong>{quoteRefreshRequiredCount}</strong> need a fresh quote</span>}<small>Page count = source pages × copies. Refresh expired quotes before checkout. Reject files before checkout; after payment starts, use the file-level refund action.</small></div>
    <div className="modal-actions"><button className="secondary-button" onClick={onClose} disabled={busy}>Keep reviewing</button><button className="primary-button" disabled={busy||jobs.length===0} onClick={()=>onApply(acceptedIds,pdfBrandingEnabled)}>{busy?<span className="button-spinner"/>:"Apply review"}</button></div>
  </Modal>;
}

function JobHistoryModal({job,events,onClose}:{job:ShopPrintJob;events:ShopJobEvent[];onClose:()=>void}) {
  return <Modal title={job.jobNumber} subtitle={`Audit history · ${job.document.originalFileName}`} onClose={onClose}>
    <div className="shop-history-list">{events.length===0?<p>No recorded events yet.</p>:events.map(event=><div key={event.id}><i/><span><strong>{prettyStatus(event.newStatus)}</strong><small>{event.actorType==="ShopUser"?"Shop team":event.actorType==="Customer"?"Customer":"System"} · {formatIstDateTime(event.createdUtc)}</small>{event.reasonCode&&<small>{event.reasonCode.replaceAll("_"," ").toLowerCase()}</small>}</span></div>)}</div>
    <div className="modal-actions"><button className="secondary-button" onClick={onClose}>Close history</button></div>
  </Modal>;
}

function DocumentPreviewModal({name,url,mediaType,job,readOnly=false,isSavedOutput=false,focusEditOnMount=false,onClose,onApprove,onReject,onRefund,onEditPhoto}:{
  name:string;url:string;mediaType:string;job:ShopPrintJob;readOnly?:boolean;isSavedOutput?:boolean;focusEditOnMount?:boolean;onClose:()=>void;onApprove:()=>void;onReject:()=>void;onRefund:()=>void;onEditPhoto?:()=>void;
}) {
  const editPhotoButtonRef=useRef<HTMLButtonElement|null>(null);
  const isPdf=mediaType==="application/pdf";
  const isImage=mediaType==="image/jpeg"||mediaType==="image/png";
  useEffect(()=>{
    if(focusEditOnMount)editPhotoButtonRef.current?.focus();
  },[focusEditOnMount]);
  useEffect(()=>{
    if(!isDesktopApp()||(!isImage&&job.printKind!=="PHOTO"))return;
    void setDesktopContentProtection(true);
    return ()=>{void setDesktopContentProtection(false);};
  },[isImage,job.printKind]);
  const photoDimensions:Record<string,string>={"35x45":"35 × 45 mm","25x35":"25 × 35 mm","2x2":"50.8 × 50.8 mm","4x6":"101.6 × 152.4 mm"};
  const previewSummary = isSavedOutput
    ? "Final PDF export saved by this shop. This is the actual output file; the uploaded original remains separate and unchanged."
    : job.printKind==="PHOTO"
      ? `${job.photoCount??0} ${photoDimensions[job.photoSizeCode??""]??job.photoSizeCode??""} photos · ${job.pageCount} A4 PDF page(s). Preview displays the complete prepared file.`
      : `${job.pageCount} source page(s) × ${job.copies} copy/copies = ${job.pageCount*job.copies} printed page(s). Preview displays the uploaded original once.`;
  const subtitle = isSavedOutput ? "Saved PDF output · Read-only preview" : readOnly ? `Read-only preview · ${previewSummary}` : job.batchId ? `File review · Batch ${job.batchId.slice(0,8).toUpperCase()} · ${previewSummary}` : previewSummary;
  return <Modal title={name} subtitle={subtitle} onClose={onClose}>
    <div className="shop-document-preview" onContextMenu={event=>{if(isImage||job.printKind==="PHOTO")event.preventDefault();}} onDragStart={event=>{if(isImage||job.printKind==="PHOTO")event.preventDefault();}}>
      {isPdf&&job.printKind==="PHOTO"?<><iframe src={url} title={name}/><span className="shop-photo-preview-watermark" aria-hidden="true">SMARTPRINT · SHOP PRINT ONLY · PRIVATE PREVIEW</span></>:isPdf?<iframe src={url} title={name}/>:isImage?<><img src={url} alt={name} draggable={false}/><span className="shop-photo-preview-watermark" aria-hidden="true">SMARTPRINT · PRIVATE SHOP PREVIEW</span></>:<div className="shop-document-preview-unavailable">Preview format is not supported. <a href={url} download={name}>Download the original file</a></div>}
    </div>
    {onEditPhoto&&<div className="file-review-note"><ImageIcon size={16}/><span>Open the same Photo Studio used by customers to crop, improve the background, add form details and prepare this photo for shop printing.</span><button ref={editPhotoButtonRef} className="secondary-button" onClick={onEditPhoto}>Enhance photo for printing</button></div>}
    {!readOnly&&isImage&&job.status==="AWAITING_SHOP_APPROVAL"&&job.storageMode==="LOCAL_AGENT"&&<div className="file-review-note"><ShieldCheck size={16}/><span>This original is stored only on the paired shop computer. It remains local; this version does not copy it to cloud storage for editing.</span></div>}
    {["AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT"].includes(job.status)&&<div className="file-review-note"><Check size={16}/><span>{readOnly ? "This preview is part of the batch review. Keep or change this file's decision in the batch panel after closing the preview." : "Review this file independently. Accepting or rejecting it will not change any other file in the batch."}</span></div>}
    {job.status==="PAID_AUTHORISED"&&(job.payment?.status==="VERIFIED"||job.payment?.status==="PARTIALLY_REFUNDED")&&<div className="file-refund-note"><Wallet size={16}/><span>Paid amount: <strong>{formatMoney(job.payment.amountMinor,job.payment.currency)}</strong>. A refund can be recorded while this file has not yet been handed to the shop computer.</span></div>}
    <div className="modal-actions">
      {!readOnly&&job.status==="AWAITING_SHOP_APPROVAL"&&<button className="primary-button" onClick={onApprove}><Check size={15}/> Accept this file</button>}
      {!readOnly&&["AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT"].includes(job.status)&&<button className="secondary-button reject-action" onClick={onReject}><X size={15}/> Reject this file</button>}
      {!readOnly&&(job.status==="PAID_AUTHORISED"||job.status==="REJECTED")&&(job.payment?.status==="VERIFIED"||job.payment?.status==="PARTIALLY_REFUNDED")&&!job.localQueueReceivedUtc&&!job.claimedByAgentDeviceId&&<button className="secondary-button reject-action" onClick={onRefund}><RotateCcw size={15}/> {job.status==="REJECTED"?"Refund rejected file":"Review pages & refund"}</button>}
      <button className="secondary-button" onClick={onClose}>Close preview</button>
    </div>
  </Modal>;
}

function RefundModal({job,token,busy,onClose,onSubmit}:{
  job:ShopPrintJob; token:string; busy:boolean; onClose:()=>void;
  onSubmit:(values:{refundMethod:"UPI"|"CASH";refundReference:string|null;reason:string;rejectedPageNumbers:number[];idempotencyKey:string})=>void;
}) {
  const [rejectedPages,setRejectedPages]=useState<Set<number>>(()=>{
    if(job.status==="REJECTED")return new Set<number>(Array.from({length:job.pageCount},(_,index)=>index+1));
    try{return new Set<number>(JSON.parse(job.rejectedPageNumbersJson||"[]") as number[]);}catch{return new Set<number>();}
  });
  const [estimate,setEstimate]=useState<Awaited<ReturnType<typeof previewShopPrintRefund>>|null>(null);
  const [estimating,setEstimating]=useState(false);
  const [estimateError,setEstimateError]=useState("");
  const [method,setMethod]=useState<"UPI"|"CASH">(job.payment?.method==="CASH"?"CASH":"UPI");
  const [reference,setReference]=useState("");
  const [reason,setReason]=useState(job.status==="REJECTED"?"Shop rejected the entire file after preview.":"Customer rejected selected pages after shop preview.");
  const [confirmed,setConfirmed]=useState(false);
  const refundRequest=useRef<{signature:string;key:string}|null>(null);
  const selectedPages=Array.from(rejectedPages).sort((a,b)=>a-b);
  const selectionKey=selectedPages.join(",");
  const alreadyRejected=(()=>{try{return new Set<number>(JSON.parse(job.rejectedPageNumbersJson||"[]") as number[]);}catch{return new Set<number>();}})();

  useEffect(()=>{
    let active=true;
    setEstimating(true);setEstimateError("");setEstimate(null);
    void previewShopPrintRefund(token,job.id,selectedPages).then(value=>{if(active)setEstimate(value);})
      .catch(error=>{if(active)setEstimateError(error instanceof Error?error.message:"Could not calculate the refund.");})
      .finally(()=>{if(active)setEstimating(false);});
    return ()=>{active=false;};
  },[token,job.id,selectionKey]);

  const additional=estimate?.additionalRefundAmountMinor??0;
  const hasNewPages=selectedPages.some(page=>!alreadyRejected.has(page));
  const canSubmit=!!estimate&&hasNewPages&&reason.trim().length>=3&&confirmed&&
    (method==="CASH"||reference.trim().length>=4);
  const togglePage=(page:number,checked:boolean)=>{
    if(alreadyRejected.has(page))return;
    setRejectedPages(current=>{const next=new Set(current);if(checked)next.add(page);else next.delete(page);return next;});
  };

  return <Modal title={job.status==="REJECTED"?"Refund rejected file":"Review pages and refund"} subtitle={job.status==="REJECTED"?"All pages are selected because the shop rejected this entire file. Confirm the full-file refund before handoff.":"Every page starts accepted. Tick only pages to reject; selections can be undone before recording. Already refunded pages stay excluded."} onClose={onClose}>
    <div className="refund-summary-card">
      <div><span>FILE</span><strong>{job.document.originalFileName}</strong></div>
      <div><span>VERIFIED PAYMENT</span><strong>{formatMoney(job.payment?.amountMinor||job.quotedAmountMinor,job.payment?.currency||job.currency)}</strong></div>
    </div>
    <div className="refund-selection-actions">
      <span>Reject specific pages or the entire file.</span>
      <button type="button" className="secondary-button reject-action" disabled={busy} onClick={()=>setRejectedPages(new Set(Array.from({length:job.pageCount},(_,index)=>index+1)))}>Reject entire file · refund all pages</button>
    </div>
    <div className="page-refund-grid" role="group" aria-label="Pages to reject">
      {Array.from({length:job.pageCount},(_,index)=>index+1).map(page=>{
        const rejected=rejectedPages.has(page), locked=alreadyRejected.has(page);
        return <label className={locked?"page-refund-chip already-rejected":rejected?"page-refund-chip selected":"page-refund-chip"} key={page}>
          <input type="checkbox" checked={rejected} disabled={locked||busy} onChange={event=>togglePage(page,event.target.checked)}/>
          <span>Page {page}{locked?" · refunded":rejected?" · reject":""}</span>
        </label>;
      })}
    </div>
    <div className="page-refund-estimate">
      {estimating?<span>Calculating the exact refund from the original quote…</span>:
       estimateError?<span className="inline-error">{estimateError}</span>:
       estimate?<><span>{estimate.acceptedPageCount} pages will remain in the print job.</span><strong>Return {formatMoney(additional,estimate.currency)}</strong><small>Cumulative returned: {formatMoney(estimate.cumulativeRefundAmountMinor,estimate.currency)} · remaining payment: {formatMoney(estimate.remainingPaidAmountMinor,estimate.currency)}</small></>:<span>Select page(s) to see the refund amount.</span>}
    </div>
    {estimate&&additional>0&&<form className="refund-form" onSubmit={event=>{
      event.preventDefault();
      if(!canSubmit)return;
      const payload={refundMethod:method,refundReference:method==="UPI"?reference.trim():null,reason:reason.trim(),rejectedPageNumbers:selectedPages};
      const signature=JSON.stringify(payload);
      if(refundRequest.current?.signature!==signature)refundRequest.current={signature,key:crypto.randomUUID()};
      onSubmit({...payload,idempotencyKey:refundRequest.current.key});
    }}>
      <label>Refund returned by<select value={method} onChange={event=>setMethod(event.target.value as "UPI"|"CASH")}><option value="UPI">UPI transfer</option><option value="CASH">Cash returned at counter</option></select></label>
      {method==="UPI"&&<label>Refund transaction reference<input value={reference} onChange={event=>setReference(event.target.value)} maxLength={100} placeholder="Enter the refund UPI reference" required/></label>}
      <label>Reason for rejecting these pages<textarea rows={3} maxLength={300} value={reason} onChange={event=>setReason(event.target.value)} required/></label>
      <label className="refund-confirm-line"><input type="checkbox" checked={confirmed} onChange={event=>setConfirmed(event.target.checked)}/><span>I confirm that I have already returned the exact additional amount shown above. SmartPrint records the refund; it does not send money automatically.</span></label>
      <div className="modal-actions"><button type="button" className="secondary-button" onClick={onClose}>Cancel</button><button className="primary-button" disabled={busy||estimating||!canSubmit}>{busy?<span className="button-spinner"/>:"Record refund and reject selected pages"}</button></div>
    </form>}
    {estimate&&additional===0&&<div className="refund-form"><p>Select at least one additional page that produces a positive refund from the original quote. No page is rejected until a refund can be recorded.</p><div className="modal-actions"><button type="button" className="secondary-button" onClick={onClose}>Close</button></div></div>}
  </Modal>;
}

function formatBytes(bytes:number) {
  return bytes<1024*1024?`${(bytes/1024).toFixed(0)} KB`:`${(bytes/(1024*1024)).toFixed(2)} MB`;
}

function ShopOwnerAccountModal({ profile, busy, onClose, onSave }: {
  profile: ShopProfile;
  busy: boolean;
  onClose: () => void;
  onSave: (values: { ownerName: string; email: string; currentPassword?: string }) => Promise<void>;
}) {
  const [ownerName, setOwnerName] = useState(profile.ownerName);
  const [email, setEmail] = useState(profile.ownerEmail);
  const [currentPassword, setCurrentPassword] = useState("");
  const [saveError, setSaveError] = useState("");
  const emailChanged = email.trim().toLowerCase() !== profile.ownerEmail.trim().toLowerCase();
  return (
    <Modal title="Personal details" subtitle="Update the name and sign-in email for your shop owner account." onClose={onClose}>
      <form className="modal-form account-edit-form" onSubmit={async event => {
        event.preventDefault();
        setSaveError("");
        try {
          await onSave({
            ownerName: ownerName.trim(),
            email: email.trim(),
            currentPassword: emailChanged ? currentPassword : undefined
          });
          onClose();
        } catch (exception) {
          setSaveError(exception instanceof Error ? exception.message : "Could not update your personal details.");
        }
      }}>
        <label>Your name
          <input value={ownerName} onChange={event => setOwnerName(event.target.value)}
            autoComplete="name" maxLength={160} required/>
        </label>
        <label>Sign-in email
          <input type="email" value={email}
            onChange={event => setEmail(event.currentTarget.value)}
            onInput={event => setEmail(event.currentTarget.value)}
            autoComplete="email" maxLength={256} required/>
        </label>
        {emailChanged && <>
          <div className="modal-note account-email-change-note">
            <ShieldCheck size={16}/>
            <span>This address becomes your sign-in email immediately. Make sure it is correct. Your current password is required to confirm the change.</span>
          </div>
          <label>Current password
            <input type="password" value={currentPassword}
              onChange={event => setCurrentPassword(event.target.value)}
              autoComplete="current-password" required/>
          </label>
        </>}
        {saveError && <div className="inline-error account-edit-error" role="alert">{saveError}</div>}
        <div className="modal-actions">
          <button type="button" className="secondary-button" onClick={onClose} disabled={busy}>Cancel</button>
          <button className="primary-button" disabled={busy || !ownerName.trim() || !email.trim() || (emailChanged && !currentPassword)}>
            {busy ? <span className="button-spinner"/> : "Save personal details"}
          </button>
        </div>
      </form>
    </Modal>
  );
}

function HelpSupportModal({ onClose, onNavigate }: { onClose: () => void; onNavigate: (section: Section) => void }) {
  const topics: { section: Section; title: string; description: string; icon: typeof FileText }[] = [
    { section: "Print Queue", title: "Print jobs and approvals", description: "Review incoming jobs, decisions, print status and refunds.", icon: FileText },
    { section: "Printers", title: "Printers and connections", description: "Check printer setup, availability and device details.", icon: Printer },
    { section: "Print Agent", title: "Shop computer and Print Agent", description: "Pair or reconnect the Windows computer that runs printing.", icon: Monitor },
    { section: "QR Stations", title: "QR stations", description: "Manage counter QR codes and their assigned printers.", icon: QrCode },
    { section: "Pricing", title: "Pricing", description: "Review the rates customers see before they submit a job.", icon: Wallet }
  ];

  return (
    <Modal title="Help & support" subtitle="Choose the part of your shop workspace you need help with." onClose={onClose}>
      <div className="help-topic-list">
        {topics.map(({ section, title, description, icon: Icon }) => (
          <button type="button" className="help-topic" key={section} onClick={() => onNavigate(section)}>
            <span className="help-topic-icon"><Icon size={17}/></span>
            <span className="help-topic-copy"><strong>{title}</strong><small>{description}</small></span>
            <ArrowRight size={16}/>
          </button>
        ))}
      </div>
      <p className="help-modal-note">These shortcuts open the existing shop tools. They do not change your settings or print jobs.</p>
    </Modal>
  );
}

function PrinterModal({ busy, onClose, onSubmit }: {
  busy:boolean; onClose:()=>void; onSubmit:(v:{displayName:string;manufacturer:string;model:string;connectionType:string;connectionAddress?:string;queueName?:string})=>void;
}) {
  const [displayName,setDisplayName]=useState("");
  const [manufacturer,setManufacturer]=useState("HP");
  const [model,setModel]=useState("");
  const [connectionType,setConnectionType]=useState("WindowsQueue");
  const [connectionAddress,setConnectionAddress]=useState("");
  const [queueName,setQueueName]=useState("");
  return <Modal title="Add a printer" subtitle="Register its identity first. Live discovery will be handled by the local Print Agent." onClose={onClose}>
    <form className="modal-form" onSubmit={e=>{e.preventDefault();onSubmit({displayName,manufacturer,model,connectionType,connectionAddress:connectionAddress||undefined,queueName:queueName||undefined});}}>
      <label>Display name<input value={displayName} onChange={e=>setDisplayName(e.target.value)} placeholder="e.g. Front counter printer" required maxLength={160}/></label>
      <div className="form-two-col"><label>Manufacturer<select value={manufacturer} onChange={e=>setManufacturer(e.target.value)}>{["HP","Canon","Ricoh","Epson","Brother","Kyocera","Xerox","Other"].map(item=><option key={item}>{item}</option>)}</select></label><label>Model<input value={model} onChange={e=>setModel(e.target.value)} placeholder="Model name" required maxLength={120}/></label></div>
      <label>Connection type<select value={connectionType} onChange={e=>setConnectionType(e.target.value)}><option value="WindowsQueue">Windows installed queue</option><option value="USB">USB</option><option value="LAN">LAN / Network</option><option value="IPP">IPP</option></select></label>
      {(connectionType==="LAN"||connectionType==="IPP")&&<label>Network address<input value={connectionAddress} onChange={e=>setConnectionAddress(e.target.value)} placeholder="192.168.1.100 or ipp://printer/ipp/print"/></label>}
      {connectionType==="WindowsQueue"&&<label>Windows queue name <span className="field-hint">Optional until the agent discovers installed queues</span><input value={queueName} onChange={e=>setQueueName(e.target.value)} placeholder="Exact Windows printer queue name"/></label>}
      <div className="modal-note"><ShieldCheck size={15}/> The printer will be listed as Unknown until a trusted local agent confirms its status and capabilities.</div>
      <div className="modal-actions"><button type="button" className="secondary-button" onClick={onClose}>Cancel</button><button className="primary-button" disabled={busy||!displayName.trim()||!model.trim()}>{busy?<span className="button-spinner"/>:"Add printer"}</button></div>
    </form>
  </Modal>;
}

function StationModal({ busy, printers, onClose, onSubmit }: {
  busy:boolean; printers:PrinterDevice[]; onClose:()=>void; onSubmit:(v:{displayName:string;printerDeviceId?:string})=>void;
}) {
  const [displayName,setDisplayName]=useState("Front counter");
  const [printerDeviceId,setPrinterDeviceId]=useState("");
  return <Modal title="Create a QR station" subtitle="Give customers a clear entry point for this counter or printer." onClose={onClose}>
    <form className="modal-form" onSubmit={e=>{e.preventDefault();onSubmit({displayName,printerDeviceId:printerDeviceId||undefined});}}>
      <label>Station name<input value={displayName} onChange={e=>setDisplayName(e.target.value)} placeholder="e.g. Front counter" required maxLength={120}/></label>
      <label>Default printer <span className="field-hint">Optional · can be changed before a job is printed</span><select value={printerDeviceId} onChange={e=>setPrinterDeviceId(e.target.value)}><option value="">No default printer</option>{printers.filter(printer=>printer.isActive).map(printer=><option key={printer.id} value={printer.id}>{printer.displayName} · {printer.manufacturer}</option>)}</select></label>
      <div className="modal-note"><QrCode size={15}/> The QR contains a public station identifier, not a permanent print authorisation. Every scan creates an expiring customer session. Only enabled printers are offered as defaults; you can also assign a printer to each paid job in the queue.</div>
      <div className="modal-actions"><button type="button" className="secondary-button" onClick={onClose}>Cancel</button><button className="primary-button" disabled={busy||!displayName.trim()}>{busy?<span className="button-spinner"/>:"Create station"}</button></div>
    </form>
  </Modal>;
}

function SetupCheck({complete,title,detail,onClick}:{complete:boolean;title:string;detail:string;onClick:()=>void}) {
  return <button className="setup-check" onClick={onClick}><span className={complete?"check-circle complete":"check-circle"}>{complete?<Check size={14}/>:null}</span><span className="setup-check-copy"><strong>{title}</strong><small>{detail}</small></span><ArrowRight size={16}/></button>;
}
function Device({name,detail,state,tone}:{name:string;detail:string;state:string;tone:string}) {
  return <div className="device-row"><div className="device-glyph"><Printer size={16}/></div><div className="device-copy"><strong>{name}</strong><span>{detail}</span></div><Status label={state} tone={tone}/></div>;
}
function initials(value:string) { return value.trim().split(/\s+/).slice(0,2).map(part=>part[0]?.toUpperCase()||"").join("") || "SP"; }
function pageDescription(section:Section) {
  const descriptions:Record<Section,string> = {
    "Order Journey":"See every customer order move from upload through review, payment and printing to handover.",
    "Overview":"Your shop setup and operations, all in one place.",
    "Print Queue":"Review real customer uploads and server-calculated print quotes.",
    "Printers":"Manage connected devices, capabilities and health.",
    "QR Stations":"Create secure customer entry points for each counter.",
    "Print Agent":"Pair the Windows computer that hosts your installed printers.",
    "Pricing":"Set your mono, colour and duplex rates.",
    "Subscription":"Review your assigned plan, entitlement limits and current usage.",
    "Payments":"Configure your shop's payment methods and verify customer print payments.",
    "Customers":"View customer activity and print history.",
    "Reports":"Understand print volume, shop revenue and performance.",
    "Settings":"Update the shop name and branding customers see when they connect."
  };
  return descriptions[section];
}
async function copyText(value:string) {
  try {
    if(navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(value);
      return;
    }
  } catch {
    // Fall through to the in-page clipboard fallback.
  }
  const helper=document.createElement("textarea");
  helper.value=value;
  helper.setAttribute("readonly","");
  helper.style.position="fixed";
  helper.style.opacity="0";
  document.body.appendChild(helper);
  helper.select();
  try { document.execCommand("copy"); } finally { helper.remove(); }
}
