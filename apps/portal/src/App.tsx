import { Fragment, useEffect, useRef, useState, type ReactNode } from "react";
import {
  Activity, ArrowRight, Check, Clock3, Copy, CreditCard, FileText, FileUp, FolderOpen, LayoutDashboard,
  LifeBuoy, LogOut, Menu, Moon, Monitor, Plus, Printer, QrCode, Search, Settings,
  ShieldCheck, Sun, Users, Wallet, X, PanelLeftClose, PanelLeftOpen, RotateCcw
} from "lucide-react";
import QRCode from "react-qr-code";
import AuthScreen from "./AuthScreen";
import PublicLandingPage from "./PublicLandingPage";
import { getPublicDownloadPath, usePublicRelease } from "./release-metadata";
import PlatformAdminConsole from "./PlatformAdminConsole";
import PlatformAdminAuthScreen from "./PlatformAdminAuthScreen";
import { resolvePortalRoute, shopPathForSection, shopSectionFromPath } from "./portal-routing";
import {
  ApiError, isPlatformAdminSession, acceptCustomerQuote, approveShopPrintJob, assignShopPrintJobPrinter, calculateCustomerQuote, getShopCustomerHistory, getShopCustomers, getShopReportSummary,
  confirmShopPrintOutput, dispatchShopPrintJob,
  createAgentEnrollmentCode, createCustomerSession, createPrinter, createStation,
  downloadShopDocument, getCustomerJobStatus, getShopAgentDevices, getShopJobEvents, getShopPrinters, getShopPrintJobsPage, loadShopData,
  logout, readStoredTokens, rejectShopPrintJob, rejectShopPayment, revokeAgentDevice,
  reportCustomerUpiPayment, renewCustomerPrintSession, getNativeCustomerPrintSession, saveNativeCustomerPrintSession, clearNativeCustomerPrintSession, resumeCustomerSession, rotateStationCode, startCustomerPayment, storeTokens,
  updatePrinterActive, updateShopPaymentSettings, updateShopPricing,
  updateShopProfile, updateStationEnabled, uploadCustomerDocument, uploadCustomerDocumentLocally,
  verifyShopPayment, retryShopPdfExport, recordShopPrintRefund, previewShopPrintRefund, type CustomerPayment, type ShopPayment, type ShopPaymentSettings, type ShopRefund,
  type AgentDevice, type AuthTokens, type CustomerJobStatus, type PrinterDevice, type PrintQuoteResult, type ShopJobEvent,
  type QrStation, type ShopCustomer, type ShopCustomerHistory, type ShopCustomerPage, type ShopPricingProfile, type ShopPrintJob, type ShopProfile, type ShopReportSummary, type ShopSubscriptionSummary,
  type UploadedPrintJob
} from "./api";
import { enrollLocalAgent, getLocalAgentStatus, isDesktopApp, type DesktopAgentStatus } from "./desktop-bridge";

type Section = "Overview" | "Print Queue" | "Printers" | "QR Stations" | "Print Agent" | "Pricing" | "Subscription" | "Payments" | "Customers" | "Reports" | "Settings";
type Modal = "printer" | "station" | null;

const sections: { label: Section; icon: typeof LayoutDashboard }[] = [
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

export default function App() {
  const route = resolvePortalRoute(window.location.pathname);
  const [tokens, setTokens] = useState<AuthTokens | null>(() =>
    route.kind === "admin" ? readStoredTokens("admin")
      : route.kind === "shop" ? readStoredTokens("shop")
      : null
  );

  if (route.kind === "station") {
    return <CustomerEntry publicCode={route.publicCode} />;
  }

  if (route.kind === "public") {
    return <PublicLandingPage />;
  }

  if (route.kind === "admin") {
    const onAdminSignOut = async () => {
      await storeTokens(null, "admin");
      setTokens(null);
    };
    if (!tokens) return <PlatformAdminAuthScreen onAuthenticated={setTokens}/>;
    if (!isPlatformAdminSession(tokens)) {
      return <ScopeAccessDenied scope="platform administration" onSignOut={onAdminSignOut}/>;
    }
    return <PlatformAdminConsole tokens={tokens} onSignOut={onAdminSignOut}/>;
  }

  const onShopSignOut = async () => {
    await storeTokens(null, "shop");
    setTokens(null);
  };
  if (!tokens) return <AuthScreen onAuthenticated={setTokens}/>;
  if (isPlatformAdminSession(tokens)) {
    return <ScopeAccessDenied scope="the shop workspace" onSignOut={onShopSignOut}/>;
  }
  return <ShopConsole tokens={tokens} onSignOut={onShopSignOut}/>;
}

function ScopeAccessDenied({ scope, onSignOut }: { scope: string; onSignOut: () => Promise<void> }) {
  const [busy, setBusy] = useState(false);
  async function signOut() {
    setBusy(true);
    try { await onSignOut(); } finally { setBusy(false); }
  }
  return (
    <main className="auth-screen">
      <header className="auth-topbar">
        <div className="brand-lockup auth-brand"><div className="brand-mark"><Printer size={21}/></div><div><div className="brand-name">smart<span>print</span></div><div className="brand-caption">BY QRtoPrint</div></div></div>
        <a className="auth-home-link" href="/">Back to website</a>
      </header>
      <section className="auth-card" style={{ maxWidth: 520, margin: "12vh auto" }}>
        <div className="auth-card-heading"><div className="auth-card-icon"><ShieldCheck size={20}/></div><div><span>APPLICATION SCOPE MISMATCH</span><h2>Access not available</h2><p>This signed-in account is not authorized for {scope}.</p></div></div>
        <button className="auth-submit" onClick={() => void signOut()} disabled={busy}>{busy ? <span className="button-spinner"/> : "Sign out and use another account"} <ArrowRight size={16}/></button>
      </section>
    </main>
  );
}

function ShopConsole({ tokens, onSignOut }: { tokens: AuthTokens; onSignOut: () => Promise<void> }) {
  const [active, setActive] = useState<Section>(() => shopSectionFromPath(window.location.pathname));

  useEffect(() => {
    const syncSectionFromPath = () => setActive(shopSectionFromPath(window.location.pathname));
    window.addEventListener("popstate", syncSectionFromPath);
    return () => window.removeEventListener("popstate", syncSectionFromPath);
  }, []);
  const [dark, setDark] = useState(true);
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(() => {
    try { return window.localStorage.getItem("qrtoprint.sidebar-collapsed") === "true"; } catch { return false; }
  });
  const reloadInFlight = useRef(false);
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
  const [cloudStatus, setCloudStatus] = useState<"checking" | "connected" | "unavailable">("checking");
  const [profile, setProfile] = useState<ShopProfile | null>(null);
  const [printers, setPrinters] = useState<PrinterDevice[]>([]);
  const [stations, setStations] = useState<QrStation[]>([]);
  const [pricing, setPricing] = useState<ShopPricingProfile | null>(null);
  const [printJobs, setPrintJobs] = useState<ShopPrintJob[]>([]);
  const [hasMoreQueueJobs, setHasMoreQueueJobs] = useState(false);
  const [loadingOlderQueueJobs, setLoadingOlderQueueJobs] = useState(false);
  const loadingOlderQueueJobsRef = useRef(false);
  const [queueApiOffset, setQueueApiOffset] = useState(0);
  const [queueSearch, setQueueSearch] = useState("");
  const queueSearchGeneration = useRef(0);
  const queuePageGeneration = useRef(0);
  const queueFirstPageIds = useRef<Set<string>>(new Set());
  const [agentDevices, setAgentDevices] = useState<AgentDevice[]>([]);
  const agentDevicesRef = useRef<AgentDevice[]>([]);
  agentDevicesRef.current = agentDevices;
  const [paymentSettings, setPaymentSettings] = useState<ShopPaymentSettings | null>(null);
  const [payments, setPayments] = useState<ShopPayment[]>([]);
  const [refunds, setRefunds] = useState<ShopRefund[]>([]);
  const [subscription, setSubscription] = useState<ShopSubscriptionSummary | null>(null);
  const [apiConnectionState, setApiConnectionState] = useState<"checking" | "connected" | "degraded" | "offline">("checking");
  const [enrollmentCode, setEnrollmentCode] = useState<{ code: string; expiresUtc: string } | null>(null);
  const [agentBusy, setAgentBusy] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [modal, setModal] = useState<Modal>(null);
  const [busy, setBusy] = useState(false);
  const [toast, setToast] = useState("");
  const [preview, setPreview] = useState<{ name: string; url: string; mediaType: string; job: ShopPrintJob } | null>(null);
  const [refundJob, setRefundJob] = useState<ShopPrintJob | null>(null);
  const [batchReviewJobs, setBatchReviewJobs] = useState<ShopPrintJob[] | null>(null);
  const [batchReviewDecisions, setBatchReviewDecisions] = useState<Record<string, boolean>>({});
  const [batchReviewRetry, setBatchReviewRetry] = useState(false);
  const [batchReviewRetryIds, setBatchReviewRetryIds] = useState<string[]>([]);
  type ActionDialogState = { title: string; message: string; inputLabel?: string; confirmLabel: string; resolve: (value: string | null) => void };
  const [actionDialog, setActionDialog] = useState<ActionDialogState | null>(null);
  const [actionDialogValue, setActionDialogValue] = useState("");
  const [busyJobId, setBusyJobId] = useState<string | null>(null);
  const [eventHistory, setEventHistory] = useState<{ job: ShopPrintJob; events: ShopJobEvent[] } | null>(null);

  async function reload() {
    if (reloadInFlight.current) return;
    queueSearchGeneration.current++;
    queuePageGeneration.current++;
    reloadInFlight.current = true;
    setApiConnectionState("checking");
    setLoading(true);
    setError("");
    try {
      const snapshot = await loadShopData(tokens.accessToken, queueSearch);
      setProfile(snapshot.profile);
      setPrinters(snapshot.printers);
      setStations(snapshot.stations);
      setPricing(snapshot.pricing);
      setPrintJobs(snapshot.printJobs);
      queueFirstPageIds.current = new Set(snapshot.printJobs.map(job => job.id));
      setHasMoreQueueJobs(snapshot.printJobs.length === 100);
      setQueueApiOffset(snapshot.printJobs.length);
      agentDevicesRef.current = snapshot.agentDevices;
      setAgentDevices(snapshot.agentDevices);
      setPaymentSettings(snapshot.paymentSettings);
      setPayments(snapshot.payments);
      setRefunds(snapshot.refunds);
      setSubscription(snapshot.subscription);
      setApiConnectionState("connected");
      setCloudStatus("connected");
      if (isDesktopApp()) {
        // The Windows service may still be starting after installation or reboot.
        // Keep discovery off the initial shop-data load and retry quietly.
        scheduleDesktopAgentSync(0);
      }
    } catch (exception) {
      setCloudStatus("unavailable");
      if (exception instanceof ApiError && exception.status === 401) {
        storeTokens(null);
        try { await onSignOut(); }
        catch (signOutError) { setError(signOutError instanceof Error ? signOutError.message : "Could not clear the secure desktop session."); }
      } else {
        handleShopApiError(exception, "Could not load your shop.");
      }
    } finally {
      setLoading(false);
      reloadInFlight.current = false;
    }
  }

  function handleShopApiError(exception: unknown, fallback: string) {
    if (exception instanceof ApiError && exception.status === 401) {
      storeTokens(null);
      onSignOut();
      return;
    }
    const status = exception instanceof ApiError ? exception.status : 0;
    setApiConnectionState(status === 0 ? "offline" : status >= 500 ? "degraded" : "connected");
    setError(exception instanceof Error ? exception.message : fallback);
  }

  async function refreshPrintQueue() {
    const generation = queuePageGeneration.current;
    try {
      const [latest, latestPrinters, latestAgents] = await Promise.all([
        getShopPrintJobsPage(tokens.accessToken, 0, 100, queueSearch),
        getShopPrinters(tokens.accessToken),
        getShopAgentDevices(tokens.accessToken)
      ]);
      if (generation !== queuePageGeneration.current) return;
      setApiConnectionState("connected");
      setPrinters(latestPrinters);
      setAgentDevices(latestAgents);
      const latestIds = new Set(latest.map(job => job.id));
      const firstPageMembershipChanged = latest.length !== queueFirstPageIds.current.size ||
        latest.some(job => !queueFirstPageIds.current.has(job.id));
      if (firstPageMembershipChanged) {
        // Any insert/removal shifts every offset page. Reset pagination to the
        // current first page so no older records are skipped or duplicated.
        queuePageGeneration.current++;
        queueFirstPageIds.current = latestIds;
        setPrintJobs(latest);
        setQueueApiOffset(latest.length);
        setHasMoreQueueJobs(latest.length === 100);
        return;
      }
      setPrintJobs(current => {
        const byId = new Map(current.map(job => [job.id, job]));
        for (const job of latest) byId.set(job.id, job);
        return Array.from(byId.values()).sort((left, right) =>
          Date.parse(right.createdUtc) - Date.parse(left.createdUtc) || right.id.localeCompare(left.id)
        );
      });
      setHasMoreQueueJobs(latest.length === 100 || printJobs.length > latest.length);
    } catch (exception) {
      if (generation !== queuePageGeneration.current) return;
      handleShopApiError(exception, "Could not refresh the print queue.");
    }
  }

  async function loadOlderQueueJobs() {
    if (loadingOlderQueueJobsRef.current || !hasMoreQueueJobs) return;
    loadingOlderQueueJobsRef.current = true;
    const generation = queuePageGeneration.current;
    const offset = queueApiOffset;
    setLoadingOlderQueueJobs(true);
    try {
      const older = await getShopPrintJobsPage(tokens.accessToken, offset, 100, queueSearch);
      if (generation !== queuePageGeneration.current) return;
      setPrintJobs(current => {
        const knownIds = new Set(current.map(job => job.id));
        const merged = [...current, ...older.filter(job => !knownIds.has(job.id))];
        return merged.sort((left, right) =>
          Date.parse(right.createdUtc) - Date.parse(left.createdUtc) || right.id.localeCompare(left.id)
        );
      });
      setQueueApiOffset(offset => offset + older.length);
      setHasMoreQueueJobs(older.length === 100);
    } catch (exception) {
      if (generation !== queuePageGeneration.current) return;
      handleShopApiError(exception, "Could not load older print jobs.");
    } finally {
      loadingOlderQueueJobsRef.current = false;
      setLoadingOlderQueueJobs(false);
    }
  }

  async function searchPrintQueue(value: string) {
    const normalized = value.trim().slice(0, 120);
    const generation = ++queueSearchGeneration.current;
    queuePageGeneration.current++;
    setQueueSearch(normalized);
    try {
      const matches = await getShopPrintJobsPage(tokens.accessToken, 0, 100, normalized);
      if (generation !== queueSearchGeneration.current) return;
      queueFirstPageIds.current = new Set(matches.map(job => job.id));
      setPrintJobs(matches);
      setQueueApiOffset(matches.length);
      setHasMoreQueueJobs(matches.length === 100);
    } catch (exception) {
      if (generation !== queueSearchGeneration.current) return;
      handleShopApiError(exception, "Could not search the print queue.");
    }
  }

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
      try { window.localStorage.setItem("qrtoprint.sidebar-collapsed", String(next)); } catch { /* Storage is optional. */ }
      return next;
    });
  }

  useEffect(() => {
    void reload();
    // A token change is the only trigger; mutations call reload explicitly.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tokens.accessToken]);

  useEffect(() => () => {
    if (desktopAgentSyncTimer.current !== null) {
      window.clearTimeout(desktopAgentSyncTimer.current);
      desktopAgentSyncTimer.current = null;
    }
  }, []);

  useEffect(() => {
    if (active === "Print Queue") return;
    let mounted = true;
    let inFlight = false;
    const refreshDevices = async () => {
      if (inFlight) return;
      inFlight = true;
      try {
        const [latestPrinters, latestAgents] = await Promise.all([
          getShopPrinters(tokens.accessToken),
          getShopAgentDevices(tokens.accessToken)
        ]);
        if (mounted) {
          setPrinters(latestPrinters);
          setAgentDevices(latestAgents);
          setApiConnectionState("connected");
        }
      } catch (exception) {
        if (mounted) {
          if (exception instanceof ApiError && exception.status === 401) {
            storeTokens(null);
            onSignOut();
          } else {
            const status = exception instanceof ApiError ? exception.status : 0;
            setApiConnectionState(status === 0 ? "offline" : status >= 500 ? "degraded" : "connected");
          }
        }
      } finally {
        inFlight = false;
      }
    };
    void refreshDevices();
    const timer = window.setInterval(() => void refreshDevices(), 15_000);
    return () => {
      mounted = false;
      window.clearInterval(timer);
    };
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
  const today = new Intl.DateTimeFormat("en-IN", { weekday: "long", day: "numeric", month: "long" }).format(new Date());

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
    try {
      await updatePrinterActive(tokens.accessToken, printer.id, !printer.isActive);
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not update printer.");
    }
  }

  async function toggleStation(station: QrStation) {
    try {
      const updated = await updateStationEnabled(tokens.accessToken, station.id, !station.isEnabled);
      setStations(current => current.map(item => item.id === station.id ? { ...item, isEnabled: updated.isEnabled } : item));
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not update QR station.");
    }
  }

  async function createFreshStationCode(station: QrStation) {
    const confirmed = await confirmInApp("Generating a replacement QR will invalidate every previously printed sign for this station. Continue only when you intend to replace them.", "Replace this station's QR?", "Replace QR");
    if (!confirmed) return;
    try {
      const result = await rotateStationCode(tokens.accessToken, station.id);
      setStations(current => current.map(item => item.id === station.id
        ? { ...item, publicUrl: result.publicUrl, publicCodeHint: result.publicCodeHint }
        : item));
      setToast("New QR code generated. The previous code is now invalid.");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not generate a QR code.");
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

      setToast("This computer is securely connected. SmartPrint is discovering its printers.");
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
      await assignShopPrintJobPrinter(tokens.accessToken, job.id, printerDeviceId);
      setToast("Printer assigned. The paired agent can now transfer this paid job into its protected local queue.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not assign this printer.");
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
    upiEnabled: boolean; upiId: string | null; payeeName: string | null; cashEnabled: boolean;
  }) {
    setBusy(true);
    setError("");
    try {
      const updated = await updateShopPaymentSettings(tokens.accessToken, values);
      setPaymentSettings(updated);
      setToast("Shop payment methods saved. Customer print revenue continues to go directly to your shop.");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not save shop payment settings.");
    } finally {
      setBusy(false);
    }
  }

  async function verifyPayment(payment: ShopPayment) {
    const confirmation = payment.method === "UPI"
      ? `Have you independently confirmed this UPI transaction in your shop's bank/UPI app? Reference: ${payment.paymentReference || "not supplied"}`
      : "Have you physically received the cash for this print job?";
    if (!(await confirmInApp(confirmation, "Verify payment received", "Verify payment"))) return;

    setBusyJobId(payment.printJobId);
    setError("");
    try {
      await verifyShopPayment(tokens.accessToken, payment.id);
      setToast("Payment verified. The job is paid/authorised; printing still requires printer assignment and an explicit dispatch.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not verify this payment.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function rejectPayment(payment: ShopPayment) {
    const reason = await promptInApp("Explain why this payment report is being rejected.", "Reject payment report", "Rejection reason");
    if (reason === null) return;
    if (reason.trim().length < 3) {
      setError("A rejection reason of at least 3 characters is required.");
      return;
    }

    setBusyJobId(payment.printJobId);
    setError("");
    try {
      await rejectShopPayment(tokens.accessToken, payment.id, reason.trim());
      setToast("Payment report rejected. The customer can choose a new payment attempt.");
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

  async function savePricing(values: {
    monoPerSideMinor: number;
    colourPerSideMinor: number;
    monoDuplexPerSheetMinor: number;
    colourDuplexPerSheetMinor: number;
    minimumChargeMinor: number;
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

  async function previewJob(job: ShopPrintJob) {
    setBusyJobId(job.id);
    setError("");
    try {
      const blob = await downloadShopDocument(tokens.accessToken, job.id);
      const url = URL.createObjectURL(blob);
      setPreview({ name: job.document.originalFileName, url, mediaType: blob.type || job.document.mediaType, job });
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not open the document.");
    } finally {
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

  async function applyBatchReview(batchJobs: ShopPrintJob[], acceptedJobIds: string[]) {
    const accepted = new Set(acceptedJobIds);
    const acceptJobs = batchJobs.filter(job => accepted.has(job.id));
    const rejectJobs = batchJobs.filter(job => !accepted.has(job.id));
    if (acceptJobs.length + rejectJobs.length === 0) return;
    if (!(await confirmInApp(`Accept ${acceptJobs.length} file(s) and reject ${rejectJobs.length} file(s)? Accepted files will move to payment. Rejected files will be excluded from printing.`, "Confirm batch review", "Apply review"))) return;

    setBusyJobId("batch-review");
    setError("");
    const failedIds = new Set<string>();
    const failures: string[] = [];
    for (const job of acceptJobs) {
      try { await approveShopPrintJob(tokens.accessToken, job.id); }
      catch (exception) {
        failedIds.add(job.id);
        failures.push(`${job.document.originalFileName}: ${exception instanceof Error ? exception.message : "Could not accept"}`);
      }
    }
    for (const job of rejectJobs) {
      try { await rejectShopPrintJob(tokens.accessToken, job.id); }
      catch (exception) {
        failedIds.add(job.id);
        failures.push(`${job.document.originalFileName}: ${exception instanceof Error ? exception.message : "Could not reject"}`);
      }
    }

    await reload();
    if (failedIds.size > 0) {
      let retryJobs: ShopPrintJob[];
      try {
        const latest = await getShopPrintJobsPage(tokens.accessToken, 0, 100, queueSearch);
        retryJobs = latest.filter(job => failedIds.has(job.id) && job.status === "AWAITING_SHOP_APPROVAL");
      } catch {
        retryJobs = batchJobs.filter(job => failedIds.has(job.id));
      }
      const retryIds = new Set(retryJobs.map(job => job.id));
      setBatchReviewDecisions(current => {
        const next = { ...current };
        for (const job of batchJobs) {
          if (!retryIds.has(job.id)) delete next[job.id];
          else next[job.id] = accepted.has(job.id);
        }
        return next;
      });
      setBatchReviewJobs(retryJobs.length > 0 ? retryJobs : null);
      setBatchReviewRetry(retryJobs.length > 0);
      setBatchReviewRetryIds(retryJobs.map(job => job.id));
      if (retryJobs.length > 0) {
        setError(`Some files could not be updated: ${failures.join("; ")}. Only files still awaiting review remain open. Their previous accept/reject choices have been kept; retry these files without changing the other decisions.`);
      } else {
        setError(`The review requests returned errors, but the refreshed queue shows no failed files still awaiting review. Check the updated statuses before trying another action. Details: ${failures.join("; ")}`);
      }
    } else {
      setBatchReviewJobs(null);
      setBatchReviewRetry(false);
      setBatchReviewRetryIds(current => current.filter(id => !batchJobs.some(job => job.id === id)));
      setBatchReviewDecisions(current => {
        const next = { ...current };
        for (const job of batchJobs) delete next[job.id];
        return next;
      });
      setToast(`Batch review saved: ${acceptJobs.length} accepted and ${rejectJobs.length} rejected. You can review paid files separately to record refunds.`);
    }
    setBusyJobId(null);
  }

  async function recordFileRefund(job: ShopPrintJob, values: { refundMethod: "UPI" | "CASH"; refundReference: string | null; reason: string; rejectedPageNumbers: number[] }) {
    setBusyJobId(job.id);
    setError("");
    try {
      const result = await recordShopPrintRefund(tokens.accessToken, job.id, {
        ...values, confirmedSent: true, idempotencyKey: crypto.randomUUID()
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

  const navAction = (section: Section) => {
    const nextPath = shopPathForSection(section);
    if (window.location.pathname !== nextPath) {
      window.history.pushState({}, "", nextPath);
    }
    setActive(section);
    setSidebarOpen(false);
    setError("");
  };

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
        <div className="nav-label">SHOP WORKSPACE</div>
        <nav className="primary-nav" aria-label="Shop navigation">
          {sections.map(({ label, icon: Icon }) => (
            <button key={label} title={label} aria-label={label} className={active === label ? "nav-item active" : "nav-item"} onClick={() => navAction(label)}>
              <Icon size={18} strokeWidth={1.8}/><span>{label}</span>
            </button>
          ))}
        </nav>
        <div className="sidebar-bottom">
          <button type="button" className="plan-card" onClick={() => navAction("Subscription")}><div className="plan-icon"><ShieldCheck size={17}/></div><div><strong>{subscription?.plan?.name || (subscription?.subscription.planCode === "TRIAL" ? "Trial plan" : "Subscription")}</strong><span>{subscription ? `${subscription.subscription.status} · ends ${new Date(subscription.subscription.periodEndUtc).toLocaleDateString("en-IN")}` : "View plan and usage"}</span></div><ArrowRight size={14}/></button>
          <button className="nav-item support-link" onClick={() => navAction("Settings")}><LifeBuoy size={18}/><span>Help & support</span></button>
          <div className="profile-row">
            <div className="profile-avatar">{initials(profile?.ownerName || "Shop Owner")}</div>
            <div className="profile-copy"><strong>{profile?.ownerName || "Shop owner"}</strong><span>Shop owner</span></div>
            <button className="icon-button signout-button" aria-label="Sign out" title="Sign out" onClick={signOut}><LogOut size={16}/></button>
          </div>
        </div>
      </aside>
      {sidebarOpen && <button className="mobile-scrim" aria-label="Close navigation overlay" onClick={() => setSidebarOpen(false)}/>}

      <main className="main-panel">
        <header className="topbar">
          <div className="topbar-left">
            <button className="icon-button sidebar-toggle" aria-label={sidebarCollapsed ? "Expand navigation" : "Collapse navigation"} title={sidebarCollapsed ? "Expand navigation" : "Collapse navigation"} onClick={toggleSidebarCollapsed}>{sidebarCollapsed ? <PanelLeftOpen size={18}/> : <PanelLeftClose size={18}/>}</button>
            <button className="icon-button mobile-menu" aria-label="Open navigation" onClick={() => setSidebarOpen(true)}><Menu size={20}/></button>
            <div className="breadcrumb"><span>Shop workspace</span><span className="crumb-slash">/</span><strong>{active}</strong></div>
          </div>
          <div className="topbar-actions">
            <div className="connection-pill" role="status" aria-live="polite" title={cloudStatus === "connected" ? "The latest shop data refresh succeeded." : cloudStatus === "checking" ? "Checking your shop connection." : "The latest shop data refresh could not reach the cloud service."}>
              <span className="live-dot" style={{ opacity: cloudStatus === "unavailable" ? 0.45 : 1 }}/>
              <span>{cloudStatus === "connected" ? "Cloud connected" : cloudStatus === "checking" ? "Checking cloud" : "Cloud unavailable"}</span>
            </div>
            <button className="icon-button theme-button" aria-label={dark ? "Switch to light theme" : "Switch to dark theme"} onClick={() => setDark(!dark)}>{dark ? <Sun size={18}/> : <Moon size={18}/>}</button>
            <div className="service-status-row" aria-label="Live shop system status">
              <span className={"service-status "+apiConnectionState} title="API connection"><i/>API {apiConnectionState === "connected" ? "Connected" : apiConnectionState === "degraded" ? "Degraded" : apiConnectionState === "offline" ? "Offline" : "Checking"}</span>
              <span className={"service-status "+(onlineAgentCount > 0 ? "connected" : activeAgentCount > 0 ? "offline" : "checking")} title="Windows Agent heartbeat"><i/>Agent {onlineAgentCount > 0 ? onlineAgentCount+" online" : activeAgentCount > 0 ? "Offline" : "Not paired"}</span>
              <span className={"service-status "+(usablePrinterCount > 0 ? "connected" : offlinePrinterCount > 0 ? "offline" : "checking")} title="Active printer availability"><i/>Printers {usablePrinterCount > 0 ? usablePrinterCount+" available" : offlinePrinterCount > 0 ? offlinePrinterCount+" unavailable" : "Unavailable"}</span>
            </div>
            <div className="topbar-divider"/>
            <span className="topbar-product-tag"><span className="mode-indicator"/> {isDesktopApp() ? "Desktop workspace" : "Shop console"}</span>
          </div>
        </header>

        <div className="content-wrap">
          <div className="page-heading">
            <div><div className="eyebrow"><span className="eyebrow-line"/>SHOP WORKSPACE</div><h1>{active === "Overview" ? `Welcome, ${(profile?.ownerName || "there").split(" ")[0]}` : active}</h1><p>{active === "Overview" ? "Your shop setup and operations, all in one place." : pageDescription(active)}</p></div>
            <div className="heading-actions"><span className="date-chip"><Clock3 size={15}/>{today}</span>{active === "Printers" && <button className="primary-button" onClick={() => setModal("printer")}><Plus size={16}/> Add printer</button>}{active === "QR Stations" && <button className="primary-button" onClick={() => setModal("station")}><Plus size={16}/> Create QR station</button>}</div>
          </div>

          {error && <div className="inline-error" role="alert"><span>{error}</span><button onClick={() => setError("")} aria-label="Dismiss error"><X size={15}/></button></div>}
          {toast && <div className="inline-success" role="status"><Check size={15}/><span>{toast}</span><button onClick={() => setToast("")} aria-label="Dismiss message"><X size={15}/></button></div>}

          {active === "Overview" && <Overview profile={profile} printers={printers} stations={stations} printJobs={printJobs} pricing={pricing} agentDevices={agentDevices} paymentSettings={paymentSettings} onNavigate={navAction}/>}
          {active === "Printers" && <PrintersPage printers={printers} onAdd={() => setModal("printer")} onToggle={togglePrinter}/>}
          {active === "QR Stations" && <StationsPage stations={stations} profile={profile} onAdd={() => setModal("station")} onToggle={toggleStation} onGenerate={createFreshStationCode}/>}
          {active === "Print Agent" && <PrintAgentPage agents={agentDevices} enrollmentCode={enrollmentCode} busy={agentBusy} onGenerate={generateAgentCode} onRevoke={revokeAgent} desktop={isDesktopApp()} localAgentStatus={localAgentStatus} localAgentConflict={localAgentConflict} localAgentRevoked={localAgentRevoked} localAgentNeedsRepair={localAgentNeedsRepair} localAgentEnrollmentIssue={localAgentEnrollmentIssue} localAgentOwnershipChecking={localAgentOwnershipChecking} onConnect={()=>void connectLocalComputer()} onReconnectAfterRevocation={()=>void connectLocalComputer(false,agentDevices,true)}/>}
          {active === "Pricing" && pricing && <PricingPage pricing={pricing} onSave={savePricing} busy={busy}/>}
          {active === "Subscription" && subscription && <SubscriptionPage summary={subscription}/>}
          {active === "Payments" && paymentSettings && <PaymentsPage settings={paymentSettings} payments={payments} refunds={refunds} onSave={savePaymentSettings} onVerify={verifyPayment} onReject={rejectPayment} busy={busy} busyJobId={busyJobId}/>}
          {active === "Settings" && profile && <SettingsPage profile={profile} onSave={saveProfile} busy={busy}/>}
          {active === "Print Queue" && <PrintQueuePage jobs={printJobs} printers={printers} agentDevices={agentDevices} searchQuery={queueSearch} onSearch={searchPrintQueue} onPreview={previewJob} onApprove={approveJob} onReject={rejectJob} onRefund={job=>setRefundJob(job)} onReviewBatch={batchId=>{const waiting=printJobs.filter(job=>job.batchId===batchId&&job.status==="AWAITING_SHOP_APPROVAL");setBatchReviewRetry(waiting.some(job=>batchReviewRetryIds.includes(job.id)));setBatchReviewJobs(waiting);}} onHistory={viewJobHistory} onAssignPrinter={assignPrinter} onDispatch={dispatchPrintJob} onConfirmOutput={confirmPrintedOutput} onRetryPdfExport={retryPdfExport} onRefresh={refreshPrintQueue} onLoadOlder={loadOlderQueueJobs} hasMoreJobs={hasMoreQueueJobs} loadingOlderJobs={loadingOlderQueueJobs} busyJobId={busyJobId}/>} 
          {active === "Customers" && <CustomersPage token={tokens.accessToken}/>}
          {active === "Reports" && <ReportsPage token={tokens.accessToken}/>} 

          <footer className="app-footer"><span>© 2026 SmartPrint by QuantaEdge</span><span><span className="footer-status"/> API session authenticated</span><span>Made for print shops, with care.</span></footer>
        </div>
      </main>

      {modal === "printer" && <PrinterModal busy={busy} onClose={() => setModal(null)} onSubmit={submitPrinter}/>}
      {modal === "station" && <StationModal busy={busy} printers={printers.filter(printer => printer.isActive)} onClose={() => setModal(null)} onSubmit={submitStation}/>}
            {batchReviewJobs && <BatchReviewModal jobs={batchReviewJobs} busy={busyJobId==="batch-review"} decisions={batchReviewDecisions} retrying={batchReviewRetry} onDecision={(jobId,accepted)=>setBatchReviewDecisions(current=>({...current,[jobId]:accepted}))} onClose={()=>setBatchReviewJobs(null)} onPreview={previewJob} onApply={acceptedIds=>void applyBatchReview(batchReviewJobs,acceptedIds)}/>}
      {preview && <DocumentPreviewModal name={preview.name} url={preview.url} mediaType={preview.mediaType} job={preview.job} onApprove={()=>void approveJob(preview.job)} onReject={()=>void rejectJob(preview.job)} onRefund={()=>setRefundJob(preview.job)} onClose={() => { URL.revokeObjectURL(preview.url); setPreview(null); }}/>} 
      {eventHistory && <JobHistoryModal job={eventHistory.job} events={eventHistory.events} onClose={() => setEventHistory(null)}/>}
      {refundJob && <RefundModal job={refundJob} token={tokens.accessToken} busy={busyJobId===refundJob.id} onClose={()=>setRefundJob(null)} onSubmit={values=>void recordFileRefund(refundJob,values)}/>} 
      {actionDialog && <Modal title={actionDialog.title} subtitle="This action will be recorded against your shop account." onClose={closeActionDialog}>
        <div className="in-app-dialog-body"><p>{actionDialog.message}</p>{actionDialog.inputLabel&&<label className="dialog-input-label">{actionDialog.inputLabel}<textarea autoFocus rows={3} maxLength={300} value={actionDialogValue} onChange={event=>setActionDialogValue(event.target.value)} placeholder="Enter at least 3 characters"/></label>}</div>
        <div className="modal-actions"><button className="secondary-button" onClick={closeActionDialog}>Cancel</button><button className="primary-button" disabled={!!actionDialog.inputLabel&&actionDialogValue.trim().length<3} onClick={submitActionDialog}>{actionDialog.confirmLabel}</button></div>
      </Modal>}
    </div>
  );
}

function CustomersPage({ token }: { token: string }) {
  const [searchDraft, setSearchDraft] = useState("");
  const [search, setSearch] = useState("");
  const [page, setPage] = useState(1);
  const [refreshKey, setRefreshKey] = useState(0);
  const [result, setResult] = useState<ShopCustomerPage | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [selectedCustomer, setSelectedCustomer] = useState<ShopCustomer | null>(null);
  const [history, setHistory] = useState<ShopCustomerHistory | null>(null);
  const [historyLoading, setHistoryLoading] = useState(false);
  const [historyError, setHistoryError] = useState("");
  const historyRequest = useRef(0);
  const pageSize = 20;

  useEffect(() => {
    let current = true;
    setLoading(true);
    setError("");
    void getShopCustomers(token, { search, page, pageSize })
      .then(value => { if (current) setResult(value); })
      .catch(exception => {
        if (current) setError(exception instanceof Error ? exception.message : "Could not load shop customers.");
      })
      .finally(() => { if (current) setLoading(false); });
    return () => { current = false; };
  }, [token, search, page, pageSize, refreshKey]);

  async function openHistory(customer: ShopCustomer) {
    const request = ++historyRequest.current;
    setSelectedCustomer(customer);
    setHistory(null);
    setHistoryError("");
    setHistoryLoading(true);
    try {
      const value = await getShopCustomerHistory(token, customer.id);
      if (request === historyRequest.current) setHistory(value);
    } catch (exception) {
      if (request === historyRequest.current) {
        setHistoryError(exception instanceof Error ? exception.message : "Could not load this customer's print history.");
      }
    } finally {
      if (request === historyRequest.current) setHistoryLoading(false);
    }
  }

  function closeHistory() {
    historyRequest.current++;
    setSelectedCustomer(null);
    setHistory(null);
    setHistoryError("");
    setHistoryLoading(false);
  }

  const totalPages = Math.max(1, Math.ceil((result?.totalCount ?? 0) / pageSize));
  const firstRow = result?.totalCount ? (page - 1) * pageSize + 1 : 0;
  const lastRow = Math.min(page * pageSize, result?.totalCount ?? 0);

  return (
    <section className="customers-workspace">
      <div className="customers-intro">
        <div>
          <span className="reports-eyebrow">SHOP RELATIONSHIPS</span>
          <h2>Customer directory</h2>
          <p>Signed-in SmartPrint customers who have connected to this shop. Guest sessions stay anonymous.</p>
        </div>
        <div className="customers-total">
          <strong>{result?.totalCount ?? "—"}</strong>
          <span>linked customers</span>
        </div>
      </div>

      <form className="customers-toolbar" onSubmit={event => {
        event.preventDefault();
        setPage(1);
        setSearch(searchDraft.trim());
      }}>
        <label className="search-field customers-search">
          <Search size={14}/>
          <input value={searchDraft} onChange={event => setSearchDraft(event.target.value)}
            placeholder="Search customer name or email" aria-label="Search customer name or email"/>
        </label>
        <button className="primary-button" type="submit"><Search size={14}/> Search</button>
        <button className="secondary-button" type="button" onClick={() => setRefreshKey(value => value + 1)} disabled={loading}>
          <Activity size={14}/> Refresh
        </button>
      </form>

      {error && <div className="inline-error" role="alert">{error}</div>}
      {loading && !result ? <div className="panel reports-loading"><span className="button-spinner"/> Loading customers…</div> : null}
      {!loading && !error && result?.items.length === 0 && (
        <div className="panel customers-empty">
          <span className="customers-empty-icon"><Users size={24}/></span>
          <h3>{search ? "No matching customers" : "No signed-in customers yet"}</h3>
          <p>{search
            ? "Try another name or email search."
            : "When a customer signs in before scanning this shop's QR code, their shop-specific activity will appear here. Guest jobs are intentionally not identified or linked."}</p>
        </div>
      )}

      {result && result.items.length > 0 && (
        <div className="panel customers-table-panel">
          <div className="panel-heading customers-panel-heading">
            <div><h2>Customers connected to this shop</h2><p>Only activity created through this shop's QR sessions is included.</p></div>
            <span className="neutral-chip">{result.totalCount} total</span>
          </div>
          <div className="table-scroll">
            <table className="customers-table">
              <thead><tr><th>CUSTOMER</th><th>PRINT JOBS</th><th>COMPLETED</th><th>LAST QR SESSION</th><th>HISTORY</th></tr></thead>
              <tbody>
                {result.items.map(customer => (
                  <tr key={customer.id}>
                    <td><div className="customer-cell"><strong>{customer.displayName || "SmartPrint customer"}</strong><span>{customer.maskedEmail}</span></div></td>
                    <td><strong className="customers-number">{customer.printJobCount}</strong><small className="customers-subvalue">{customer.sessionCount} QR sessions</small></td>
                    <td><strong className="customers-number">{customer.completedJobCount}</strong><small className="customers-subvalue">output confirmed</small></td>
                    <td><span className="customers-date">{new Date(customer.lastSeenUtc).toLocaleString()}</span></td>
                    <td><button type="button" className="customers-history-button" onClick={() => void openHistory(customer)}>View history <ArrowRight size={13}/></button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="table-footer customers-pagination">
            <span>Showing <strong>{firstRow}–{lastRow}</strong> of <strong>{result.totalCount}</strong></span>
            <div><button type="button" disabled={page <= 1 || loading} onClick={() => setPage(value => Math.max(1, value - 1))}>← Previous</button>
              <span>{page} / {totalPages}</span>
              <button type="button" disabled={page >= totalPages || loading} onClick={() => setPage(value => Math.min(totalPages, value + 1))}>Next →</button></div>
          </div>
        </div>
      )}

      {selectedCustomer && (
        <Modal title={selectedCustomer.displayName || "SmartPrint customer"}
          subtitle={selectedCustomer.maskedEmail + " · Only this shop's print history"}
          onClose={closeHistory}>
          <div className="customer-history-modal">
            {historyLoading && <div className="reports-loading"><span className="button-spinner"/> Loading print history…</div>}
            {historyError && <div className="inline-error" role="alert">{historyError}</div>}
            {history && (
              <>
                <div className="customer-history-summary"><strong>{history.totalJobs}</strong><span>print jobs at this shop</span></div>
                {history.jobs.length === 0
                  ? <div className="empty-search">No print jobs are available for this customer yet.</div>
                  : <div className="customer-history-list">
                    {history.jobs.map(job => {
                      const netPaid = job.payment
                        ? Math.max(0, job.payment.amountMinor - job.payment.refundedAmountMinor)
                        : 0;
                      const tone = job.status === "COMPLETED" ? "green"
                        : job.status.includes("REJECT") || job.status === "FAILED" ? "red"
                        : job.status.includes("AWAITING") ? "amber" : "blue";
                      return (
                        <article className="customer-history-row" key={job.id}>
                          <div className="customer-history-heading">
                            <div><strong>{job.document.originalFileName}</strong><span>{job.jobNumber} · {new Date(job.createdUtc).toLocaleString()}</span></div>
                            <Status label={prettyStatus(job.status)} tone={tone}/>
                          </div>
                          <div className="customer-history-facts">
                            <span>{job.pageCount} pages · {job.copies} copies</span>
                            <strong>{formatMoney(job.quotedAmountMinor, job.currency)}</strong>
                          </div>
                          {job.payment && <div className="customer-history-payment">
                            {job.payment.status.replaceAll("_", " ")} · {job.payment.method} · Net recorded: {formatMoney(netPaid, job.payment.currency)}
                            {job.payment.refundedAmountMinor > 0 ? " · Refund recorded" : ""}
                          </div>}
                          <div className="customer-history-output">{job.outputType === "PDF_EXPORT"
                            ? (job.outputCompletedUtc ? "PDF export created and verified" : "PDF export not yet completed")
                            : (job.outputConfirmedUtc ? "Shop confirmed physical output" : "Physical output not yet confirmed")}</div>
                        </article>
                      );
                    })}
                  </div>}
              </>
            )}
          </div>
        </Modal>
      )}
    </section>
  );
}

function ReportsPage({ token }: { token: string }) {
  const [days, setDays] = useState<7 | 30 | 90>(30);
  const [summary, setSummary] = useState<ShopReportSummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [refreshKey, setRefreshKey] = useState(0);

  useEffect(() => {
    let current = true;
    setLoading(true);
    setError("");
    setSummary(null);
    void getShopReportSummary(token, days)
      .then(result => { if (current) setSummary(result); })
      .catch(exception => {
        if (current) {
          setError(exception instanceof Error ? exception.message : "Could not load shop reports.");
        }
      })
      .finally(() => { if (current) setLoading(false); });
    return () => { current = false; };
  }, [token, days, refreshKey]);

  const statusMaximum = Math.max(1, ...(summary?.jobsByStatus.map(item => item.count) ?? [0]));

  return (
    <section className="reports-workspace">
      <div className="reports-toolbar">
        <div><span className="reports-eyebrow">SHOP PERFORMANCE</span><h2>Operations report</h2><p>Live figures from this shop\u2019s recorded print jobs and verified collections.</p></div>
        <div className="reports-controls">
          <label className="reports-period">Period
            <select aria-label="Reporting period" value={days} onChange={event => setDays(Number(event.target.value) as 7 | 30 | 90)}>
              <option value={7}>Last 7 days</option><option value={30}>Last 30 days</option><option value={90}>Last 90 days</option>
            </select>
          </label>
          <button className="secondary-button reports-refresh" type="button" onClick={() => setRefreshKey(value => value + 1)} disabled={loading}><Activity size={14}/> Refresh</button>
        </div>
      </div>
      {error && <div className="inline-error" role="alert">{error}</div>}
      {loading && !summary ? <div className="panel reports-loading"><span className="button-spinner"/> Loading shop activity…</div> : null}
      {!loading && !summary && !error ? <div className="panel reports-loading">No report data is available for this period yet.</div> : null}
      {summary && <>
        <div className="stat-grid reports-stat-grid">
          <Metric icon={<FileText size={18}/>} label="Print jobs" value={summary.totalJobs.toLocaleString("en-IN")} note={`Created in the last ${summary.periodDays} days`} accent="blue"/>
          <Metric icon={<Check size={18}/>} label="Output confirmed" value={summary.completedJobs.toLocaleString("en-IN")} note="Physically confirmed by shop operators" accent="mint"/>
          <Metric icon={<Printer size={18}/>} label="Pages requested" value={summary.pagesRequested.toLocaleString("en-IN")} note="Page count × requested copies" accent="violet"/>
          <Metric icon={<Wallet size={18}/>} label="Payments to review" value={summary.pendingPaymentCount.toLocaleString("en-IN")} note="Open UPI/cash attempts across all dates" accent="amber"/>
        </div>
        <div className="reports-grid">
          <section className="panel reports-panel">
            <div className="panel-heading"><div><h2>Job status breakdown</h2><p>Current status of jobs created during this period</p></div><span className="neutral-chip">{summary.totalJobs} jobs</span></div>
            {summary.jobsByStatus.length === 0 ? <div className="reports-empty">No print jobs were created in this period.</div> :
              <div className="report-status-list">{summary.jobsByStatus.map(item => <div className="report-status-row" key={item.status}>
                <div className="report-status-label"><span>{prettyStatus(item.status)}</span><strong>{item.count.toLocaleString("en-IN")}</strong></div>
                <div className="report-status-track"><span style={{ width: `${Math.max(2, item.count / statusMaximum * 100)}%` }}/></div>
              </div>)}</div>}
          </section>
          <section className="panel reports-panel">
            <div className="panel-heading"><div><h2>Verified collections</h2><p>Shop-confirmed payments, grouped separately by currency</p></div><span className="neutral-chip">Verified only</span></div>
            {summary.verifiedCollectionsByMethod.length === 0 ? <div className="reports-empty">No payments have been verified in this period. Unverified UPI references are not included.</div> :
              <div className="report-collection-list">{summary.verifiedCollectionsByMethod.map(item => <div className="report-collection-row" key={`${item.currency}-${item.method}`}>
                <div><strong>{item.method === "UPI" ? "UPI" : "Cash at counter"}</strong><small>{item.paymentCount} verified {item.paymentCount === 1 ? "payment" : "payments"}</small></div>
                <strong className="report-collection-amount">{formatMoney(item.amountMinor, item.currency)}</strong>
              </div>)}</div>}
          </section>
          <section className="panel reports-panel">
            <div className="panel-heading"><div><h2>Refunds recorded</h2><p>Refunds the shop confirmed sending during this period</p></div><span className="neutral-chip">Recorded as sent</span></div>
            {summary.refundsRecordedByMethod.length === 0 ? <div className="reports-empty">No refunds were recorded as sent during this period.</div> :
              <div className="report-collection-list">{summary.refundsRecordedByMethod.map(item => <div className="report-collection-row" key={`refund-${item.currency}-${item.method}`}>
                <div><strong>{item.method === "UPI" ? "UPI refund" : "Cash refund"}</strong><small>{item.refundCount} recorded {item.refundCount === 1 ? "refund" : "refunds"}</small></div>
                <strong className="report-collection-amount">{formatMoney(item.amountMinor, item.currency)}</strong>
              </div>)}</div>}
          </section>
        </div>
        {summary.outputUnconfirmedJobs > 0 && <div className="report-caution"><ShieldCheck size={16}/><span><strong>{summary.outputUnconfirmedJobs} output-unconfirmed {summary.outputUnconfirmedJobs === 1 ? "job" : "jobs"}.</strong> Check the actual pages before any manual retry. These jobs are not counted as completed.</span></div>}
        <div className="reports-footnote">Report window: {new Date(summary.fromUtc).toLocaleString("en-IN")} – {new Date(summary.toUtc).toLocaleString("en-IN")}. Job counts use creation time, gross collections use payment verification time, and refunds use the date the shop recorded sending them. Pages requested are not a claim of pages physically printed. A UPI reference is excluded until the shop verifies the receipt.</div>
      </>}
    </section>
  );
}

function Overview({ profile, printers, stations, printJobs, pricing, agentDevices, paymentSettings, onNavigate }: {
  profile: ShopProfile | null; printers: PrinterDevice[]; stations: QrStation[];
  printJobs: ShopPrintJob[]; pricing: ShopPricingProfile | null;
  agentDevices: AgentDevice[]; paymentSettings: ShopPaymentSettings | null;
  onNavigate: (section: Section) => void;
}) {
  const activePrinters = printers.filter(printer => printer.isActive);
  const todayKey = new Date().toDateString();
  const jobsToday = printJobs.filter(job => new Date(job.createdUtc).toDateString() === todayKey);
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
        <Metric icon={<Printer size={18}/>} label="Registered printers" value={String(printers.length)} note={`${activePrinters.length} enabled for use`} accent="blue"/>
        <Metric icon={<QrCode size={18}/>} label="QR stations" value={String(stations.length)} note={`${stations.filter(station=>station.isEnabled).length} currently enabled`} accent="violet"/>
        <Metric icon={<FileText size={18}/>} label="Print jobs today" value={String(jobsToday.length)} note="Real customer uploads" accent="amber"/>
        <Metric icon={<Wallet size={18}/>} label="Quotes awaiting review" value={String(quotesAwaiting.length)} note="Quotes are not collected payments" accent="mint"/>
      </div>
      <div className="dashboard-grid">
        <section className="panel activity-empty-panel">
          <div className="panel-heading"><div><h2>Customer activity</h2><p>Recent uploads and quote requests from your QR stations</p></div><span className="neutral-chip">{printJobs.length} recent jobs</span></div>
          {printJobs.length===0 ? <div className="activity-empty-content"><div className="activity-empty-art"><div className="activity-art-sheet"><FileText size={25}/><i/><i/><i/></div><div className="activity-art-printer"><Printer size={27}/></div><span className="activity-art-spark">✦</span></div><h3>Ready when your first customer is</h3><p>Set your prices and share a QR station. Customer uploads will appear here as soon as they arrive.</p><button className="text-action" onClick={() => onNavigate(printers.length ? "QR Stations" : "Printers")}>{printers.length ? "Open QR stations" : "Add your first printer"} <span>→</span></button></div> :
            <div className="activity-job-list">
              {printJobs.slice(0,4).map(job=><div className="activity-job-row" key={job.id}><div className="activity-job-icon"><FileText size={16}/></div><div className="activity-job-copy"><strong>{job.jobNumber}</strong><span>{job.document.originalFileName} · {job.pageCount} pages</span></div><div className="activity-job-status"><Status label={prettyStatus(job.status)} tone={job.status==="AWAITING_CUSTOMER_APPROVAL"||job.status==="AWAITING_SHOP_APPROVAL"?"amber":"blue"}/><small>{new Date(job.createdUtc).toLocaleTimeString([], {hour:"2-digit",minute:"2-digit"})}</small></div></div>)}
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

function PrintersPage({ printers, onAdd, onToggle }: { printers: PrinterDevice[]; onAdd:()=>void; onToggle:(printer:PrinterDevice)=>void }) {
  return (
    <section className="panel collection-panel">
      <div className="collection-header"><div><h2>Your printers</h2><p>Manage physical printers and local PDF export destinations. Disabling a printer blocks new assignments; it does not cancel a Windows spooler job already accepted.</p></div><button className="primary-button" onClick={onAdd}><Plus size={16}/> Add printer</button></div>
      {printers.length===0 ? <EmptyModule icon={<Printer size={24}/>} title="No printers registered yet" text="Add your first printer. It will appear here as awaiting local-agent discovery until the Windows Print Agent reports its capabilities and live status." actionLabel="Add your first printer" onAction={onAdd}/> :
      <div className="printer-grid">{printers.map(printer=><article className="printer-card" key={printer.id}><div className="printer-card-top"><div className="printer-card-icon"><Printer size={20}/></div><Status label={printer.status} tone={printer.status.toLowerCase()==="ready"?"green":"slate"}/></div><h3>{printer.outputMode==="PDF_EXPORT"?"PDF export · "+printer.displayName:printer.displayName}</h3><p>{printer.outputMode==="PDF_EXPORT"?"Direct local PDF generation · no Windows Save As dialog":printer.manufacturer+" · "+printer.model}</p><div className="printer-meta"><span>{printer.outputMode==="PDF_EXPORT"?"PDF export destination":printer.connectionType}{printer.isDefault?(printer.outputMode==="PDF_EXPORT"?" · Default output destination":" · Default Windows queue"):""}</span><span>{printer.queueName || printer.connectionAddress || "Connection not verified"}</span></div><div className="printer-card-footer"><span className={printer.isActive?"enabled-label":"disabled-label"}>{printer.isActive?"Enabled":"Disabled"}</span><button className="subtle-button" onClick={()=>onToggle(printer)}>{printer.isActive?"Disable":"Enable"}</button></div></article>)}</div>}
    </section>
  );
}

function StationsPage({ stations, profile, onAdd, onToggle, onGenerate }: {
  stations:QrStation[]; profile:ShopProfile|null; onAdd:()=>void; onToggle:(station:QrStation)=>void; onGenerate:(station:QrStation)=>void;
}) {
  const [printingStationId,setPrintingStationId]=useState<string|null>(null);
  useEffect(()=>{
    if(!printingStationId)return;
    let cancelled=false;
    const onAfterPrint=()=>setPrintingStationId(null);
    const printSelectedStation=async()=>{
      const card=document.getElementById(`qr-station-${printingStationId}`);
      const images=Array.from(card?.querySelectorAll("img")??[]);
      await Promise.all(images.map(async image=>{
        if(image.complete)return;
        try{await image.decode();}catch{/* A missing optional logo must not block printing. */}
      }));
      await document.fonts.ready.catch(()=>undefined);
      await new Promise<void>(resolve=>{
        window.requestAnimationFrame(()=>window.requestAnimationFrame(()=>resolve()));
      });
      if(!cancelled)window.print();
    };
    window.addEventListener("afterprint",onAfterPrint);
    void printSelectedStation();
    return ()=>{
      cancelled=true;
      window.removeEventListener("afterprint",onAfterPrint);
    };
  },[printingStationId]);

  return (
    <section className="panel collection-panel">
      <div className="collection-header"><div><h2>Customer QR stations</h2><p>Each station has a permanent public link. Every scan creates a separate short-lived customer session.</p></div><button className="primary-button" onClick={onAdd}><Plus size={16}/> Create station</button></div>
      {stations.length===0 ? <EmptyModule icon={<QrCode size={24}/>} title="Your first QR station starts here" text="Create a station for a counter or printer. The QR code identifies the station; every scan gets a separate expiring customer session." actionLabel="Create QR station" onAction={onAdd}/> :
      <div className="station-grid">{stations.map(station=><article id={`qr-station-${station.id}`} className={printingStationId===station.id?"station-card print-active":"station-card"} key={station.id}><div className="station-card-head"><div className="station-icon"><QrCode size={18}/></div><div className="station-head-copy"><strong>{station.displayName}</strong><span>{station.isEnabled?"Accepting new sessions":"Disabled"}</span></div><Status label={station.isEnabled?"Enabled":"Disabled"} tone={station.isEnabled?"green":"slate"}/></div>
        <div className="station-qr-area">{station.publicUrl ? <div className="qr-paper">
          <div className="qr-shop-brand">{profile?.logoUrl?<img src={profile.logoUrl} alt={profile.displayName+" logo"}/>:<span className="qr-shop-initial">{(profile?.displayName||"S").slice(0,1).toUpperCase()}</span>}<div><strong>{profile?.displayName||station.displayName}</strong><small>DOCUMENT PRINTING STATION</small></div></div>
          <div className="qr-code-frame"><QRCode value={station.publicUrl} size={172} level="H" fgColor="#101c35" bgColor="#ffffff"/></div>
          <span className="qr-scan-label">SCAN TO PRINT</span>
          <span className="qr-scan-hint">Scan to upload documents and send them to this shop</span>
          <div className="qr-shop-station">{station.displayName}</div>
          <div className="qr-powered"><small>POWERED BY</small><img src="/branding/quantaedge-logo.svg" alt="QuantaEdge"/></div>
        </div> : <div className="qr-missing"><QrCode size={34}/><strong>QR code not stored</strong><span>This older station needs a one-time QR replacement. Its existing printed code cannot be recovered from the old hash.</span><button className="secondary-button" onClick={()=>onGenerate(station)}>Reissue permanent QR</button></div>}</div>
        <div className="station-foot"><span>Code ending ···{station.publicCodeHint}</span><button className="subtle-button" onClick={()=>onToggle(station)}>{station.isEnabled?"Disable station":"Enable station"}</button></div>
        {station.publicUrl && <div className="station-actions"><button className="secondary-button" onClick={()=>void copyText(station.publicUrl!)}> <Copy size={14}/> Copy link</button><button className="secondary-button" onClick={()=>setPrintingStationId(station.id)}>Print QR sign</button></div>}
      </article>)}</div>}
    </section>
  );
}



function PrintAgentPage({agents,enrollmentCode,busy,onGenerate,onRevoke,desktop,localAgentStatus,localAgentConflict,localAgentRevoked,localAgentNeedsRepair,localAgentEnrollmentIssue,localAgentOwnershipChecking,onConnect,onReconnectAfterRevocation}:{
  agents:AgentDevice[];
  enrollmentCode:{code:string;expiresUtc:string}|null;
  busy:boolean;
  onGenerate:()=>void;
  onRevoke:(agent:AgentDevice)=>void;
  desktop:boolean;
  localAgentStatus:DesktopAgentStatus|null;
  localAgentConflict:boolean;
  localAgentRevoked:boolean;
  localAgentNeedsRepair:boolean;
  localAgentEnrollmentIssue:{code:string|null;message:string}|null;
  localAgentOwnershipChecking:boolean;
  onConnect:()=>void;
  onReconnectAfterRevocation:()=>void;
}) {
  const { release, state: releaseState } = usePublicRelease();
  const windowsInstallerUrl = release?.windowsAvailable ? getPublicDownloadPath("windows") : null;
  const connected = Boolean(localAgentStatus?.paired && agents.some(agent =>
    !agent.revokedUtc && agent.id === localAgentStatus.agentDeviceId
  ));
  return <div className="agent-page">
    <section className="panel agent-pairing-panel">
      {desktop ? <div className="desktop-agent-connect">
        <div className="agent-intro">
          <div className="agent-hero-icon"><Monitor size={22}/></div>
          <div><div className="eyebrow"><span className="eyebrow-line"/>THIS COMPUTER</div><h2>Your print computer</h2><p>SmartPrint connects this computer to your shop automatically. The background print service stays active even when you close this window.</p></div>
        </div>
        <div className="desktop-agent-status">
          <span className={connected ? "desktop-agent-indicator connected" : localAgentConflict ? "desktop-agent-indicator conflict" : "desktop-agent-indicator"} />
          <div><strong>{connected ? "Connected to this shop" : localAgentRevoked ? "Disconnected by the shop owner" : localAgentNeedsRepair ? "Connection needs repair" : localAgentEnrollmentIssue ? (localAgentConflict ? "Existing shop pairing needs revocation" : "Connection needs attention") : localAgentConflict ? "Connected to another shop" : localAgentOwnershipChecking ? "Checking shop registration" : localAgentStatus?.serviceReachable ? "Ready to connect" : "Print service not responding"}</strong>
            <small>{connected ? (localAgentStatus?.localApiReachable === false ? "Connected to your shop. Cloud printing remains available; local-network uploads are reconnecting automatically." : "Printer discovery and job hand-off are managed by the SmartPrint service.") : localAgentRevoked ? "This computer was revoked and will not reconnect automatically. Select Reconnect this computer when your shop is ready; no pairing code is needed." : localAgentNeedsRepair ? "Windows could not read this computer's saved pairing. Check the paired-computer list below; revoke its old entry if it is still active, then select Repair connection. No code entry is needed." : localAgentEnrollmentIssue ? localAgentEnrollmentIssue.message : localAgentConflict ? "For security, this computer cannot be moved until its previous shop owner revokes it." : localAgentOwnershipChecking ? "SmartPrint is verifying this computer's saved registration with your shop. It will not transfer or re-enroll it while ownership is uncertain." : localAgentStatus?.serviceReachable ? "Your owner session will authorize a one-time connection. No pairing code is shown." : "SmartPrint is starting or reconnecting its background service. It will retry automatically."}</small>
          </div>
        </div>
        <div className="desktop-agent-actions">
          {localAgentConflict ? (
            <button className="primary-button" disabled={busy} onClick={onReconnectAfterRevocation}>
              {busy ? <span className="button-spinner"/> : <><RotateCcw size={15}/> I’ve had the previous shop revoke this computer</>}
            </button>
          ) : localAgentOwnershipChecking ? (
            <span role="status" aria-live="polite">Checking shop registration…</span>
          ) : localAgentRevoked ? (
            <button className="primary-button" disabled={busy} onClick={onConnect}>
              {busy ? <span className="button-spinner"/> : <><RotateCcw size={15}/> Reconnect this computer</>}
            </button>
          ) : localAgentNeedsRepair ? (
            <button className="primary-button" disabled={busy} onClick={onConnect}>
              {busy ? <span className="button-spinner"/> : <><RotateCcw size={15}/> Repair connection</>}
            </button>
          ) : (
            <button className="primary-button" disabled={busy || connected || Boolean(localAgentStatus?.paired)} onClick={onConnect}>
              {busy ? <span className="button-spinner"/> : <><Monitor size={15}/>{localAgentStatus?.serviceReachable ? "Connect this computer" : "Retry connection"}</>}
            </button>
          )}
          <span>Your shop data remains protected by your account and this computer's Windows security.</span>
        </div>
      </div> : <>
      <div className="agent-intro">
        <div className="agent-hero-icon"><Monitor size={22}/></div>
        <div><div className="eyebrow"><span className="eyebrow-line"/>LOCAL PRINT CONNECTIVITY</div><h2>Connect your shop computer</h2><p>Pair the Windows computer where your printers are installed. The agent reports printer inventory and health without exposing the agent secret in the browser.</p></div>
      </div>
      <div className="agent-steps">
        <div><span>01</span><strong>Generate a pairing code</strong><small>One-time code, valid for 10 minutes.</small></div>
        <div><span>02</span><strong>Run the guided Windows installer</strong><small>Enter the code in the setup wizard. No PowerShell commands are required.</small></div>
        <div><span>03</span><strong>Confirm the connection</strong><small>Return here and wait for the computer heartbeat and printer discovery.</small></div>
      </div>
      {enrollmentCode&&<div className="agent-code-panel">
        <div><span>ONE-TIME PAIRING CODE</span><strong>{enrollmentCode.code}</strong><small>Expires {new Date(enrollmentCode.expiresUtc).toLocaleTimeString([], {hour:"2-digit",minute:"2-digit"})}</small></div>
        <button className="secondary-button" onClick={()=>void copyText(enrollmentCode.code)}><Copy size={14}/> Copy code</button>
      </div>}
      <div className="agent-command-panel">
        <div><span>GUIDED WINDOWS INSTALLER</span><strong>Download and run SmartPrint Windows Setup on the shop computer.</strong><small>Run setup as administrator, enter the one-time pairing code in its wizard, and let it install and start the background service.</small></div>
        {windowsInstallerUrl
          ? <a className="secondary-button" href={windowsInstallerUrl}><Monitor size={14}/> Download Windows installer</a>
          : <span className="subtle-button" aria-disabled="true">{releaseState === "loading" ? "Checking installer availability…" : releaseState === "error" ? "Installer availability temporarily unavailable" : "Windows installer not published"}</span>}
      </div>
      <div className="agent-security-note"><ShieldCheck size={16}/><span>The pairing code expires after 10 minutes. The agent secret is returned only once and stored on the Windows machine using DPAPI LocalMachine encryption. Revoke a computer here if it is replaced or lost.</span></div>
      <div className="agent-generate-row"><span>Pairing codes are single use. Generating a new one does not revoke already paired computers.</span><button className="primary-button" disabled={busy} onClick={onGenerate}>{busy?<span className="button-spinner"/>:<><Plus size={15}/> Generate pairing code</>}</button></div>
      </>}
    </section>
    <section className="panel agent-device-panel">
      <div className="collection-header"><div><h2>Paired Windows computers</h2><p>Agent connection history and the number of discovered printer queues.</p></div><span className="setup-count">{agents.filter(agent=>!agent.revokedUtc).length} active</span></div>
      {agents.length===0?<div className="agent-empty"><Monitor size={25}/><strong>No Windows computer paired yet</strong><span>Generate a pairing code above and run the agent on the computer connected to your printers.</span></div>:
        <div className="agent-device-list">{agents.map(agent=><article className="agent-device-card" key={agent.id}>
          <div className="agent-device-icon"><Monitor size={19}/></div>
          <div className="agent-device-copy"><strong>{agent.deviceName}</strong><span>{agent.printerCount} printer {agent.printerCount===1?"queue":"queues"} discovered · Paired {new Date(agent.createdUtc).toLocaleDateString()}</span><small>{agent.lastSeenUtc?("Last contact "+new Date(agent.lastSeenUtc).toLocaleString()):"No heartbeat received yet"}</small>{agent.localEndpoints?.length?<small>LAN status endpoint: {agent.localEndpoints.join(" · ")}</small>:<small>LAN endpoint not detected · check that the computer is on a private shop network</small>}</div>
          <Status label={agent.revokedUtc?"Revoked":agent.status} tone={agent.revokedUtc?"red":agent.status==="Online"?"green":"slate"}/>
          {!agent.revokedUtc&&<button className="subtle-button reject-action" disabled={busy} onClick={()=>onRevoke(agent)}>Revoke</button>}
        </article>)}</div>}
    </section>
  </div>;
}

function PricingPage({ pricing, onSave, busy }: {
  pricing: ShopPricingProfile;
  onSave: (values: {
    monoPerSideMinor: number;
    colourPerSideMinor: number;
    monoDuplexPerSheetMinor: number;
    colourDuplexPerSheetMinor: number;
    minimumChargeMinor: number;
  }) => void;
  busy: boolean;
}) {
  const [mono,setMono]=useState((pricing.monoPerSideMinor/100).toFixed(2));
  const [colour,setColour]=useState((pricing.colourPerSideMinor/100).toFixed(2));
  const [monoDuplex,setMonoDuplex]=useState((pricing.monoDuplexPerSheetMinor/100).toFixed(2));
  const [colourDuplex,setColourDuplex]=useState((pricing.colourDuplexPerSheetMinor/100).toFixed(2));
  const [minimum,setMinimum]=useState((pricing.minimumChargeMinor/100).toFixed(2));
  useEffect(()=>{
    setMono((pricing.monoPerSideMinor/100).toFixed(2));
    setColour((pricing.colourPerSideMinor/100).toFixed(2));
    setMonoDuplex((pricing.monoDuplexPerSheetMinor/100).toFixed(2));
    setColourDuplex((pricing.colourDuplexPerSheetMinor/100).toFixed(2));
    setMinimum((pricing.minimumChargeMinor/100).toFixed(2));
  },[pricing]);
  const toMinor=(value:string)=>Math.round(Number(value)*100);
  const valid=[mono,colour,monoDuplex,colourDuplex,minimum].every(value=>value.trim()!==""&&Number.isFinite(Number(value))&&Number(value)>=0);
  return <section className="panel settings-panel pricing-page">
    <div className="settings-intro"><div className="settings-icon"><Wallet size={20}/></div><div><h2>Print rate card</h2><p>Set the prices your customers see before they authorise a job.</p></div><span className={pricing.isConfigured?"pricing-status configured":"pricing-status"}>{pricing.isConfigured?"Active rate card":"Not configured"}</span></div>
    <div className="pricing-disclaimer"><ShieldCheck size={16}/><span>Prices are stored in paise and calculated by the server. Updating this rate card affects new quotes; existing quotes retain their saved price and pricing version.</span></div>
    <form className="settings-form" onSubmit={event=>{
      event.preventDefault();
      if(!valid||toMinor(mono)<=0||toMinor(colour)<=0)return;
      onSave({
        monoPerSideMinor:toMinor(mono),
        colourPerSideMinor:toMinor(colour),
        monoDuplexPerSheetMinor:toMinor(monoDuplex),
        colourDuplexPerSheetMinor:toMinor(colourDuplex),
        minimumChargeMinor:toMinor(minimum)
      });
    }}>
      <div className="pricing-group-heading"><strong>Single-sided printing</strong><span>Price per A4 printed side</span></div>
      <div className="pricing-input-grid">
        <label>Black & white <span className="price-input-wrap"><span>₹</span><input type="number" min="0.01" max="1000" step="0.01" value={mono} onChange={e=>setMono(e.target.value)} required/></span></label>
        <label>Colour <span className="price-input-wrap"><span>₹</span><input type="number" min="0.01" max="1000" step="0.01" value={colour} onChange={e=>setColour(e.target.value)} required/></span></label>
      </div>
      <div className="pricing-group-heading"><strong>Double-sided printing</strong><span>Price per physical A4 sheet (two sides)</span></div>
      <div className="pricing-input-grid">
        <label>Black & white duplex <span className="price-input-wrap"><span>₹</span><input type="number" min="0" max="1000" step="0.01" value={monoDuplex} onChange={e=>setMonoDuplex(e.target.value)}/></span></label>
        <label>Colour duplex <span className="price-input-wrap"><span>₹</span><input type="number" min="0" max="1000" step="0.01" value={colourDuplex} onChange={e=>setColourDuplex(e.target.value)}/></span></label>
      </div>
      <div className="pricing-group-heading"><strong>Minimum order</strong><span>Applied if the calculated print total is lower</span></div>
      <label>Minimum job charge <span className="price-input-wrap minimum-price"><span>₹</span><input type="number" min="0" max="100000" step="0.01" value={minimum} onChange={e=>setMinimum(e.target.value)}/></span></label>
      <div className="settings-summary"><ShieldCheck size={16}/><span>Customer payment gateway setup is separate and is not active yet. This screen configures print quotations only; no money is collected here.</span></div>
      <div className="settings-actions"><button className="primary-button" disabled={busy||!valid||toMinor(mono)<=0||toMinor(colour)<=0}>{busy?<span className="button-spinner"/>:"Save print prices"}</button></div>
    </form>
    <div className="pricing-version-row"><span>Rate card version</span><strong>v{pricing.version}</strong><span>Last updated</span><strong>{new Date(pricing.updatedUtc).toLocaleString()}</strong></div>
  </section>;
}


function PaymentsPage({settings,payments,refunds,onSave,onVerify,onReject,busy,busyJobId}:{
  settings:ShopPaymentSettings;
  payments:ShopPayment[];
  refunds:ShopRefund[];
  onSave:(values:{upiEnabled:boolean;upiId:string|null;payeeName:string|null;cashEnabled:boolean})=>void;
  onVerify:(payment:ShopPayment)=>void;
  onReject:(payment:ShopPayment)=>void;
  busy:boolean;
  busyJobId:string|null;
}) {
  const [upiEnabled,setUpiEnabled]=useState(settings.upiEnabled);
  const [upiId,setUpiId]=useState(settings.upiId||"");
  const [payeeName,setPayeeName]=useState(settings.payeeName||"");
  const [cashEnabled,setCashEnabled]=useState(settings.cashEnabled);
  useEffect(()=>{
    setUpiEnabled(settings.upiEnabled);
    setUpiId(settings.upiId||"");
    setPayeeName(settings.payeeName||"");
    setCashEnabled(settings.cashEnabled);
  },[settings.upiEnabled,settings.upiId,settings.payeeName,settings.cashEnabled]);

  const pending=payments.filter(payment=>payment.status==="AWAITING_CUSTOMER_PAYMENT"||payment.status==="CUSTOMER_REPORTED"||payment.status==="CASH_AWAITING_PAYMENT");
  return <div className="payments-workspace">
    <section className="panel payment-settings-panel">
      <div className="collection-header"><div><h2>Shop payment methods</h2><p>Customer print payments go directly to your own UPI account or are received at your counter.</p></div><span className="setup-count">{(upiEnabled?1:0)+(cashEnabled?1:0)} enabled</span></div>
      <div className="payment-method-card">
        <div className="payment-method-heading"><div className="payment-method-icon"><Wallet size={19}/></div><div><strong>UPI · QR and payment app</strong><small>Configure your shop's own UPI ID. Payments are not auto-verified.</small></div><label className="switch"><input type="checkbox" checked={upiEnabled} onChange={event=>setUpiEnabled(event.target.checked)}/><span/></label></div>
        {upiEnabled&&<div className="payment-settings-fields">
          <label>Shop UPI ID<input value={upiId} onChange={event=>setUpiId(event.target.value)} placeholder="yourshop@bank"/></label>
          <label>Payee / business name<input value={payeeName} onChange={event=>setPayeeName(event.target.value)} maxLength={120} placeholder="Name shown in customer's UPI app"/></label>
        </div>}
      </div>
      <div className="payment-method-card">
        <div className="payment-method-heading"><div className="payment-method-icon"><Printer size={19}/></div><div><strong>Cash at counter</strong><small>Allow customers to select cash and pay in person.</small></div><label className="switch"><input type="checkbox" checked={cashEnabled} onChange={event=>setCashEnabled(event.target.checked)}/><span/></label></div>
      </div>
      <div className="payment-security-note"><ShieldCheck size={16}/><span>SmartPrint by QuantaEdge does not receive, hold or route your customer print revenue. Verify every UPI reference against your own bank/UPI app; cash must be physically received. SmartPrint platform subscription charges are billed separately.</span></div>
      <div className="payment-settings-footer"><span>Last updated {new Date(settings.updatedUtc).toLocaleString()}</span><button className="primary-button" disabled={busy||(!upiEnabled&&!cashEnabled)||(upiEnabled&&(!upiId.trim()||!payeeName.trim()))} onClick={()=>onSave({upiEnabled,upiId:upiEnabled?upiId.trim():null,payeeName:upiEnabled?payeeName.trim():null,cashEnabled})}>{busy?<span className="button-spinner"/>:"Save payment methods"}</button></div>
    </section>
    <section className="panel payment-review-panel">
      <div className="collection-header"><div><h2>Payment verification</h2><p>Review UPI references and confirm cash collected before a job becomes payment-authorised.</p></div><span className="setup-count">{pending.length} need review</span></div>
      {payments.length===0?<EmptyModule icon={<Wallet size={24}/>} title="No print payments yet" text="When customers choose UPI or cash for a shop-approved job, the payment attempt will appear here."/>:
      <div className="payment-record-list">{payments.map(payment=><article className="payment-record" key={payment.id}>
        <div className="payment-record-head"><div><strong>{payment.jobNumber}</strong><small>{payment.documentName}</small></div><div className="payment-record-amount">{formatMoney(payment.amountMinor,payment.currency)}</div></div>
        <div className="payment-record-meta"><span>{payment.method}</span><span>{new Date(payment.createdUtc).toLocaleString()}</span><Status label={payment.status.replaceAll("_"," ")} tone={payment.status==="VERIFIED"?"green":payment.status==="REJECTED"?"red":payment.status==="CUSTOMER_REPORTED"||payment.status==="CASH_AWAITING_PAYMENT"?"amber":"slate"}/></div>
        {payment.method==="UPI"&&<div className="payment-reference-details"><span>Transaction reference</span><strong>{payment.paymentReference||"Customer has not submitted a reference"}</strong><small>Payee snapshot: {payment.payeeNameSnapshot||"Not recorded"} · {payment.upiIdSnapshot||"No UPI ID snapshot"}</small></div>}
        {payment.rejectionReason&&<div className="payment-rejection-note">Reason: {payment.rejectionReason}</div>}
        {(payment.status==="AWAITING_CUSTOMER_PAYMENT"||payment.status==="CUSTOMER_REPORTED"||payment.status==="CASH_AWAITING_PAYMENT")&&<div className="payment-review-actions">{payment.status==="AWAITING_CUSTOMER_PAYMENT"&&<button className="secondary-button reject-action" disabled={busy||busyJobId===payment.printJobId} onClick={()=>onReject(payment)}>Cancel attempt</button>}{payment.status==="CUSTOMER_REPORTED"&&<button className="secondary-button reject-action" disabled={busy||busyJobId===payment.printJobId} onClick={()=>onReject(payment)}>Reject report</button>}<button className="primary-button" disabled={busy||busyJobId===payment.printJobId} onClick={()=>onVerify(payment)}>{busyJobId===payment.printJobId?<span className="button-spinner"/>:payment.method==="UPI"?"Verify in bank app":"Confirm cash received"}</button></div>}
        {payment.status==="VERIFIED"&&<div className="payment-verified-note"><Check size={14}/> Verified {payment.verifiedUtc?new Date(payment.verifiedUtc).toLocaleString():"by shop"}</div>}
      </article>)}</div>}
    </section>
    <section className="panel refund-audit-panel">
      <div className="collection-header"><div><h2>Refund audit log</h2><p>Every shopkeeper-recorded file refund, with its exact amount, return method, reason and operator.</p></div><span className="setup-count">{refunds.length} recorded</span></div>
      {refunds.length===0?<EmptyModule icon={<RotateCcw size={24}/>} title="No refunds recorded" text="When a shopkeeper rejects a paid file and records its return, the audit entry will appear here."/>:
      <div className="payment-record-list">{refunds.map(refund=><article className="payment-record refund-record" key={refund.id}>
        <div className="payment-record-head"><div><strong>{refund.jobNumber}</strong><small>{refund.documentName}</small></div><div className="payment-record-amount">{formatMoney(refund.amountMinor,refund.currency)}</div></div>
        <div className="payment-record-meta"><span>{refund.method} refund</span><span>{new Date(refund.refundedUtc).toLocaleString()}</span><Status label="Recorded as sent" tone="green"/></div>
        <div className="refund-record-details"><span>Shopkeeper</span><strong>{refund.refundedByName}</strong><span>Reason</span><strong>{refund.reason}</strong>{refund.reference&&<><span>Refund reference</span><strong>{refund.reference}</strong></>}</div>
      </article>)}</div>}
    </section>
  </div>;
}

function PrintQueuePage({ jobs, printers, agentDevices, searchQuery, onSearch, onPreview, onApprove, onReject, onRefund, onReviewBatch, onHistory, onAssignPrinter, onDispatch, onConfirmOutput, onRetryPdfExport, onRefresh, onLoadOlder, hasMoreJobs, loadingOlderJobs, busyJobId }: {
  jobs: ShopPrintJob[];
  printers: PrinterDevice[];
  agentDevices: AgentDevice[];
  searchQuery: string;
  onSearch: (query: string) => Promise<void>;
  onPreview: (job: ShopPrintJob) => void;
  onApprove: (job: ShopPrintJob) => void;
  onReject: (job: ShopPrintJob) => void;
  onRefund: (job: ShopPrintJob) => void;
  onReviewBatch: (batchId: string) => void;
  onHistory: (job: ShopPrintJob) => void;
  onAssignPrinter: (job: ShopPrintJob, printerDeviceId: string) => void;
  onDispatch: (job: ShopPrintJob) => void;
  onConfirmOutput: (job: ShopPrintJob) => void;
  onRetryPdfExport: (job: ShopPrintJob) => void;
  onRefresh: () => Promise<void> | void;
  onLoadOlder: () => Promise<void> | void;
  hasMoreJobs: boolean;
  loadingOlderJobs: boolean;
  busyJobId: string | null;
}) {
  const [autoRefresh,setAutoRefresh]=useState(true);
  const [refreshSeconds,setRefreshSeconds]=useState(10);
  const [searchInput,setSearchInput]=useState(searchQuery);
  const [expandedBatchKeys,setExpandedBatchKeys]=useState<Set<string>>(()=>new Set());
  useEffect(()=>setSearchInput(searchQuery),[searchQuery]);
  const refreshRunning=useRef(false);
  const refreshCallback=useRef(onRefresh);
  refreshCallback.current=onRefresh;

  async function refreshQueue() {
    if(refreshRunning.current)return;
    refreshRunning.current=true;
    try { await refreshCallback.current(); }
    finally { refreshRunning.current=false; }
  }

  useEffect(()=>{
    if(!autoRefresh)return;
    const timer=window.setInterval(()=>void refreshQueue(),Math.max(5,refreshSeconds)*1000);
    return ()=>window.clearInterval(timer);
  },[autoRefresh,refreshSeconds]);

  const reviewBatches=Array.from(new Map(jobs.filter(job=>job.batchId&&job.status==="AWAITING_SHOP_APPROVAL").map(job=>[job.batchId!,job])).keys());
  const onlineAgentIds = new Set(agentDevices.filter(agent =>
    !agent.revokedUtc && agent.status === "Online" && !!agent.lastSeenUtc &&
    Date.parse(agent.lastSeenUtc) > Date.now() - 2 * 60 * 1000
  ).map(agent => agent.id));
  const dispatchablePrinters = printers.filter(printer =>
    isPrinterAvailable(printer, onlineAgentIds)
  );
  const batchMap=new Map<string,{key:string;batchId:string|null;jobs:ShopPrintJob[]}>();
  for(const job of jobs) {
    const key=job.batchId??`single:${job.id}`;
    const group=batchMap.get(key)??{key,batchId:job.batchId??null,jobs:[]};
    group.jobs.push(job);
    batchMap.set(key,group);
  }
  const queueBatches=Array.from(batchMap.values());

  return <section className="panel queue-panel live-queue-panel">
    <div className="collection-header"><div><h2>Customer print jobs</h2><p>Preview every file separately. Each file can be accepted or rejected without changing the rest of its upload batch. Refunds are available before handoff to the print computer.</p></div><div className="queue-header-actions"><span className="setup-count">{queueBatches.length} batches shown</span><form className="queue-search-form" onSubmit={event=>{event.preventDefault();void onSearch(searchInput);}}><input aria-label="Search all print jobs" type="search" maxLength={120} placeholder="Job, file or printer" value={searchInput} onChange={event=>setSearchInput(event.target.value)}/><button className="secondary-button" type="submit"><Search size={13}/> Search</button></form><button className="secondary-button queue-refresh-button" onClick={()=>void refreshQueue()} disabled={refreshRunning.current}><Activity size={13}/> Refresh queue</button></div></div>
    <div className="queue-refresh-controls">
      <label className="queue-auto-toggle"><input type="checkbox" checked={autoRefresh} onChange={event=>setAutoRefresh(event.target.checked)}/><span className="queue-toggle-track"/><strong>Auto refresh</strong><small>{autoRefresh?"On":"Off"}</small></label>
      <label className="queue-refresh-interval">Refresh every <input type="number" min={5} max={3600} step={1} value={refreshSeconds} onChange={event=>setRefreshSeconds(Math.max(5,Math.min(3600,Number(event.target.value)||5)))}/> seconds</label>
      <span className="queue-refresh-note">{autoRefresh?"Queue refreshes automatically while this page is open.":"Automatic refresh is off. Use Refresh queue whenever needed."}</span>
    </div>
    {reviewBatches.length>0&&<div className="batch-review-strip"><div><strong>{reviewBatches.length} batch(es) waiting for review</strong><span>All files start accepted. Uncheck only documents the shop should reject.</span></div>{reviewBatches.map(batchId=><button key={batchId} className="secondary-button" onClick={()=>onReviewBatch(batchId)}>Review batch · {jobs.filter(job=>job.batchId===batchId&&job.status==="AWAITING_SHOP_APPROVAL").length} files</button>)}</div>}
    {jobs.length===0 ? <EmptyModule icon={<FileText size={24}/>} title="Your queue is clear" text="When a customer uploads a document through one of your QR stations, the job and its current state will appear here."/> :
      <div className="table-scroll"><table><thead><tr><th>JOB</th><th>DOCUMENT</th><th>PRINT SETTINGS</th><th>QUOTE</th><th>PRINTER / LOCAL QUEUE</th><th>STATUS</th><th>CREATED</th><th>ACTIONS</th></tr></thead><tbody>
        {queueBatches.map(batch=>{
          const first=batch.jobs[0];
          const waiting=batch.jobs.filter(job=>job.status==="AWAITING_SHOP_APPROVAL");
          const folders=Array.from(new Set(batch.jobs.map(job=>job.batchGroupName??"Loose files")));
          const statusLabels=Array.from(new Set(batch.jobs.map(job=>job.status)));
          const settings=Array.from(new Set(batch.jobs.map(job=>`${job.colourMode} · ${job.isDuplex?"Double":"Single"}-sided`)));
          const payableJobs=batch.jobs.filter(job=>job.status!=="REJECTED"&&job.status!=="REFUNDED");
          const totalCopies=payableJobs.reduce((sum,job)=>sum+job.copies,0);
          const totalAmount=payableJobs.reduce((sum,job)=>sum+Math.max(0,job.quotedAmountMinor),0);
          const printersInBatch=Array.from(new Set(batch.jobs.map(job=>job.printerName||"Not assigned")));
          const expanded=expandedBatchKeys.has(batch.key);
          const label=statusLabels.length===1?prettyStatus(statusLabels[0]):`${statusLabels.length} states`;
          const tone=statusLabels.every(status=>status==="COMPLETED"||status==="LOCAL_QUEUE_READY")?"green":statusLabels.some(status=>status==="AWAITING_SHOP_APPROVAL"||status==="AWAITING_CUSTOMER_APPROVAL"||status==="OUTPUT_UNCONFIRMED")?"amber":"blue";
          return <Fragment key={batch.key}>
            <tr className="queue-batch-summary-row">
              <td><strong className="job-id">{batch.batchId?"Batch · "+first.jobNumber:first.jobNumber}</strong><small className="batch-summary-sub">{batch.jobs.length} file(s)</small></td>
              <td><div className="customer-cell"><strong>{folders.length} folder group(s)</strong><span>{folders.slice(0,3).join(" · ")}{folders.length>3?" · +"+(folders.length-3):""}</span></div></td>
              <td><div className="detail-cell"><strong>{totalCopies} copies</strong><span>{settings.length===1?settings[0]:"Mixed settings"}</span></div></td>
              <td className="amount-cell">{totalAmount>0?formatMoney(totalAmount,first.currency):"Not quoted"}</td>
              <td><div className="queue-printer-cell"><strong>{printersInBatch.length===1?printersInBatch[0]:"Multiple printers"}</strong><small>{waiting.length} awaiting review</small></div></td>
              <td><Status label={label} tone={tone}/></td>
              <td className="time-cell">{new Date(first.createdUtc).toLocaleString()}</td>
              <td><div className="queue-actions">
                {waiting.length>0&&batch.batchId&&<button className="subtle-button approve-action" disabled={busyJobId==="batch-review"} onClick={()=>onReviewBatch(batch.batchId!)}>Preview & review</button>}
                <button className="subtle-button" onClick={()=>setExpandedBatchKeys(current=>{const next=new Set(current);if(next.has(batch.key))next.delete(batch.key);else next.add(batch.key);return next;})}>{expanded?"Hide files":"Files"}</button>
              </div></td>
            </tr>
            {expanded&&<tr className="queue-batch-detail-row"><td colSpan={8}><div className="queue-batch-files">{batch.jobs.map(job=><article className="queue-batch-file-card" key={job.id}>
              <div className="queue-batch-file-main">
                <div><strong>{job.document.originalFileName}</strong><small>{job.batchGroupName??"Loose files"} · {job.pageCount} pages × {job.copies} · {job.colourMode} · {job.isDuplex?"Double-sided":"Single-sided"}</small></div>
                <Status label={prettyStatus(job.status)} tone={job.status==="REJECTED"?"red":job.status==="COMPLETED"||job.status==="LOCAL_QUEUE_READY"?"green":job.status==="AWAITING_SHOP_APPROVAL"?"amber":"blue"}/>
                <strong className="amount-cell">{job.quotedAmountMinor>0?formatMoney(job.quotedAmountMinor,job.currency):"—"}</strong>
              </div>
              <div className="queue-batch-file-actions">
                <button className="subtle-button" disabled={busyJobId===job.id} onClick={()=>onPreview(job)}>Preview</button>
                <button className="subtle-button" disabled={busyJobId===job.id} onClick={()=>onHistory(job)}>History</button>
                {job.status==="AWAITING_SHOP_APPROVAL"&&<><button className="subtle-button approve-action" disabled={busyJobId===job.id} onClick={()=>onApprove(job)}>Accept</button><button className="subtle-button reject-action" disabled={busyJobId===job.id} onClick={()=>onReject(job)}>Reject</button></>}
                {job.status==="PAID_AUTHORISED"&&(job.payment?.status==="VERIFIED"||job.payment?.status==="PARTIALLY_REFUNDED")&&!job.localQueueReceivedUtc&&!job.claimedByAgentDeviceId&&<button className="subtle-button reject-action" disabled={busyJobId===job.id} onClick={()=>onRefund(job)}>Review pages / refund</button>}
                {(job.status==="PAID_AUTHORISED"||job.status==="LOCAL_QUEUE_CLAIMED")&&<select aria-label={"Assign printer for "+job.jobNumber} value={job.printerDeviceId||""} disabled={busyJobId===job.id||dispatchablePrinters.length===0||!!job.localQueueReceivedUtc||(job.status==="LOCAL_QUEUE_CLAIMED"&&!!job.agentLeaseExpiresUtc&&new Date(job.agentLeaseExpiresUtc).getTime()>Date.now())} onChange={event=>onAssignPrinter(job,event.target.value)}>
                  <option value="">{dispatchablePrinters.length===0?"No live agent printer":"Select printer"}</option>
                  {dispatchablePrinters.map(printer=><option key={printer.id} value={printer.id}>{printer.outputMode==="PDF_EXPORT"?"Save PDF · ":""}{printer.displayName} · {printer.status}</option>)}
                </select>}
                {job.status==="LOCAL_QUEUE_READY"&&<button className="subtle-button approve-action" disabled={busyJobId===job.id} onClick={()=>onDispatch(job)}>{job.outputMode==="PDF_EXPORT"?"Save PDF":"Start printing"}</button>}
                {job.status==="OUTPUT_UNCONFIRMED"&&job.outputMode==="PDF_EXPORT"&&<button className="subtle-button approve-action" disabled={busyJobId===job.id} onClick={()=>onRetryPdfExport(job)}>Save PDF again</button>}
                {(job.status==="SPOOLER_ACCEPTED"||job.status==="PRINTING"||(job.status==="OUTPUT_UNCONFIRMED"&&job.outputMode!=="PDF_EXPORT"))&&<button className="subtle-button approve-action" disabled={busyJobId===job.id||(job.status!=="OUTPUT_UNCONFIRMED"&&!!spoolerStatusWarning(job.spoolerStatus,job.printerStatus))} onClick={()=>onConfirmOutput(job)}>Confirm output</button>}
              </div>
            </article>)}</div></td></tr>}
          </Fragment>;
        })}
      </tbody></table></div>}
    {hasMoreJobs && <div className="queue-load-older"><button className="secondary-button" type="button" onClick={() => void onLoadOlder()} disabled={loadingOlderJobs}>{loadingOlderJobs ? <span className="button-spinner"/> : <ArrowRight size={14}/>} Load older jobs</button></div>}
  </section>;
}



function BatchReviewModal({jobs,busy,decisions,retrying,onDecision,onClose,onPreview,onApply}:{
  jobs:ShopPrintJob[];
  busy:boolean;
  decisions:Record<string,boolean>;
  retrying:boolean;
  onDecision:(jobId:string,accepted:boolean)=>void;
  onClose:()=>void;
  onPreview:(job:ShopPrintJob)=>void;
  onApply:(acceptedJobIds:string[])=>void;
}) {
  const acceptedIds=jobs.filter(job=>decisions[job.id]??true).map(job=>job.id);
  const acceptedCount=acceptedIds.length;
  const rejectedCount=jobs.length-acceptedCount;
  const acceptedTotal=jobs.filter(job=>decisions[job.id]??true).reduce((sum,job)=>sum+Math.max(0,job.quotedAmountMinor),0);
  const folders=Array.from(new Set(jobs.map(job=>job.batchGroupName??"Loose files")));
  return <Modal title={retrying?"Retry batch review":"Review batch"} subtitle={retrying?"Some decisions did not save. Only the files still awaiting review are shown; each keeps its previous accept/reject choice.":"All files start accepted. Uncheck only files the shop cannot print; undo any change before applying."} onClose={onClose}>
    <div className="batch-review-list">{folders.map(folderName=><section className="batch-review-folder" key={folderName}>
      <div className="batch-review-folder-head"><FolderOpen size={15}/><strong>{folderName}</strong><small>{jobs.filter(job=>(job.batchGroupName??"Loose files")===folderName).length} files</small></div>
      {jobs.filter(job=>(job.batchGroupName??"Loose files")===folderName).map((job,index)=>{
        const accepted=decisions[job.id]??true;
        return <div className={accepted?"batch-review-row accepted":"batch-review-row rejected"} key={job.id}>
          <label className="batch-review-checkbox"><input type="checkbox" checked={accepted} disabled={busy} onChange={event=>onDecision(job.id,event.target.checked)}/></label>
          <span className="batch-review-index">{index+1}</span>
          <div className="batch-review-file"><strong>{job.document.originalFileName}</strong><small>{job.pageCount} pages × {job.copies} · {job.colourMode} · {job.isDuplex?"Double-sided":"Single-sided"} · {formatMoney(job.quotedAmountMinor,job.currency)}</small><Status label={accepted?"Accepted":"Marked for rejection"} tone={accepted?"green":"red"}/></div>
          <button className="subtle-button" type="button" disabled={busy} onClick={()=>onPreview(job)}>Preview</button>
        </div>;
      })}
    </section>)}</div>
    <div className="batch-review-summary"><span><strong>{acceptedCount}</strong> accepted</span><span><strong>{rejectedCount}</strong> rejected</span><span><strong>{formatMoney(acceptedTotal,jobs[0]?.currency??"INR")}</strong> accepted value</span><small>Unpaid rejected files are excluded before payment. Decisions are kept if an individual update needs retrying.</small></div>
    <div className="modal-actions"><button className="secondary-button" onClick={onClose} disabled={busy}>Keep reviewing</button><button className="primary-button" disabled={busy||jobs.length===0} onClick={()=>onApply(acceptedIds)}>{busy?<span className="button-spinner"/>:"Apply review"}</button></div>
  </Modal>;
}

function JobHistoryModal({job,events,onClose}:{job:ShopPrintJob;events:ShopJobEvent[];onClose:()=>void}) {
  return <Modal title={job.jobNumber} subtitle={`Audit history · ${job.document.originalFileName}`} onClose={onClose}>
    <div className="shop-history-list">{events.length===0?<p>No recorded events yet.</p>:events.map(event=><div key={event.id}><i/><span><strong>{prettyStatus(event.newStatus)}</strong><small>{event.actorType==="ShopUser"?"Shop team":event.actorType==="Customer"?"Customer":"System"} · {new Date(event.createdUtc).toLocaleString()}</small>{event.reasonCode&&<small>{event.reasonCode.replaceAll("_"," ").toLowerCase()}</small>}</span></div>)}</div>
    <div className="modal-actions"><button className="secondary-button" onClick={onClose}>Close history</button></div>
  </Modal>;
}

function DocumentPreviewModal({name,url,mediaType,job,onClose,onApprove,onReject,onRefund}:{
  name:string;url:string;mediaType:string;job:ShopPrintJob;onClose:()=>void;onApprove:()=>void;onReject:()=>void;onRefund:()=>void;
}) {
  const isPdf=mediaType==="application/pdf";
  const previewSummary = `${job.pageCount} page(s) · ${job.copies} copy/copies requested. Preview displays the uploaded original once.`;
  return <Modal title={name} subtitle={job.batchId? `File review · Batch ${job.batchId.slice(0,8).toUpperCase()} · ${previewSummary}` : previewSummary} onClose={onClose}>
    <div className="shop-document-preview">{isPdf?<iframe src={url} title={name}/>:<img src={url} alt={name}/>}</div>
    {job.status==="AWAITING_SHOP_APPROVAL"&&<div className="file-review-note"><Check size={16}/><span>Review this file independently. Accepting or rejecting it will not change any other file in the batch.</span></div>}
    {job.status==="PAID_AUTHORISED"&&(job.payment?.status==="VERIFIED"||job.payment?.status==="PARTIALLY_REFUNDED")&&<div className="file-refund-note"><Wallet size={16}/><span>Paid amount: <strong>{formatMoney(job.payment.amountMinor,job.payment.currency)}</strong>. A refund can be recorded while this file has not yet been handed to the shop computer.</span></div>}
    <div className="modal-actions">
      {job.status==="AWAITING_SHOP_APPROVAL"&&<><button className="secondary-button reject-action" onClick={onReject}><X size={15}/> Reject this file</button><button className="primary-button" onClick={onApprove}><Check size={15}/> Accept this file</button></>}
      {job.status==="PAID_AUTHORISED"&&(job.payment?.status==="VERIFIED"||job.payment?.status==="PARTIALLY_REFUNDED")&&!job.localQueueReceivedUtc&&!job.claimedByAgentDeviceId&&<button className="secondary-button reject-action" onClick={onRefund}><RotateCcw size={15}/> Review pages & refund</button>}
      <button className="secondary-button" onClick={onClose}>Close preview</button>
    </div>
  </Modal>;
}

function RefundModal({job,token,busy,onClose,onSubmit}:{
  job:ShopPrintJob; token:string; busy:boolean; onClose:()=>void;
  onSubmit:(values:{refundMethod:"UPI"|"CASH";refundReference:string|null;reason:string;rejectedPageNumbers:number[]})=>void;
}) {
  const [rejectedPages,setRejectedPages]=useState<Set<number>>(()=>{
    try{return new Set<number>(JSON.parse(job.rejectedPageNumbersJson||"[]") as number[]);}catch{return new Set<number>();}
  });
  const [estimate,setEstimate]=useState<Awaited<ReturnType<typeof previewShopPrintRefund>>|null>(null);
  const [estimating,setEstimating]=useState(false);
  const [estimateError,setEstimateError]=useState("");
  const [method,setMethod]=useState<"UPI"|"CASH">(job.payment?.method==="CASH"?"CASH":"UPI");
  const [reference,setReference]=useState("");
  const [reason,setReason]=useState("Customer rejected selected pages after shop preview.");
  const [confirmed,setConfirmed]=useState(false);
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

  return <Modal title="Review pages and refund" subtitle="Every page starts accepted. Tick only pages to reject; selections can be undone before recording. Already refunded pages stay excluded." onClose={onClose}>
    <div className="refund-summary-card">
      <div><span>FILE</span><strong>{job.document.originalFileName}</strong></div>
      <div><span>VERIFIED PAYMENT</span><strong>{formatMoney(job.payment?.amountMinor||job.quotedAmountMinor,job.payment?.currency||job.currency)}</strong></div>
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
    {estimate&&additional>0&&<form className="refund-form" onSubmit={event=>{event.preventDefault();if(canSubmit)onSubmit({refundMethod:method,refundReference:method==="UPI"?reference.trim():null,reason:reason.trim(),rejectedPageNumbers:selectedPages});}}>
      <label>Refund returned by<select value={method} onChange={event=>setMethod(event.target.value as "UPI"|"CASH")}><option value="UPI">UPI transfer</option><option value="CASH">Cash returned at counter</option></select></label>
      {method==="UPI"&&<label>Refund transaction reference<input value={reference} onChange={event=>setReference(event.target.value)} maxLength={100} placeholder="Enter the refund UPI reference" required/></label>}
      <label>Reason for rejecting these pages<textarea rows={3} maxLength={300} value={reason} onChange={event=>setReason(event.target.value)} required/></label>
      <label className="refund-confirm-line"><input type="checkbox" checked={confirmed} onChange={event=>setConfirmed(event.target.checked)}/><span>I confirm that I have already returned the exact additional amount shown above. SmartPrint records the refund; it does not send money automatically.</span></label>
      <div className="modal-actions"><button type="button" className="secondary-button" onClick={onClose}>Cancel</button><button className="primary-button" disabled={busy||estimating||!canSubmit}>{busy?<span className="button-spinner"/>:"Record refund and reject selected pages"}</button></div>
    </form>}
    {estimate&&additional===0&&<div className="refund-form"><p>Select at least one additional page that produces a positive refund from the original quote. No page is rejected until a refund can be recorded.</p><div className="modal-actions"><button type="button" className="secondary-button" onClick={onClose}>Close</button></div></div>}
  </Modal>;
}

function rejectedPageList(json?:string) {
  try {
    const pages=JSON.parse(json||"[]");
    return Array.isArray(pages)&&pages.length>0?pages.join(", "):"selected pages";
  } catch {
    return "selected pages";
  }
}

function spoolerStatusWarning(status?: string | null, printerStatus?: string | null) {
  const spooler = (status || "").toUpperCase();
  const printer = (printerStatus || "").toUpperCase();
  if (spooler === "OFFLINE" || printer === "OFFLINE")
    return "Printer is offline. Reconnect it; Windows may resume this same queued job. Do not start a duplicate.";
  if (spooler === "PAPEROUT")
    return "The printer reports no paper. Refill it and check the same Windows queue before continuing.";
  if (spooler === "PAUSED" || printer === "PAUSED")
    return "The printer or Windows print queue is paused. Resume the existing job in Windows; do not submit another copy.";
  if (spooler === "PRINTER_UNAVAILABLE")
    return "The Agent cannot currently open this printer queue. Reconnect or restore the queue, then refresh this job.";
  if (spooler === "ERROR")
    return "Windows reports a printer error. Resolve it in the printer queue and inspect any pages already produced.";
  if (spooler === "USERINTERVENTION")
    return "The printer requires attention. Resolve the prompt at the device before checking the same queued job.";
  return "";
}
function prettyStatus(status:string) {
  const names:Record<string,string>={
    AWAITING_CONFIGURATION:"Configure options",
    AWAITING_CUSTOMER_APPROVAL:"Quote awaiting customer",
    QUOTE_EXPIRED:"Quote expired",
    AWAITING_SHOP_APPROVAL:"Awaiting shop approval",
    REJECTED:"Rejected",
    REFUNDED:"Fully refunded",
    PARTIALLY_REFUNDED:"Partially refunded",

    UPLOADED:"Uploaded",
    VALIDATING:"Validating",
    ANALYSING:"Analysing",
    AWAITING_PAYMENT:"Awaiting payment",
    AWAITING_COUNTER_PAYMENT:"Pay at counter",
    AUTHORISED:"Authorised",
    PAID_AUTHORISED:"Paid · authorised",
    LOCAL_QUEUE_CLAIMED:"Transferring locally",
    LOCAL_QUEUE_READY:"Local copy secured",
    PRINT_AUTHORISED:"Ready to print",
    PRINT_SUBMITTING:"Sending to printer",
    SPOOLER_ACCEPTED:"Accepted by spooler",
    PRINTING:"Printing",
    OUTPUT_UNCONFIRMED:"Output unconfirmed",
    PDF_EXPORTING:"Creating PDF",
    PDF_EXPORTED:"PDF saved",
    QUEUED:"Queued",
    SUBMITTING_TO_SPOOLER:"Sending to printer",
    COMPLETED:"Completed",
    FAILED:"Failed",
    CANCELLED:"Cancelled"
  };
  return names[status] || status.replaceAll("_"," ").toLowerCase().replace(/\\b\\w/g,letter=>letter.toUpperCase());
}
function isPrinterAvailable(printer: PrinterDevice, onlineAgentIds: ReadonlySet<string>, now = Date.now()) {
  if (!printer.isActive || !printer.agentDeviceId || !onlineAgentIds.has(printer.agentDeviceId)) return false;
  if (printer.outputMode === "PDF_EXPORT") return true;
  const lastSeen = printer.lastSeenUtc ? Date.parse(printer.lastSeenUtc) : Number.NaN;
  return Number.isFinite(lastSeen) && lastSeen > now - 2 * 60 * 1000 &&
    (printer.status === "Ready" || printer.status === "Busy");
}

function formatMoney(minor:number,currency:string) {
  return new Intl.NumberFormat("en-IN",{style:"currency",currency,minimumFractionDigits:2}).format(minor/100);
}
function formatBytes(bytes:number) {
  return bytes<1024*1024?`${(bytes/1024).toFixed(0)} KB`:`${(bytes/(1024*1024)).toFixed(2)} MB`;
}

function SettingsPage({ profile, onSave, busy }: { profile:ShopProfile; onSave:(v:{displayName:string;logoUrl:string|null;accentColor:string})=>void; busy:boolean }) {
  const [displayName,setDisplayName]=useState(profile.displayName);
  const [logoUrl,setLogoUrl]=useState(profile.logoUrl || "");
  const [accentColor,setAccentColor]=useState(profile.accentColor || "#7662E8");
  useEffect(()=>{setDisplayName(profile.displayName);setLogoUrl(profile.logoUrl||"");setAccentColor(profile.accentColor||"#7662E8");},[profile]);
  return <section className="panel settings-panel"><div className="settings-intro"><div className="settings-icon"><Settings size={20}/></div><div><h2>Shop identity</h2><p>These details appear to customers when they connect through your QR stations.</p></div></div>
    <form className="settings-form" onSubmit={event=>{event.preventDefault();onSave({displayName:displayName.trim(),logoUrl:logoUrl.trim()||null,accentColor});}}>
      <label>Customer-facing shop name<input value={displayName} onChange={e=>setDisplayName(e.target.value)} required maxLength={180}/></label>
      <label>Logo URL <span className="field-hint">Optional · secure HTTPS image URL</span><input type="url" value={logoUrl} onChange={e=>setLogoUrl(e.target.value)} placeholder="https://example.com/logo.png"/></label>
      <label>Brand accent colour<div className="colour-picker-row"><input type="color" value={accentColor} onChange={e=>setAccentColor(e.target.value)}/><input value={accentColor} onChange={e=>setAccentColor(e.target.value)} pattern="^#[0-9A-Fa-f]{6}$" required/></div></label>
      <div className="settings-summary"><ShieldCheck size={16}/><span>Standard Smart Print branding is active. Full white-label branding is a paid plan upgrade.</span></div>
      <div className="settings-actions"><button className="primary-button" disabled={busy}>{busy?<span className="button-spinner"/>:"Save shop identity"}</button></div>
    </form>
    <div className="settings-meta"><span>Shop slug</span><strong>{profile.slug}</strong><span>Currency</span><strong>{profile.currency}</strong><span>Time zone</span><strong>{profile.timeZone}</strong></div>
  </section>;
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
      <label>Default printer <span className="field-hint">Optional · can be changed before a job is printed</span><select value={printerDeviceId} onChange={e=>setPrinterDeviceId(e.target.value)}><option value="">No default printer</option>{printers.map(printer=><option key={printer.id} value={printer.id}>{printer.displayName} · {printer.manufacturer}</option>)}</select></label>
      <div className="modal-note"><QrCode size={15}/> The QR contains a public station identifier, not a permanent print authorisation. Every scan creates an expiring customer session.</div>
      <div className="modal-actions"><button type="button" className="secondary-button" onClick={onClose}>Cancel</button><button className="primary-button" disabled={busy||!displayName.trim()}>{busy?<span className="button-spinner"/>:"Create station"}</button></div>
    </form>
  </Modal>;
}

function Modal({title,subtitle,children,onClose}:{title:string;subtitle:string;children:ReactNode;onClose:()=>void}) {
  return <div className="modal-scrim" onMouseDown={e=>{if(e.target===e.currentTarget)onClose();}}><section className="modal-card" role="dialog" aria-modal="true" aria-label={title}><div className="modal-heading"><div><h2>{title}</h2><p>{subtitle}</p></div><button className="icon-button" onClick={onClose} aria-label="Close"><X size={18}/></button></div>{children}</section></div>;
}

function EmptyModule({icon,title,text,actionLabel,onAction}:{icon:ReactNode;title:string;text:string;actionLabel?:string;onAction?:()=>void}) {
  return <section className="panel empty-module"><div className="empty-module-icon">{icon}</div><h2>{title}</h2><p>{text}</p>{actionLabel&&onAction&&<button className="primary-button" onClick={onAction}>{actionLabel}<ArrowRight size={15}/></button>}</section>;
}

function SetupCheck({complete,title,detail,onClick}:{complete:boolean;title:string;detail:string;onClick:()=>void}) {
  return <button className="setup-check" onClick={onClick}><span className={complete?"check-circle complete":"check-circle"}>{complete?<Check size={14}/>:null}</span><span className="setup-check-copy"><strong>{title}</strong><small>{detail}</small></span><ArrowRight size={16}/></button>;
}
function Metric({icon,label,value,note,accent}:{icon:ReactNode;label:string;value:string;note:string;accent:string}) {
  return <section className="metric-card"><div className="metric-top"><div className={"metric-icon "+accent}>{icon}</div><span className="metric-dots"><i/><i/><i/></span></div><div className="metric-label">{label}</div><div className="metric-value">{value}</div><div className="metric-foot"><span>{note}</span></div></section>;
}
function Device({name,detail,state,tone}:{name:string;detail:string;state:string;tone:string}) {
  return <div className="device-row"><div className="device-glyph"><Printer size={16}/></div><div className="device-copy"><strong>{name}</strong><span>{detail}</span></div><Status label={state} tone={tone}/></div>;
}
function Status({label,tone}:{label:string;tone:string}) { return <span className={"status-badge "+tone}><i/>{label}</span>; }
function initials(value:string) { return value.trim().split(/\s+/).slice(0,2).map(part=>part[0]?.toUpperCase()||"").join("") || "SP"; }
function SubscriptionPage({ summary }: { summary: ShopSubscriptionSummary }) {
  const { subscription, plan, entitlements, usage } = summary;
  const status = subscription.status.toLowerCase();
  const statusTone = status === "active" ? "tone-success" : status === "trial" ? "tone-warning" : "tone-neutral";
  const planName = plan?.name ?? (subscription.planCode === "TRIAL" ? "Trial plan" : subscription.planCode);
  const periodStart = new Date(subscription.periodStartUtc).toLocaleDateString("en-IN", { day: "numeric", month: "short", year: "numeric" });
  const periodEnd = new Date(subscription.periodEndUtc).toLocaleDateString("en-IN", { day: "numeric", month: "short", year: "numeric" });
  const price = (amount: number | null | undefined, currency: string) =>
    amount == null ? "Not configured" : `${currency} ${(amount / 100).toLocaleString("en-IN", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;

  return (
    <section className="subscription-workspace">
      <div className="subscription-hero-grid">
        <section className="panel subscription-current-panel">
          <div className="subscription-panel-top">
            <span className="subscription-eyebrow">CURRENT SUBSCRIPTION</span>
            <span className={`subscription-status ${statusTone}`}>{subscription.status}</span>
          </div>
          <h2>{planName}</h2>
          <p className="subscription-plan-meta">
            {plan ? `Plan ${plan.code} · version ${plan.version} · ${plan.status}` : "A versioned plan has not yet been assigned to this shop."}
          </p>
          <div className="subscription-period">
            <div><span>Billing interval</span><strong>{subscription.billingInterval}</strong></div>
            <div><span>Current period</span><strong>{periodStart} – {periodEnd}</strong></div>
          </div>
          <div className="subscription-prices">
            <div><span>Monthly plan price</span><strong>{price(plan?.monthlyPriceMinor, plan?.currency ?? "INR")}</strong></div>
            <div><span>Annual plan price</span><strong>{price(plan?.annualPriceMinor, plan?.currency ?? "INR")}</strong></div>
          </div>
          {!entitlements.isAccessActive && (
            <div className="subscription-legacy-note subscription-expired-note"><ShieldCheck size={15}/><span>This trial or subscription is no longer active. New print requests are paused. Contact SmartPrint by QuantaEdge support to restore access.</span></div>
          )}
          {entitlements.isLegacyUnconfigured && entitlements.isAccessActive && (
            <div className="subscription-legacy-note"><ShieldCheck size={15}/><span>This subscription predates plan assignment. Existing shop access is being preserved until its terms are explicitly assigned.</span></div>
          )}
        </section>

        <section className="panel subscription-feature-panel">
          <div className="panel-heading"><div><h2>Included capabilities</h2><p>Resolved from the version assigned to your subscription</p></div></div>
          <div className="subscription-feature-list">
            <FeatureLine label="Cloud-assisted printing" enabled={entitlements.cloudAssistedPrintingEnabled}/>
            <FeatureLine label="White-label experience" enabled={entitlements.whiteLabelEnabled}/>
            <FeatureLine label="Advanced reports" enabled={entitlements.advancedReportsEnabled}/>
          </div>
          <div className="subscription-upgrade-note">
            <CreditCard size={15}/>
            <span>Plan changes and subscription billing are managed by the SmartPrint by QuantaEdge platform team. Customer print payments remain separate and continue to be handled by your shop.</span>
          </div>
        </section>
      </div>

      <section className="panel subscription-usage-panel">
        <div className="subscription-usage-heading">
          <div><h2>Usage and limits</h2><p>Live counts for this shop. Monthly print jobs use the UTC calendar month.</p></div>
          <span className="subscription-updated">Updated {new Date(summary.generatedUtc).toLocaleTimeString("en-IN", { hour: "2-digit", minute: "2-digit" })}</span>
        </div>
        <div className="subscription-usage-grid">
          <UsageMeter label="Registered printers" used={usage.printerCount} maximum={entitlements.maxPrinters}/>
          <UsageMeter label="Paired computers" used={usage.agentDeviceCount} maximum={entitlements.maxAgentDevices}/>
          <UsageMeter label="QR stations" used={usage.qrStationCount} maximum={entitlements.maxQrStations}/>
          <UsageMeter label="Print jobs this month" used={usage.monthlyPrintJobCount} maximum={entitlements.maxMonthlyPrintJobs}/>
        </div>
        <div className="subscription-document-limit">
          <span>Maximum cloud document size</span>
          <strong>{entitlements.maxDocumentBytes == null ? "No configured cap" : `${(entitlements.maxDocumentBytes / (1024 * 1024)).toLocaleString("en-IN", { maximumFractionDigits: 2 })} MB`}</strong>
        </div>
        <div className="subscription-usage-footnote">Disabled printers, QR stations and offline agents still count as registered resources unless removed or revoked. Revoked agent computers are excluded.</div>
      </section>
    </section>
  );
}

function UsageMeter({ label, used, maximum }: { label: string; used: number; maximum: number | null }) {
  const boundedMaximum = maximum == null ? null : Math.max(0, maximum);
  const percentage = boundedMaximum == null ? 0 : boundedMaximum > 0 ? Math.min(100, used / boundedMaximum * 100) : 100;
  return (
    <div className="subscription-usage-item">
      <div className="subscription-usage-item-top"><span>{label}</span><strong>{used.toLocaleString("en-IN")} <small>{boundedMaximum == null ? "/ unlimited" : `/ ${boundedMaximum.toLocaleString("en-IN")}`}</small></strong></div>
      <div className="subscription-progress-track"><span style={{ width: boundedMaximum == null ? "0%" : `${percentage}%` }}/></div>
      <small className="subscription-usage-caption">{boundedMaximum == null ? "No configured plan limit" : boundedMaximum === 0 ? "Not available on this plan" : `${Math.max(0, boundedMaximum - used).toLocaleString("en-IN")} remaining`}</small>
    </div>
  );
}

function FeatureLine({ label, enabled }: { label: string; enabled: boolean }) {
  return (
    <div className="subscription-feature-line">
      <span className={enabled ? "subscription-feature-indicator enabled" : "subscription-feature-indicator"}>{enabled ? <Check size={13}/> : <X size={13}/>}</span>
      <span>{label}</span>
      <strong>{enabled ? "Included" : "Not included"}</strong>
    </div>
  );
}

function pageDescription(section:Section) {
  const descriptions:Record<Section,string> = {
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
    "Settings":"Configure your shop identity and preferences."
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

function CustomerEntry({publicCode}:{publicCode:string}) {
  const [result,setResult]=useState<Awaited<ReturnType<typeof createCustomerSession>>|null>(null);
  const [error,setError]=useState("");
  const [loading,setLoading]=useState(true);
  const [localReady,setLocalReady]=useState(false);
  useEffect(()=>{setLocalReady(false);},[result?.localAgent?.agentDeviceId]);
  useEffect(()=>{
    let mounted=true;
    const stationKey=`qrtoprint.customer-session:${publicCode}`;
    const restore=async()=>{
      const storedToken=sessionStorage.getItem(stationKey) ||
        await getNativeCustomerPrintSession(publicCode);
      let existing:Awaited<ReturnType<typeof createCustomerSession>>|null=null;
      if(storedToken){
        try {
          existing=await resumeCustomerSession(storedToken,publicCode);
        } catch(exception) {
          if(exception instanceof ApiError&&exception.status===401){
            sessionStorage.removeItem(stationKey);
            sessionStorage.removeItem("qrtoprint.customer-session");
            await clearNativeCustomerPrintSession(publicCode);
          } else throw exception;
        }
      }
      const value=existing??await createCustomerSession(publicCode);
      // Wait for native encrypted persistence before presenting the connected session.
      // In a regular browser the bridge is absent and this safely becomes a no-op.
      const storedNatively=await saveNativeCustomerPrintSession(publicCode,value.sessionToken);
      if(!mounted)return;
      setResult(value);
      if(storedNatively)sessionStorage.removeItem(stationKey);
      else sessionStorage.setItem(stationKey,value.sessionToken);
      sessionStorage.removeItem("qrtoprint.customer-session");
    };
    void restore().catch(exception=>{
      if(mounted)setError(exception instanceof Error?exception.message:"This QR station is unavailable.");
    }).finally(()=>{if(mounted)setLoading(false);});
    return ()=>{mounted=false;};
  },[publicCode]);

  useEffect(()=>{
    if(!result)return;
    let active=true;
    let renewing=false;
    const renew=()=>{
      if(renewing)return;
      renewing=true;
      void renewCustomerPrintSession(result.sessionToken)
        .then(session=>{
          if(active){
            setResult(current=>current?.sessionToken===result.sessionToken
              ? {...current,expiresUtc:session.expiresUtc}
              : current);
          }
        })
        .catch(()=>{/* A temporary offline period is retried on the next foreground check. */})
        .finally(()=>{renewing=false;});
    };
    const checkExpiry=()=>{
      const expiry=Date.parse(result.expiresUtc);
      if(!Number.isFinite(expiry)||expiry<=Date.now()+10*60*1000)renew();
    };
    const timer=window.setInterval(renew,5*60*1000);
    const onVisibility=()=>{if(document.visibilityState==="visible")checkExpiry();};
    window.addEventListener("focus",checkExpiry);
    document.addEventListener("visibilitychange",onVisibility);
    return ()=>{
      active=false;
      window.clearInterval(timer);
      window.removeEventListener("focus",checkExpiry);
      document.removeEventListener("visibilitychange",onVisibility);
    };
  },[result?.sessionToken,result?.expiresUtc]);

  const nativeAppMode = navigator.userAgent.includes("SmartPrintAndroid/1");
  return <div className={nativeAppMode ? "customer-entry smartprint-native" : "customer-entry"}><div className="customer-entry-glow"/>
    <header className="customer-entry-brand"><div className="brand-mark"><QrCode size={21}/></div><div><div className="brand-name">smart<span>print</span></div><div className="brand-caption">BY QUANTAEDGE</div></div></header>
    <main className="customer-entry-card">
      {loading?<><div className="customer-entry-spinner"><span className="button-spinner"/></div><h1>{nativeAppMode?"Connecting…":"Connecting to your print shop"}</h1>{!nativeAppMode&&<p>Creating a secure, time-limited session for this QR station.</p>}</>:
      error?<><div className="entry-error-icon"><X size={23}/></div><div className="entry-eyebrow">STATION UNAVAILABLE</div><h1>{nativeAppMode?"Shop unavailable":"This QR station can't be reached"}</h1>{!nativeAppMode&&<p>{error}</p>}<button className="primary-button" onClick={()=>window.location.reload()}>Try again</button></>:
      result?<><div className="entry-success-icon"><Check size={23}/></div><div className="entry-eyebrow">SECURE SESSION CREATED</div><h1>{nativeAppMode?result.shop.displayName:"Welcome to "+result.shop.displayName}</h1><p>{nativeAppMode?"Shop connected.":"Your phone is connected to this shop's print station."}</p>
        <div className="entry-printer-card"><div className="entry-printer-icon"><Printer size={20}/></div><div><strong>{result.printer?.displayName||"Shop print counter"}</strong><span>{nativeAppMode?(result.printer?.status||"Printer not selected"):result.printer?result.printer.manufacturer+" · "+result.printer.model:"The shop will select a printer for your job."}</span></div><Status label={result.printer?.status||"Session ready"} tone="blue"/></div>
        <div className="entry-session-note"><ShieldCheck size={16}/><span>{nativeAppMode?"Secure session · Expires "+new Date(result.expiresUtc).toLocaleTimeString([], {hour:"2-digit",minute:"2-digit"}):"This session expires at "+new Date(result.expiresUtc).toLocaleTimeString([], {hour:"2-digit",minute:"2-digit"})+". Your document access stays limited to this shop session."}</span></div>
        <CustomerLocalConnection localAgent={result.localAgent} onReady={setLocalReady}/>
        <CustomerPrintFlow publicCode={publicCode} sessionToken={result.sessionToken} paymentOptions={result.paymentOptions} localAgent={result.localAgent} localReady={localReady}/>
      </>:null}
    </main>
    <footer className="customer-entry-footer">© 2026 SmartPrint by QuantaEdge · Secure customer printing</footer>
  </div>;
}


type CustomerLocalAgent = NonNullable<Awaited<ReturnType<typeof createCustomerSession>>["localAgent"]>;
type LocalAgentCheckStatus = "idle" | "checking" | "connected" | "unavailable" | "denied" | "insecure";

function isPrivateIpv4Hostname(hostname:string):boolean {
  const octets=hostname.split(".").map(Number);
  return octets.length===4 &&
    octets.every(part=>Number.isInteger(part)&&part>=0&&part<=255) &&
    (octets[0]===10 ||
     (octets[0]===172&&octets[1]>=16&&octets[1]<=31) ||
     (octets[0]===192&&octets[1]===168));
}

function isLocalOrLoopbackPortalOrigin(hostname:string):boolean {
  const value=hostname.toLowerCase().replace(/^\[|\]$/g,"");
  return value==="localhost" ||
    value.endsWith(".localhost") ||
    value==="127.0.0.1" ||
    value==="::1" ||
    isPrivateIpv4Hostname(value) ||
    value.endsWith(".local");
}

function buildSecureLocalPortalUrl():string|null {
  try {
    const url=new URL(window.location.href);
    if(url.protocol!=="http:"||url.port!=="3000"||!isPrivateIpv4Hostname(url.hostname))return null;
    url.protocol="https:";
    url.port="3443";
    return url.toString();
  } catch {
    return null;
  }
}

function CustomerLocalConnection({localAgent,onReady}:{localAgent:CustomerLocalAgent|null;onReady:(ready:boolean)=>void}) {
  const nativeAppMode = navigator.userAgent.includes("SmartPrintAndroid/1");
  const [permissionState,setPermissionState]=useState<PermissionState|"unknown">("unknown");
  const [checkStatus,setCheckStatus]=useState<LocalAgentCheckStatus>("idle");
  const [message,setMessage]=useState("");
  const securePortalUrl=buildSecureLocalPortalUrl();
  const localOriginDoesNotNeedLnaPermission=isLocalOrLoopbackPortalOrigin(window.location.hostname);

  function requireSecureContext() {
    setCheckStatus("insecure");
    onReady(false);
    setMessage(nativeAppMode ? "Secure HTTPS required." : securePortalUrl
      ? "This shop page is open over HTTP. Chrome blocks local-network requests from insecure pages without showing a permission prompt. Open the secure shop link below; the certificate must be trusted on this phone."
      : "This shop page is not a secure HTTPS context. Open the shop's HTTPS customer portal before checking local printing.");
  }

  async function checkConnection() {
    if(!localAgent)return;
    if(!window.isSecureContext) {
      requireSecureContext();
      return;
    }
    setCheckStatus("checking");
    onReady(false);
    setMessage(nativeAppMode ? "Checking shop Wi-Fi." : "Checking the shop computer. No document is sent by this check.");

    let validEndpointCount=0;
    for(const value of localAgent.endpoints.slice(0,8)) {
      let endpoint:URL;
      try {
        endpoint=new URL(value);
        if(endpoint.protocol!=="http:" ||
           endpoint.port!=="17842" ||
           endpoint.username!=="" ||
           endpoint.password!=="" ||
           endpoint.pathname!=="/" ||
           endpoint.search!=="" ||
           endpoint.hash!=="" ||
           !isPrivateIpv4Hostname(endpoint.hostname))continue;
      } catch {
        continue;
      }

      validEndpointCount++;
      const controller=new AbortController();
      const timeout=window.setTimeout(()=>controller.abort(),3500);
      try {
        // Start from the user's click when a browser permission prompt is required.
        const response=await fetch(`${endpoint.origin}/api/v1/local/status`,{
          method:"GET",
          mode:"cors",
          cache:"no-store",
          credentials:"omit",
          signal:controller.signal,
          targetAddressSpace:"local"
        } as RequestInit & {targetAddressSpace:"local"});
        if(!response.ok)continue;

        const status=await response.json() as {
          agentDeviceId?:string;
          protocolVersion?:number;
          status?:string;
        };
        if(status.agentDeviceId===localAgent.agentDeviceId &&
           status.protocolVersion===1 &&
           status.status==="LOCAL_ENDPOINT_READY") {
          setCheckStatus("connected");
          onReady(true);
          setMessage(nativeAppMode ? "Shop Wi-Fi ready · Local HTTP is not encrypted." : "This phone can reach the shop computer. Direct uploads use the shop's private Wi-Fi, but this local HTTP connection is not encrypted in transit. Choose cloud-assisted upload for HTTPS transfer.");
          return;
        }
      } catch {
        // Continue across the Agent's advertised private addresses.
      } finally {
        window.clearTimeout(timeout);
      }
    }

    setCheckStatus("unavailable");
    onReady(false);
    setMessage(nativeAppMode ? "Shop offline · Cloud upload available." : validEndpointCount===0
      ? "The shop Agent has not announced a usable local address. The regular cloud-assisted upload remains available."
      : "The shop Agent did not respond. Check that its Windows service is running, this network allows device-to-device traffic, and TCP 17842 is allowed on the laptop's Private firewall profile. The regular cloud-assisted upload remains available.");
  }

  useEffect(()=>{
    if(!localAgent?.endpoints.length)return;
    if(!window.isSecureContext) {
      requireSecureContext();
      return;
    }

    // A private-LAN/loopback portal talking to another private-LAN Agent
    // stays within the same or a less-private address space. Chrome does not
    // need to prompt for local-network permission for this local-to-local path.
    if(localOriginDoesNotNeedLnaPermission) {
      setPermissionState("unknown");
      return;
    }
    if(!navigator.permissions?.query)return;

    let active=true;
    let permissionStatus:PermissionStatus|null=null;
    const updatePermission=()=>{
      if(!active||!permissionStatus)return;
      setPermissionState(permissionStatus.state);
      if(permissionStatus.state==="denied") {
        setCheckStatus("denied");
        onReady(false);
        setMessage(nativeAppMode ? "Allow local network access." : "Chrome has blocked Local network access for this public HTTPS site. On Android, open the site information icon beside the address bar → Permissions → Local network access → Allow, then reload this QR page.");
      } else if(permissionStatus.state==="prompt") {
        setCheckStatus("idle");
        setMessage(nativeAppMode ? "Tap Check Wi-Fi." : "Tap Check shop Wi-Fi to request local-network access from Chrome.");
      }
    };

    void navigator.permissions.query({name:"local-network" as PermissionName})
      .then(status=>{
        if(!active)return;
        permissionStatus=status;
        status.addEventListener("change",updatePermission);
        updatePermission();
        if(status.state==="granted")void checkConnection();
      })
      .catch(()=>{if(active)setPermissionState("unknown");});

    return ()=>{
      active=false;
      permissionStatus?.removeEventListener("change",updatePermission);
    };
  },[localAgent?.agentDeviceId,onReady]);

  if(!localAgent?.endpoints.length)return null;

  const label=checkStatus==="checking"?"Checking…":
    checkStatus==="connected"?(nativeAppMode?"Connected":"Shop computer reachable"):
    checkStatus==="denied"?(nativeAppMode?"Access blocked":"Browser permission blocked"):
    checkStatus==="insecure"?"HTTPS required":
    checkStatus==="unavailable"?"Not reachable":"Not checked";
  const tone=checkStatus==="connected"?"green":
    checkStatus==="unavailable"||checkStatus==="denied"?"red":"blue";

  return <section className="entry-local-agent">
    <div className="entry-local-agent-head">
      <div><strong>{nativeAppMode?"Shop Wi-Fi":"Shop Wi-Fi connection"}</strong><span>Optional local connection check</span></div>
      <Status label={label} tone={tone}/>
    </div>
    {!nativeAppMode&&<p>Check whether this phone can reach the shop's local print computer. This check does not upload your document.</p>}
    {checkStatus==="insecure"
      ? <div className="entry-local-agent-message">
          <small>{message}</small>
          {securePortalUrl&&<a className="customer-flow-button" href={securePortalUrl}>{nativeAppMode?"Open HTTPS":"Open secure HTTPS shop page"}</a>}
        </div>
      : permissionState==="denied"&&!localOriginDoesNotNeedLnaPermission
        ? <small className="entry-local-agent-message">{message}</small>
        : <button type="button" className="customer-flow-button" disabled={checkStatus==="checking"} onClick={()=>void checkConnection()}>
            {checkStatus==="checking"?"Checking…":checkStatus==="connected"?"Check again":nativeAppMode?"Check Wi-Fi":"Check shop Wi-Fi"}
          </button>}
    {message&&checkStatus!=="insecure"&&!(permissionState==="denied"&&!localOriginDoesNotNeedLnaPermission)&&<small className="entry-local-agent-message">{message}</small>}
  </section>;
}


type CustomerGroupSettings = { colourMode: "Mono" | "Colour"; isDuplex: boolean };
type CustomerFileSettings = { copies: string; custom: boolean; colourMode: "Mono" | "Colour"; isDuplex: boolean };

function consumeSmartPrintNativePickerGroups():string[]|undefined {
  const target=window as Window & {__smartPrintNativePickerGroups?:string[]};
  const groups=target.__smartPrintNativePickerGroups;
  delete target.__smartPrintNativePickerGroups;
  return Array.isArray(groups)?groups:undefined;
}
type CustomerSavedPrintSettings = { copies: number; colourMode: "Mono" | "Colour"; isDuplex: boolean };

type StoredCustomerDraft = {
  sessionMarker: string;
  uploaded: UploadedPrintJob;
  uploadedBatch?: UploadedPrintJob[];
  uploadedFileFingerprints?: string[];
  batchId?: string;
  quote: PrintQuoteResult | null;
  fileGroupNames?: Record<string,string>;
  groupSettings?: Record<string,CustomerGroupSettings>;
  fileSettings?: Record<string,CustomerFileSettings>;
  printSettingsByJobId?: Record<string,CustomerSavedPrintSettings>;
  quotesByJobId?: Record<string,PrintQuoteResult>;
};

function customerFileFingerprint(file:File):string {
  const relativePath=(file as File & {webkitRelativePath?:string}).webkitRelativePath??"";
  const nativeGroup=(file as File & {__smartPrintBatchGroup?:string}).__smartPrintBatchGroup??"";
  return `${relativePath||nativeGroup}\u0000${file.name}\u0000${file.size}\u0000${file.lastModified}\u0000${file.type}`;
}

function customerBatchGroupName(file:File):string {
  const relativePath=(file as File & {webkitRelativePath?:string}).webkitRelativePath??"";
  const pathParts=relativePath.split("/").filter(Boolean);
  return pathParts.length>1?pathParts[0]:"Loose files";
}

function normalizeCustomerCopies(value:string):string {
  const trimmed=value.trim();
  if(!/^\d+$/.test(trimmed))return "1";
  const copies=Number(trimmed);
  return Number.isInteger(copies)&&copies>=1&&copies<=100?String(copies):"1";
}

function readStoredCustomerDraft(publicCode:string,sessionToken:string):StoredCustomerDraft|null {
  try {
    const raw=localStorage.getItem(`qrtoprint.customer-active-job:${publicCode}`);
    if(!raw)return null;
    const draft=JSON.parse(raw) as StoredCustomerDraft;
    return draft.sessionMarker===sessionToken.slice(-16)&&
      (draft.uploaded?.job?.id||draft.uploadedBatch?.length)?draft:null;
  } catch {
    return null;
  }
}

function smartPrintNativeHeadline(status:string):string {
  switch(status) {
    case "AWAITING_SHOP_APPROVAL": return "Awaiting shop review";
    case "AWAITING_PAYMENT": return "Payment required";
    case "AWAITING_CUSTOMER_PAYMENT": return "Complete UPI payment";
    case "AWAITING_COUNTER_PAYMENT": return "Pay at counter";
    case "PAYMENT_REPORTED": return "Verification pending";
    case "PAYMENT_REJECTED": return "Payment not verified";
    case "PAID_AUTHORISED": return "Payment verified";
    case "LOCAL_QUEUE_CLAIMED": return "Transferring to shop";
    case "LOCAL_QUEUE_READY": return "Queued at shop";
    case "PRINT_AUTHORISED": return "Printing authorised";
    case "PRINT_SUBMITTING": return "Sending to printer";
    case "SPOOLER_ACCEPTED": return "Printer queue accepted";
    case "PRINTING": return "Printing";
    case "OUTPUT_UNCONFIRMED": return "Output check pending";
    case "COMPLETED": return "Ready to collect";
    case "REJECTED": return "Request declined";
    case "QUOTE_EXPIRED": return "Quote expired";
    default: return "Request in progress";
  }
}

function smartPrintNativeStatus(status:string):string {
  switch(status) {
    case "AWAITING_SHOP_APPROVAL": return "Awaiting shop approval.";
    case "AWAITING_PAYMENT": return "Shop approved · Pay now.";
    case "AWAITING_CUSTOMER_PAYMENT": return "Pay shop · Submit UTR.";
    case "AWAITING_COUNTER_PAYMENT": return "Pay at the counter.";
    case "PAYMENT_REPORTED": return "UTR sent · Verify pending.";
    case "PAYMENT_REJECTED": return "Payment not verified.";
    case "PAID_AUTHORISED": return "Payment verified · Print pending.";
    case "LOCAL_QUEUE_CLAIMED": return "Transferring to shop.";
    case "LOCAL_QUEUE_READY": return "Queued · Operator pending.";
    case "PRINT_AUTHORISED": return "Printing authorised.";
    case "PRINT_SUBMITTING": return "Sending to printer.";
    case "SPOOLER_ACCEPTED": return "Queue accepted · Paper unconfirmed.";
    case "PRINTING": return "Printing in progress.";
    case "OUTPUT_UNCONFIRMED": return "Shop checking printed pages.";
    case "COMPLETED": return "Ready for collection.";
    case "REJECTED": return "Shop rejected request.";
    case "QUOTE_EXPIRED": return "Quote expired.";
    default: return "Status updated.";
  }
}

function smartPrintNativeBoundary(status:string|undefined,hasQuote:boolean):string {
  switch(status) {
    case "AWAITING_SHOP_APPROVAL": return "Shop review · No payment.";
    case "AWAITING_PAYMENT": return "Pay shop · Verification required.";
    case "AWAITING_COUNTER_PAYMENT": return "Counter payment · Await confirmation.";
    case "AWAITING_CUSTOMER_PAYMENT": return "Submit UTR · Shop verifies.";
    case "PAYMENT_REPORTED": return "UTR pending · Shop verifies.";
    case "PAID_AUTHORISED": return "Payment verified · Printing not started.";
    case "LOCAL_QUEUE_CLAIMED": return "Secure transfer · Not printing.";
    case "LOCAL_QUEUE_READY": return "Queued · Operator approval needed.";
    case "PRINT_AUTHORISED": return "Shop approved physical printing.";
    case "PRINT_SUBMITTING": return "Sending to printer.";
    case "SPOOLER_ACCEPTED": return "Queue accepted · Paper unconfirmed.";
    case "PRINTING": return "Printing · Output unconfirmed.";
    case "OUTPUT_UNCONFIRMED": return "Output unconfirmed · Shop checks pages.";
    case "COMPLETED": return "Output confirmed · Collect print.";
    case "QUOTE_EXPIRED": return "Quote expired · Request another.";
    case "REJECTED": return "Request rejected · Ask shop.";
    default: return hasQuote ? "Quote only · No payment." : "No payment yet.";
  }
}

function CustomerPrintFlow({publicCode,sessionToken,paymentOptions,localAgent,localReady}:{publicCode:string;sessionToken:string;paymentOptions:{upiEnabled:boolean;upiId?:string|null;payeeName?:string|null;cashEnabled:boolean};localAgent:CustomerLocalAgent|null;localReady:boolean}) {
  const nativeAppMode = navigator.userAgent.includes("SmartPrintAndroid/1");
  const initialDraft=readStoredCustomerDraft(publicCode,sessionToken);
  const [files,setFiles]=useState<File[]>([]);
  const [selectedFileIndex,setSelectedFileIndex]=useState(0);
  const file=files[selectedFileIndex]??null;
  const [previewUrl,setPreviewUrl]=useState<string|null>(null);
  const [uploaded,setUploaded]=useState<UploadedPrintJob|null>(()=>initialDraft?.uploaded??initialDraft?.uploadedBatch?.[0]??null);
  const [uploadedBatch,setUploadedBatch]=useState<UploadedPrintJob[]>(()=>initialDraft?.uploadedBatch??(initialDraft?.uploaded?[initialDraft.uploaded]:[]));
  const [uploadedFileFingerprints,setUploadedFileFingerprints]=useState<string[]>(()=>initialDraft?.uploadedFileFingerprints??[]);
  const [batchId,setBatchId]=useState(()=>initialDraft?.batchId??"");
  const [fileGroupNames,setFileGroupNames]=useState<Record<string,string>>(()=>initialDraft?.fileGroupNames??{});
  const [groupSettings,setGroupSettings]=useState<Record<string,CustomerGroupSettings>>(()=>initialDraft?.groupSettings??{});
  const [fileSettings,setFileSettings]=useState<Record<string,CustomerFileSettings>>(()=>initialDraft?.fileSettings??{});
  const [printSettingsByJobId,setPrintSettingsByJobId]=useState<Record<string,CustomerSavedPrintSettings>>(()=>initialDraft?.printSettingsByJobId??{});
  const [quotesByJobId,setQuotesByJobId]=useState<Record<string,PrintQuoteResult>>(()=>initialDraft?.quotesByJobId??(initialDraft?.quote&&initialDraft?.uploaded?{[initialDraft.uploaded.job.id]:initialDraft.quote}:{}));
  const [uploadProgress,setUploadProgress]=useState<{completed:number;total:number;currentFile:string|null}|null>(null);
  const [quote,setQuote]=useState<PrintQuoteResult|null>(()=>initialDraft?.quote??null);
  const [jobStatus,setJobStatus]=useState<CustomerJobStatus|null>(null);
  const [batchStatuses,setBatchStatuses]=useState<Record<string,CustomerJobStatus>>({});
  const [payment,setPayment]=useState<CustomerPayment|null>(null);
  const [paymentReference,setPaymentReference]=useState("");
  const [paymentMethod,setPaymentMethod]=useState<"UPI"|"CASH">(paymentOptions.upiEnabled?"UPI":"CASH");
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [uploadMode,setUploadMode]=useState<"LOCAL"|"CLOUD">(localAgent?.endpoints.length?"LOCAL":"CLOUD");

  useEffect(()=>{
    if(!localAgent?.endpoints.length)setUploadMode("CLOUD");
    else setUploadMode(current=>current==="CLOUD"?"LOCAL":current);
  },[localAgent?.agentDeviceId]);

  useEffect(()=>{
    if(!paymentOptions.upiEnabled&&paymentOptions.cashEnabled)setPaymentMethod("CASH");
    else if(paymentOptions.upiEnabled)setPaymentMethod("UPI");
  },[paymentOptions.upiEnabled,paymentOptions.cashEnabled]);

  useEffect(()=>{
    const storageKey=`qrtoprint.customer-active-job:${publicCode}`;
    if(!uploaded&&uploadedBatch.length===0){
      localStorage.removeItem(storageKey);
      return;
    }
    // Store only job metadata, never the bearer session capability.
    localStorage.setItem(storageKey,JSON.stringify({
      sessionMarker:sessionToken.slice(-16),
      uploaded: uploaded??uploadedBatch[0],
      uploadedBatch,
      uploadedFileFingerprints,
      batchId,
      quote,
      fileGroupNames,
      groupSettings,
      fileSettings,
      printSettingsByJobId,
      quotesByJobId
    } satisfies StoredCustomerDraft));
  },[publicCode,sessionToken,uploaded,uploadedBatch,uploadedFileFingerprints,batchId,quote,fileGroupNames,groupSettings,fileSettings,printSettingsByJobId,quotesByJobId]);

  const batchItemsForStatus=uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]);
  const batchStatusKey=batchItemsForStatus.map(item=>item.job.id).join("|");
  const groupNameForFile=(candidate:File)=>fileGroupNames[customerFileFingerprint(candidate)]??customerBatchGroupName(candidate);
  const defaultsForGroup=(name:string):CustomerGroupSettings=>groupSettings[name]??{colourMode:"Mono",isDuplex:false};
  const settingsForFile=(candidate:File)=> {
    const fingerprint=customerFileFingerprint(candidate);
    const defaults=defaultsForGroup(groupNameForFile(candidate));
    const own=fileSettings[fingerprint];
    return {
      colourMode:own?.custom?own.colourMode:defaults.colourMode,
      isDuplex:own?.custom?own.isDuplex:defaults.isDuplex,
      copies:Number(normalizeCustomerCopies(own?.copies??"1"))
    } as CustomerSavedPrintSettings;
  };

  useEffect(()=>{
    const items=uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]);
    if(items.length===0)return;
    let active=true;
    let refreshing=false;
    const refreshAll=async()=>{
      if(refreshing)return;
      refreshing=true;
      try{
        const results=await Promise.all(items.map(async item=>{
          try{return [item.job.id,await getCustomerJobStatus(sessionToken,item.job.id)] as const;}
          catch{return null;}
        }));
        if(!active)return;
        const fresh=results.reduce<Record<string,Awaited<ReturnType<typeof getCustomerJobStatus>>>>((map,item)=>{
          if(item)map[item[0]]=item[1];
          return map;
        },{});
        setBatchStatuses(current=>({...current,...fresh}));
        const selectedId=uploaded?.job.id;
        if(selectedId&&fresh[selectedId]){
          const selected=fresh[selectedId];
          if(selected.status==="Uploaded"||selected.status==="UPLOADED"||selected.status==="AWAITING_CONFIGURATION"){
            setJobStatus(null);
            setPayment(null);
          } else {
            setJobStatus(selected);
            setPayment(selected.payment??null);
          }
        }
      } finally {refreshing=false;}
    };
    void refreshAll();
    const timer=window.setInterval(()=>void refreshAll(),30000);
    return ()=>{active=false;window.clearInterval(timer);};
  },[sessionToken,batchStatusKey,uploaded?.job.id]);

  useEffect(()=>{
    if(!uploaded)return;
    let active=true;
    void getCustomerJobStatus(sessionToken,uploaded.job.id).then(refreshed=>{
      if(!active)return;
      // A newly uploaded job starts in AWAITING_CONFIGURATION on the API.
      // Treat that state like the legacy Uploaded value so the customer can
      // choose print settings and request the first quote after a reload.
      if(refreshed.status==="Uploaded"||refreshed.status==="UPLOADED"||refreshed.status==="AWAITING_CONFIGURATION"){
        setJobStatus(null);
        setPayment(null);
        return;
      }
      setJobStatus(refreshed);
      setPayment(refreshed.payment??null);
    }).catch(exception=>{
      if(active)setError(exception instanceof ApiError?exception.message:"Could not restore this print request.");
    });
    return ()=>{active=false;};
  },[sessionToken,uploaded?.job.id]);

  useEffect(()=>{
    if(!file){setPreviewUrl(null);return;}
    const url=URL.createObjectURL(file);
    setPreviewUrl(url);
    return ()=>URL.revokeObjectURL(url);
  },[file]);

  async function upload() {
    if(files.length===0)return;
    if(uploadMode==="LOCAL"&&!localReady) {
      setError(nativeAppMode ? "Check shop Wi-Fi." : "Check shop Wi-Fi access above before starting a direct local upload.");
      return;
    }
    const currentBatchId=batchId||crypto.randomUUID();
    setBatchId(currentBatchId);
    const completedFingerprints=new Set(uploadedFileFingerprints);
    const results=[...uploadedBatch];
    const savedSettings={...printSettingsByJobId};
    const pending=files.filter(candidate=>!completedFingerprints.has(customerFileFingerprint(candidate)));
    if(pending.length===0){setError(nativeAppMode ? "Files already uploaded. Choose more." : "All selected files are already uploaded. Choose another document or folder.");return;}
    setBusy(true);setError("");setQuote(null);setQuotesByJobId({});setJobStatus(null);setUploadProgress({completed:0,total:pending.length,currentFile:pending[0]?.name??null});
    try {
      for(let index=0;index<pending.length;index++){
        const candidate=pending[index];
        setUploadProgress({completed:index,total:pending.length,currentFile:candidate.name});
        const fingerprint=customerFileFingerprint(candidate);
        const batchGroupName=groupNameForFile(candidate);
        const settings=settingsForFile(candidate);
        const created=uploadMode==="LOCAL"&&localAgent
          ? await uploadCustomerDocumentLocally(sessionToken,candidate,localAgent,currentBatchId,batchGroupName)
          : await uploadCustomerDocument(sessionToken,candidate,currentBatchId,batchGroupName);
        if(!results.some(item=>item.job.id===created.job.id))results.push(created);
        savedSettings[created.job.id]=settings;
        completedFingerprints.add(fingerprint);
        setUploadedBatch([...results]);
        setUploadedFileFingerprints([...completedFingerprints]);
        setUploadProgress({completed:index+1,total:pending.length,currentFile:pending[index+1]?.name??null});
      }
      setPrintSettingsByJobId(savedSettings);
      setUploadedBatch(results);
      setUploaded(results[0]??null);
      setQuote(null);setJobStatus(null);setPayment(null);setPaymentReference("");
      setSelectedFileIndex(0);
      setFiles([]);
    } catch(exception) {
      setPrintSettingsByJobId(savedSettings);
      setUploadedBatch(results);
      setUploadedFileFingerprints([...completedFingerprints]);
      if(results.length>0)setUploaded(results[0]);
      setError(exception instanceof ApiError?exception.message:`Upload stopped after ${completedFingerprints.size} file(s). Retry the selected files to continue; completed files will be skipped.`);
    } finally {setBusy(false);setUploadProgress(null);}
  }

  function selectUploadedFile(item:UploadedPrintJob) {
    setUploaded(item);setQuote(quotesByJobId[item.job.id]??null);setJobStatus(null);setPayment(null);setPaymentReference("");setError("");
  }

  async function getQuote() {
    const items=uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]);
    if(items.length===0)return;
    setBusy(true);setError("");setJobStatus(null);
    const nextQuotes={...quotesByJobId};
    const failures:string[]=[];
    try {
      for(const item of items) {
        const existingQuote=nextQuotes[item.job.id];
        const status=batchStatuses[item.job.id]?.status??item.job.status;
        if(existingQuote&&existingQuote.status!=="QUOTE_EXPIRED")continue;
        if(!["AWAITING_CONFIGURATION","UPLOADED","Uploaded","QUOTE_EXPIRED"].includes(status))continue;
        const settings=printSettingsByJobId[item.job.id]??{colourMode:"Mono" as const,isDuplex:false,copies:1};
        try {
          nextQuotes[item.job.id]=await calculateCustomerQuote(sessionToken,item.job.id,{
            colourMode:settings.colourMode,paperSize:"A4",isDuplex:settings.isDuplex,copies:settings.copies
          });
        } catch(exception) {
          failures.push(item.document.originalFileName);
        }
      }
      setQuotesByJobId(nextQuotes);
      const selectedQuote=uploaded?nextQuotes[uploaded.job.id]??null:null;
      setQuote(selectedQuote??Object.values(nextQuotes)[0]??null);
      if(failures.length) setError(`Could not quote: ${failures.join(", ")}. Retry the batch quote.`);
      else if(!Object.keys(nextQuotes).length) setError("No files are waiting for a quote.");
    } catch(exception) {
      setError(exception instanceof ApiError?exception.message:"The shop could not calculate the batch quote.");
    } finally {setBusy(false);}
  }

  async function acceptQuote() {
    const items=uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]);
    const pending=items.filter(item=>quotesByJobId[item.job.id]?.status==="AWAITING_CUSTOMER_APPROVAL");
    if(pending.length===0)return;
    setBusy(true);setError("");
    const nextQuotes={...quotesByJobId};
    const failures:string[]=[];
    try {
      for(const item of pending) {
        try {
          const accepted=await acceptCustomerQuote(sessionToken,item.job.id);
          const current=nextQuotes[item.job.id];
          if(current)nextQuotes[item.job.id]={...current,status:accepted.status};
        } catch {
          const fresh=await getCustomerJobStatus(sessionToken,item.job.id).catch(()=>null);
          if(fresh&&fresh.status!=="AWAITING_CUSTOMER_APPROVAL") {
            setBatchStatuses(current=>({...current,[item.job.id]:fresh}));
          } else failures.push(item.document.originalFileName);
        }
      }
      setQuotesByJobId(nextQuotes);
      if(uploaded) {
        const refreshed=await getCustomerJobStatus(sessionToken,uploaded.job.id);
        setJobStatus(refreshed);
        setPayment(refreshed.payment??null);
      }
      const refreshedBatch=await Promise.all(items.map(async item=>{
        try{return [item.job.id,await getCustomerJobStatus(sessionToken,item.job.id)] as const;}
        catch{return null;}
      }));
      setBatchStatuses(current=>({...current,...Object.fromEntries(refreshedBatch.filter((entry):entry is NonNullable<typeof entry>=>entry!==null))}));
      setQuote(uploaded?nextQuotes[uploaded.job.id]??null:null);
      if(failures.length)setError(`Could not send: ${failures.join(", ")}. Refresh and retry.`);
    } catch(exception) {
      setError(exception instanceof ApiError?exception.message:"The batch could not be sent to the shop.");
    } finally {setBusy(false);}
  }

  async function refreshJobStatus() {
    if(!uploaded)return;
    setBusy(true);setError("");
    try {
      const refreshed=await getCustomerJobStatus(sessionToken,uploaded.job.id);
      setJobStatus(refreshed);
      setPayment(refreshed.payment ?? null);
    }
    catch(exception){setError(exception instanceof ApiError?exception.message:"Could not refresh job status.");}
    finally {setBusy(false);}
  }


  // Keep the customer informed as the shop reviews, verifies payment and prints.
  // Terminal states stop polling; transient network failures are retried silently.
  useEffect(()=>{
    if(!uploaded||!jobStatus)return;
    const liveStates=new Set([
      "AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT","AWAITING_COUNTER_PAYMENT",
      "AWAITING_CUSTOMER_PAYMENT","PAYMENT_REPORTED","PAYMENT_REJECTED",
      "PAID_AUTHORISED","LOCAL_QUEUE_CLAIMED","LOCAL_QUEUE_READY",
      "PRINT_AUTHORISED","PRINT_SUBMITTING","SPOOLER_ACCEPTED","PRINTING",
      "OUTPUT_UNCONFIRMED"
    ]);
    if(!liveStates.has(jobStatus.status))return;
    let active=true;
    const timer=window.setInterval(()=>{
      void getCustomerJobStatus(sessionToken,uploaded.job.id).then(refreshed=>{
        if(!active)return;
        setJobStatus(refreshed);
        setPayment(refreshed.payment??null);
      }).catch(()=>{/* Retry on the next interval without interrupting the flow. */});
    },8000);
    return ()=>{
      active=false;
      window.clearInterval(timer);
    };
  },[sessionToken,uploaded?.job.id,jobStatus?.status]);

  async function beginPayment(method:"UPI"|"CASH") {
    if(!uploaded)return;
    setBusy(true);setError("");
    try {
      const attempt=await startCustomerPayment(sessionToken,uploaded.job.id,method);
      setPayment(attempt);
      setPaymentMethod(method);
      const refreshed=await getCustomerJobStatus(sessionToken,uploaded.job.id);
      setJobStatus(refreshed);
    } catch(exception) {
      setError(exception instanceof ApiError?exception.message:"Could not start this payment method.");
    } finally {setBusy(false);}
  }

  async function submitPaymentReference() {
    if(!payment)return;
    setBusy(true);setError("");
    try {
      await reportCustomerUpiPayment(sessionToken,payment.id,paymentReference.trim());
      setPaymentReference("");
      const refreshed=await getCustomerJobStatus(sessionToken,uploaded!.job.id);
      setJobStatus(refreshed);
      setPayment(refreshed.payment??null);
    } catch(exception) {
      setError(exception instanceof ApiError?exception.message:"The payment reference could not be submitted.");
    } finally {setBusy(false);}
  }

  function chooseFiles(next:File[], nativeGroupNames?:string[]) {
    let nestedSkipped=0;
    const rootFiles=next.filter(candidate=>{
      const relativePath=(candidate as File & {webkitRelativePath?:string}).webkitRelativePath??"";
      const parts=relativePath.split("/").filter(Boolean);
      if(relativePath&&parts.length>2){nestedSkipped++;return false;}
      return true;
    });
    const supported=rootFiles.filter(candidate=>/\.(pdf|jpe?g|png)$/i.test(candidate.name));
    for(const candidate of supported){
      const nativeGroup=nativeGroupNames?.[next.indexOf(candidate)]?.trim().slice(0,80);
      if(nativeGroup)(candidate as File & {__smartPrintBatchGroup?:string}).__smartPrintBatchGroup=nativeGroup;
    }
    const unsupportedSkipped=rootFiles.length-supported.length;
    if(supported.length===0){setError("Choose printable PDF, JPG or PNG files.");return;}
    const oversized=supported.find(candidate=>candidate.size<=0||candidate.size>20*1024*1024);
    if(oversized){setError(`“${oversized.name}” exceeds the 20 MB per-file limit.`);return;}
    const merged=new Map(files.map(candidate=>[customerFileFingerprint(candidate),candidate]));
    for(const candidate of supported)merged.set(customerFileFingerprint(candidate),candidate);
    const selected=Array.from(merged.values());
    const alreadyUploaded=new Set(uploadedFileFingerprints);
    const pending=selected.filter(candidate=>!alreadyUploaded.has(customerFileFingerprint(candidate)));
    const totalBytes=uploadedBatch.reduce((sum,item)=>sum+item.document.sizeBytes,0)+pending.reduce((sum,candidate)=>sum+candidate.size,0);
    if(uploadedBatch.length+pending.length>20||totalBytes>50*1024*1024){
      setError(nativeAppMode ? "20 files · 50 MB max." : "This batch can contain up to 20 files and 50 MB in total.");
      return;
    }
    setFileGroupNames(current=>{
      const updated={...current};
      for(const candidate of supported){
        const nativeGroup=nativeGroupNames?.[next.indexOf(candidate)]?.trim();
        updated[customerFileFingerprint(candidate)]=nativeGroup||customerBatchGroupName(candidate);
      }
      return updated;
    });
    setGroupSettings(current=>{
      const updated={...current};
      for(const candidate of supported) {
        const nativeGroup=nativeGroupNames?.[next.indexOf(candidate)]?.trim();
        const group=nativeGroup||customerBatchGroupName(candidate);
        if(!updated[group])updated[group]={colourMode:"Mono",isDuplex:false};
      }
      return updated;
    });
    setFileSettings(current=>{
      const updated={...current};
      for(const candidate of supported) {
        const key=customerFileFingerprint(candidate);
        if(!updated[key])updated[key]={copies:"1",custom:false,colourMode:"Mono",isDuplex:false};
      }
      return updated;
    });
    setFiles(selected);setSelectedFileIndex(0);
    setError(nestedSkipped? "Nested folders are skipped; only files directly inside each selected folder are included.":unsupportedSkipped? "Unsupported file types were skipped.": "");
  }

  function removeSelectedFile(fingerprint: string) {
    if (busy) return;
    const index = files.findIndex(candidate => customerFileFingerprint(candidate) === fingerprint);
    if (index < 0) return;
    const nextFiles = files.filter(candidate => customerFileFingerprint(candidate) !== fingerprint);
    setFiles(nextFiles);
    setSelectedFileIndex(current => current === index
      ? Math.min(index, Math.max(0, nextFiles.length - 1))
      : current > index ? current - 1 : current);
    setFileGroupNames(current => {
      const next = { ...current };
      delete next[fingerprint];
      return next;
    });
    setFileSettings(current => {
      const next = { ...current };
      delete next[fingerprint];
      return next;
    });
    setError("");
  }

  const pendingFilesCount=files.filter(candidate=>!uploadedFileFingerprints.includes(customerFileFingerprint(candidate))).length;
  const uploadGroups=Array.from(new Set(files.map(candidate=>groupNameForFile(candidate)))).map(name=>({
    name, files:files.filter(candidate=>groupNameForFile(candidate)===name)
  }));
  const quoteItems=uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]);
  const quoteEntries=quoteItems.map(item=>({item,quote:quotesByJobId[item.job.id]})).filter(entry=>!!entry.quote);
  const batchQuoteTotal=quoteEntries.reduce((total,entry)=>{
    const status=batchStatuses[entry.item.job.id]?.status??entry.item.job.status;
    return status==="REJECTED"||status==="REFUNDED"?total:total+(entry.quote?.quotedAmountMinor??0);
  },0);
  const canSendBatch=quoteItems.length>0&&quoteEntries.length===quoteItems.length&&quoteEntries.some(entry=>entry.quote?.status==="AWAITING_CUSTOMER_APPROVAL");
  const canCalculateBatch=quoteItems.some(item=>{
    const status=batchStatuses[item.job.id]?.status??item.job.status;
    if(quotesByJobId[item.job.id]&&quotesByJobId[item.job.id].status!=="QUOTE_EXPIRED")return false;
    return !quotesByJobId[item.job.id]||quotesByJobId[item.job.id].status==="QUOTE_EXPIRED"||
      ["AWAITING_CONFIGURATION","UPLOADED","Uploaded","QUOTE_EXPIRED"].includes(status);
  });

  return <section className="customer-print-flow">
    <div className="customer-flow-heading"><div><span>STEP 1</span><h2>{nativeAppMode?"Add files":"Add your documents"}</h2></div><span className="upload-limit">PDF, JPG or PNG · Up to 20 files / 50 MB per batch</span></div>
    {localAgent?.endpoints.length? <div className="customer-upload-modes">
      <button type="button" className={uploadMode==="LOCAL"?"customer-upload-mode selected":"customer-upload-mode"} onClick={()=>setUploadMode("LOCAL")}><strong>Shop Wi-Fi</strong><small>Document stays on the shop computer</small></button>
      <button type="button" className={uploadMode==="CLOUD"?"customer-upload-mode selected":"customer-upload-mode"} onClick={()=>setUploadMode("CLOUD")}><strong>Cloud-assisted</strong><small>Encrypted upload through the API</small></button>
    </div>:null}
    <div className="customer-upload-picker-grid">
      <label className="customer-upload-zone">
        <input type="file" multiple accept=".pdf,.jpg,.jpeg,.png,application/pdf,image/jpeg,image/png" onChange={event=>{chooseFiles(Array.from(event.currentTarget.files||[]),consumeSmartPrintNativePickerGroups());event.currentTarget.value="";}}/>
        <span className="customer-upload-icon"><FileUp size={21}/></span>
        <strong>Select multiple files</strong>
        <small>Choose up to 20 PDF, JPG or PNG documents at once</small>
      </label>
      <label className="customer-upload-zone folder-upload-zone">
        <input type="file" multiple accept={nativeAppMode?"application/x-smartprint-folder-selection":".pdf,.jpg,.jpeg,.png,application/pdf,image/jpeg,image/png"} {...({webkitdirectory:"true",directory:"true"} as any)} onChange={event=>{chooseFiles(Array.from(event.currentTarget.files||[]),consumeSmartPrintNativePickerGroups());event.currentTarget.value="";}}/>
        <span className="customer-upload-icon"><FolderOpen size={21}/></span>
        <strong>{nativeAppMode?"Choose folders":"Add a folder"}</strong>
        <small>{nativeAppMode?"Select multiple folders at once":"Choose one folder; select again to add another to this batch"}</small>
      </label>
    </div>
    {files.length>0&&<div className="customer-selected-files">
      <div className="customer-selected-files-head"><strong>{files.length} selected</strong><span>{formatBytes(files.reduce((total,item)=>total+item.size,0))} in this selection</span></div>
      <div className="customer-batch-groups">{uploadGroups.map(group=>{
        const defaults=defaultsForGroup(group.name);
        return <section className="customer-folder-group" key={group.name}>
          <div className="customer-folder-group-head">
            <div className="customer-folder-title"><FolderOpen size={16}/><span><strong>{group.name}</strong><small>{group.files.length} file(s)</small></span></div>
            <div className="customer-folder-defaults">
              <div><span>Colour</span><div className="customer-choice-row">
                <button type="button" className={defaults.colourMode==="Mono"?"customer-choice selected":"customer-choice"} onClick={()=>setGroupSettings(current=>({...current,[group.name]:{...defaults,colourMode:"Mono"}}))}>B&W</button>
                <button type="button" className={defaults.colourMode==="Colour"?"customer-choice selected":"customer-choice"} onClick={()=>setGroupSettings(current=>({...current,[group.name]:{...defaults,colourMode:"Colour"}}))}>Colour</button>
              </div></div>
              <div><span>Sides</span><div className="customer-choice-row">
                <button type="button" className={!defaults.isDuplex?"customer-choice selected":"customer-choice"} onClick={()=>setGroupSettings(current=>({...current,[group.name]:{...defaults,isDuplex:false}}))}>Single</button>
                <button type="button" className={defaults.isDuplex?"customer-choice selected":"customer-choice"} onClick={()=>setGroupSettings(current=>({...current,[group.name]:{...defaults,isDuplex:true}}))}>Double</button>
              </div></div>
            </div>
          </div>
          <div className="customer-folder-file-list">{group.files.map(selected=>{
            const fingerprint=customerFileFingerprint(selected);
            const own=fileSettings[fingerprint]??{copies:"1",custom:false,colourMode:defaults.colourMode,isDuplex:defaults.isDuplex};
            const index=files.findIndex(candidate=>customerFileFingerprint(candidate)===fingerprint);
            return <div className={selectedFileIndex===index?"customer-folder-file-row selected":"customer-folder-file-row"} key={fingerprint}>
              <button type="button" className="customer-folder-file-name" onClick={()=>setSelectedFileIndex(index)}><FileText size={15}/><span><strong>{selected.name}</strong><small>{formatBytes(selected.size)}</small></span></button>
              <label className="customer-folder-copies"><span>Copies</span><input type="number" min="1" max="100" step="1" value={own.copies} onChange={event=>setFileSettings(current=>({...current,[fingerprint]:{...(current[fingerprint]??own),copies:event.target.value}}))} onBlur={event=>setFileSettings(current=>({...current,[fingerprint]:{...(current[fingerprint]??own),copies:normalizeCustomerCopies(event.currentTarget.value)}}))}/></label>
              <button type="button" className={own.custom?"customer-file-custom active":"customer-file-custom"} onClick={()=>setFileSettings(current=>({...current,[fingerprint]:{...(current[fingerprint]??own),custom:!own.custom,colourMode:own.custom?own.colourMode:defaults.colourMode,isDuplex:own.custom?own.isDuplex:defaults.isDuplex}}))}>{own.custom?"Custom":"Folder"}</button>
              <button type="button" className="customer-file-remove" aria-label={`Remove ${selected.name} from this selection`} title="Remove from this selection" disabled={busy} onClick={()=>removeSelectedFile(fingerprint)}><X size={13}/><span>Remove</span></button>
              {own.custom&&<div className="customer-file-overrides">
                <div className="customer-choice-row">
                  <button type="button" className={own.colourMode==="Mono"?"customer-choice selected":"customer-choice"} onClick={()=>setFileSettings(current=>({...current,[fingerprint]:{...own,custom:true,colourMode:"Mono"}}))}>B&W</button>
                  <button type="button" className={own.colourMode==="Colour"?"customer-choice selected":"customer-choice"} onClick={()=>setFileSettings(current=>({...current,[fingerprint]:{...own,custom:true,colourMode:"Colour"}}))}>Colour</button>
                </div>
                <div className="customer-choice-row">
                  <button type="button" className={!own.isDuplex?"customer-choice selected":"customer-choice"} onClick={()=>setFileSettings(current=>({...current,[fingerprint]:{...own,custom:true,isDuplex:false}}))}>Single</button>
                  <button type="button" className={own.isDuplex?"customer-choice selected":"customer-choice"} onClick={()=>setFileSettings(current=>({...current,[fingerprint]:{...own,custom:true,isDuplex:true}}))}>Double</button>
                </div>
              </div>}
            </div>;
          })}</div>
        </section>;
      })}</div>
    </div>}
    {files.length>0&&<small className="customer-upload-local-note">Folder defaults apply to every file. Set an individual file to Custom for an exception.</small>}
    {previewUrl&&file&&<div className="customer-file-preview"><div className="customer-file-label"><FileText size={15}/><span>Preview · {file.name}</span></div>{file.type==="application/pdf"||/\.pdf$/i.test(file.name)?<iframe src={previewUrl} title={file.name+" preview"}/>:<img src={previewUrl} alt={file.name+" preview"}/>}</div>}
    {files.length>0&&<button className="customer-flow-button" disabled={busy||pendingFilesCount===0||(uploadMode==="LOCAL"&&!localReady)} onClick={()=>void upload()}>{busy?<span className="button-spinner"/>:<><UploadIcon/>{uploadMode==="LOCAL"&&localAgent?"Upload "+pendingFilesCount+" file(s) to shop":"Upload "+pendingFilesCount+" file(s)"}</>}</button>}
    {uploadProgress&&<div className="customer-upload-progress"><span>{uploadProgress.currentFile?`Uploading ${Math.min(uploadProgress.completed+1,uploadProgress.total)} of ${uploadProgress.total}: ${uploadProgress.currentFile}`:`${uploadProgress.completed} of ${uploadProgress.total} files uploaded`}</span><div><i style={{width:`${uploadProgress.total?uploadProgress.completed/uploadProgress.total*100:0}%`}}/>{uploadProgress.currentFile&&<b aria-hidden="true"/>}</div><small>The count advances when each document finishes uploading.</small></div>}
    {files.length>0&&uploadMode==="LOCAL"&&localAgent&&<small className="customer-upload-local-note">{nativeAppMode?(localReady?"Cloud receives metadata only.":"Check shop Wi-Fi."):localReady?"Your files travel over shop Wi-Fi. The cloud receives only job, quote and payment metadata.":"Tap Check shop Wi-Fi above first to grant the browser permission."}</small>}
    {files.length>0&&uploadMode==="CLOUD"&&localAgent&&<small className="customer-upload-local-note">{nativeAppMode?"Encrypted cloud transfer.":"Cloud-assisted fallback: files are uploaded encrypted to the API before being transferred to the shop computer."}</small>}
    {uploaded&&<>
      {uploadedBatch.length>0&&<div className="customer-uploaded-batch">
        <div className="customer-uploaded-batch-head"><strong>Batch · {uploadedBatch.length} files</strong><span>Select a file to view its status or payment.</span></div>
        {Array.from(new Set(uploadedBatch.map(item=>item.job.batchGroupName??"Loose files"))).map(groupName=><section className="customer-uploaded-folder" key={groupName}>
          <strong>{groupName}</strong>
          {uploadedBatch.filter(item=>(item.job.batchGroupName??"Loose files")===groupName).map(item=><button type="button" key={item.job.id} className={uploaded.job.id===item.job.id?"customer-uploaded-batch-item active":"customer-uploaded-batch-item"} onClick={()=>selectUploadedFile(item)}>
            <span className="batch-file-index"><FileText size={14}/></span><span className="batch-file-copy"><strong>{item.document.originalFileName}</strong><small>{item.job.pageCount} {item.job.pageCount===1?"page":"pages"} · {printSettingsByJobId[item.job.id]?.copies??item.job.copies} copies · {printSettingsByJobId[item.job.id]?.colourMode??"Mono"} · {(printSettingsByJobId[item.job.id]?.isDuplex??false)?"Double-sided":"Single-sided"} · {item.job.quotedAmountMinor>0?formatMoney(item.job.quotedAmountMinor,item.job.currency):"Awaiting quote"}</small></span>
            <Status
              label={prettyStatus(batchStatuses[item.job.id]?.status??item.job.status??"UPLOADED")}
              tone={["REJECTED","PAYMENT_REJECTED"].includes((batchStatuses[item.job.id]?.status??item.job.status??"").toUpperCase())?"red":
                ["COMPLETED","PAID_AUTHORISED","AWAITING_PAYMENT"].includes((batchStatuses[item.job.id]?.status??item.job.status??"").toUpperCase())?"green":"amber"}
            />
          </button>)}
        </section>)}
      </div>}
      <div className="uploaded-document-card"><div className="uploaded-document-icon"><FileText size={18}/></div><div><strong>{uploaded.document.originalFileName}</strong><span>{uploaded.job.pageCount} {uploaded.job.pageCount===1?"page":"pages"} · {formatBytes(uploaded.document.sizeBytes)} · Validated</span></div><Status label={prettyStatus(batchStatuses[uploaded.job.id]?.status??jobStatus?.status??uploaded.job.status??"UPLOADED")} tone={["REJECTED","PAYMENT_REJECTED"].includes((batchStatuses[uploaded.job.id]?.status??jobStatus?.status??uploaded.job.status??"").toUpperCase())?"red":["COMPLETED","PAID_AUTHORISED","AWAITING_PAYMENT"].includes((batchStatuses[uploaded.job.id]?.status??jobStatus?.status??uploaded.job.status??"").toUpperCase())?"green":"amber"}/></div>
      {canCalculateBatch&&<div className="customer-batch-quote-action">
        <div className="customer-flow-heading customer-config-heading"><div><span>STEP 2</span><h2>Review batch settings</h2></div><small>{quoteItems.length} files</small></div>
        <button className="customer-flow-button" disabled={busy} onClick={()=>void getQuote()}>{busy?<span className="button-spinner"/>:"Calculate batch quote"}</button>
      </div>}
    </>}
    {error&&<div className="customer-flow-error" role="alert">{error}</div>}
    {quoteEntries.length>0&&<div className="customer-quote-card customer-batch-quote-card">
      <div><span>BATCH QUOTE</span><strong>{formatMoney(batchQuoteTotal,quoteEntries[0].quote!.currency)}</strong><small>{quoteEntries.length} files · {quoteEntries.reduce((sum,entry)=>sum+entry.item.job.pageCount,0)} pages</small></div>
      <div className="customer-batch-quote-lines">{quoteEntries.map(entry=><div key={entry.item.job.id}><span>{entry.item.document.originalFileName}{(batchStatuses[entry.item.job.id]?.status??entry.item.job.status)==="REJECTED"?" · Rejected":""}</span><strong>{(batchStatuses[entry.item.job.id]?.status??entry.item.job.status)==="REJECTED"?"Excluded":formatMoney(entry.quote!.quotedAmountMinor,entry.quote!.currency)}</strong></div>)}</div>
      <p className="customer-payment-model-note">Each accepted file has its own payment step after shop approval. This batch total is the sum of file quotes, not one combined charge. Select each accepted file above to complete its payment.</p>
    </div>}
    {canSendBatch&&<button className="customer-flow-button" disabled={busy} onClick={()=>void acceptQuote()}>{busy?<span className="button-spinner"/>:"Send batch to shop"}</button>}
      {jobStatus?.status==="QUOTE_EXPIRED"&&<button className="customer-flow-button" disabled={busy} onClick={()=>{setQuote(null);setQuotesByJobId(current=>{const next={...current};if(uploaded)delete next[uploaded.job.id];return next;});setJobStatus(null);setPayment(null);}}>Request a fresh quote</button>}
    {jobStatus&&<div className="customer-job-state"><div className="customer-job-state-top"><Status label={prettyStatus(jobStatus.status)} tone={jobStatus.status==="REJECTED"?"red":jobStatus.status==="AWAITING_PAYMENT"?"green":"amber"}/><button className="customer-refresh-status" disabled={busy} onClick={()=>void refreshJobStatus()}>{busy?<span className="button-spinner"/>:"Refresh status"}</button></div>
      <strong>{nativeAppMode?smartPrintNativeHeadline(jobStatus.status):jobStatus.status==="AWAITING_SHOP_APPROVAL"?"Your request is with the shop":jobStatus.status==="AWAITING_PAYMENT"?"The shop accepted your request":jobStatus.status==="LOCAL_QUEUE_CLAIMED"?"Your document is reaching the shop":jobStatus.status==="LOCAL_QUEUE_READY"?"Your document is secured at the shop":jobStatus.status==="PRINT_AUTHORISED"?"The shop started printing":jobStatus.status==="PRINT_SUBMITTING"?"Your document is being sent to the printer":jobStatus.status==="SPOOLER_ACCEPTED"?"The printer queue accepted your document":jobStatus.status==="PRINTING"?"Your document is printing":jobStatus.status==="OUTPUT_UNCONFIRMED"?"The shop is checking your print":jobStatus.status==="COMPLETED"?"Your print is ready":jobStatus.status==="PAID_AUTHORISED"?"Your payment is verified":jobStatus.status==="REJECTED"?"The shop could not accept this request":jobStatus.status==="QUOTE_EXPIRED"?"Your quote has expired":"Your request is being reviewed"}</strong>
      <p>{nativeAppMode ? smartPrintNativeStatus(jobStatus.status) : jobStatus.status==="AWAITING_SHOP_APPROVAL"?"The shop will review your request next. No payment has been collected.":jobStatus.status==="AWAITING_PAYMENT"?"The shop approved your quote. Choose one of the payment methods enabled by the shop.":jobStatus.status==="LOCAL_QUEUE_CLAIMED"?"Your paid document is being transferred securely to the shop's Windows computer. It has not been sent to the printer.":jobStatus.status==="LOCAL_QUEUE_READY"?"The document is safely stored in the shop's local queue and is waiting for the print operator. Physical printing has not started.":jobStatus.status==="PRINT_AUTHORISED"?"The shop has authorised printing. The Windows agent will submit the document once.":jobStatus.status==="PRINT_SUBMITTING"?"The Windows agent is submitting your document to the printer.":jobStatus.status==="SPOOLER_ACCEPTED"?"Windows accepted the job into the printer queue. This does not yet confirm paper output.":jobStatus.status==="PRINTING"?"The printer driver reports that the job is printing. The shop will confirm the pages before marking it complete.":jobStatus.status==="OUTPUT_UNCONFIRMED"?"The printer queue could not confirm the final output. The shop operator is checking the pages before any retry.":jobStatus.status==="COMPLETED"?"The shop has checked the physical pages and marked your print ready for collection.":jobStatus.status==="AWAITING_COUNTER_PAYMENT"?"Please pay the shop at the counter. The job is not authorised until the shop confirms receipt.":jobStatus.status==="AWAITING_CUSTOMER_PAYMENT"?"Complete the UPI payment and submit its transaction reference. The shop must independently verify it.":jobStatus.status==="PAYMENT_REPORTED"?"Your UPI reference was sent to the shop. It is not marked paid until the shop verifies the transaction.":jobStatus.status==="PAID_AUTHORISED"?"The shop has verified your payment. The print queue is being prepared; physical printing has not yet started.":jobStatus.status==="PAYMENT_REJECTED"?"The shop could not verify that payment. Check with the shop before trying another method.":jobStatus.status==="REJECTED"?"Please speak with the shop operator if you need help with this request.":jobStatus.status==="QUOTE_EXPIRED"?"This quote is no longer valid. Please ask the shop for a fresh quote.":"Check back for the latest request status."}</p>
      {payment&&!!payment.refundedAmountMinor&&payment.refundedAmountMinor>0&&<div className="customer-refund-note"><Wallet size={16}/><span><strong>{formatMoney(payment.refundedAmountMinor,payment.currency)} returned</strong> for rejected pages ({rejectedPageList(jobStatus.rejectedPageNumbersJson)}). {formatMoney(Math.max(0,payment.amountMinor-payment.refundedAmountMinor),payment.currency)} remains paid; only accepted pages proceed to printing.</span></div>}
      {!!jobStatus.timeline?.length&&<div className="customer-state-timeline">{jobStatus.timeline.map((event,index)=><div key={index}><i/><span><strong>{prettyStatus(event.newStatus)}</strong><small>{event.actorType==="ShopUser"?"Shop team":event.actorType==="Customer"?"You":"System"} · {new Date(event.createdUtc).toLocaleString()}</small></span></div>)}</div>}

      {(jobStatus.status==="AWAITING_PAYMENT"||jobStatus.status==="PAYMENT_REJECTED")&&<div className="customer-pay-panel">
        <div className="customer-pay-heading"><span>PAY THE SHOP</span><strong>{formatMoney(jobStatus.quotedAmountMinor,jobStatus.currency)}</strong></div>
        {paymentOptions.upiEnabled&&<button className={paymentMethod==="UPI"?"customer-choice selected":"customer-choice"} onClick={()=>setPaymentMethod("UPI")}>UPI payment</button>}
        {paymentOptions.cashEnabled&&<button className={paymentMethod==="CASH"?"customer-choice selected":"customer-choice"} onClick={()=>setPaymentMethod("CASH")}>Cash at counter</button>}
        <button className="customer-flow-button" disabled={busy||(!paymentOptions.upiEnabled&&!paymentOptions.cashEnabled)} onClick={()=>void beginPayment(paymentMethod)}>{busy?<span className="button-spinner"/>:nativeAppMode?"Continue":"Continue with "+(paymentMethod==="UPI"?"UPI":"cash at counter")}</button>
        {!paymentOptions.upiEnabled&&!paymentOptions.cashEnabled&&<p className="customer-flow-error">{nativeAppMode?"No payment method. Ask shop.":"The shop has not configured a payment method yet. Please speak to the shop operator."}</p>}
      </div>}
      {payment&&payment.status!=="REJECTED"&&<div className="customer-pay-panel">
        <div className="customer-pay-heading"><span>{payment.method==="UPI"?"UPI PAYMENT":"CASH AT COUNTER"}</span><strong>{formatMoney(Math.max(0,payment.amountMinor-(payment.refundedAmountMinor??0)),payment.currency)}</strong></div>
        {payment.method==="UPI"&&payment.status==="AWAITING_CUSTOMER_PAYMENT"&&<div className="customer-upi-details">
          <div className="customer-upi-qr"><QRCode value={payment.upiUri||"upi://pay?pa="+encodeURIComponent(payment.upiIdSnapshot||"")} size={158}/></div>
          <strong>{payment.payeeNameSnapshot||paymentOptions.payeeName||"Shop"}</strong>
          <span>{payment.upiIdSnapshot||paymentOptions.upiId}</span>
          <a href={payment.upiUri||"#"} className="customer-flow-button customer-upi-open">Open UPI app</a>
          <label className="customer-reference-field">{nativeAppMode?"UTR":"UPI transaction reference / UTR"}<input value={paymentReference} maxLength={100} onChange={event=>setPaymentReference(event.target.value)} placeholder={nativeAppMode?"Enter UTR":"Enter UTR after paying"}/></label>
          <button className="customer-flow-button" disabled={busy||paymentReference.trim().length<4} onClick={()=>void submitPaymentReference()}>{busy?<span className="button-spinner"/>:nativeAppMode?"Submit UTR":"Submit payment reference"}</button>
          <small className="customer-payment-warning">{nativeAppMode?"UTR isn't proof. Shop verifies.":"Submitting a UTR is not proof of payment. The shop verifies it in its own UPI/bank app."}</small>
        </div>}
        {payment.method==="CASH"&&payment.status==="CASH_AWAITING_PAYMENT"&&<p>{nativeAppMode?"Pay at counter · Await confirmation.":"Pay at the shop counter. The shop will confirm receipt before authorising the job."}</p>}
        {payment.status==="CUSTOMER_REPORTED"&&<p>{nativeAppMode?"Reference received · Verification pending.":"Your payment reference was received. The shop still needs to verify the transaction."}</p>}
        {payment.status==="VERIFIED"&&<p>{nativeAppMode?"Payment verified · Printing pending.":"Payment verified by the shop. The print job is authorised, but not yet physically printed."}</p>}
        {payment.status==="PARTIALLY_REFUNDED"&&<p>{nativeAppMode?"Partial refund · Accepted pages only.":"A partial return has been recorded. The remaining paid amount is for the pages the shop accepted."}</p>}
      </div>}
    </div>}
    <div className="customer-payment-boundary"><ShieldCheck size={15}/><span>{nativeAppMode ? smartPrintNativeBoundary(jobStatus?.status,Boolean(quote)) : jobStatus?.status==="PAID_AUTHORISED"?"The shop has verified your payment. It still needs to assign and start the local printer.":jobStatus?.status==="PRINT_AUTHORISED"?"The shop has authorised physical printing.":jobStatus?.status==="PRINT_SUBMITTING"?"Your document is being sent to the Windows printer queue.":jobStatus?.status==="SPOOLER_ACCEPTED"?"Windows accepted the job. This is not proof that paper has come out.":jobStatus?.status==="PRINTING"?"The Windows driver reports that printing is in progress.":jobStatus?.status==="OUTPUT_UNCONFIRMED"?"The shop is checking the printer and actual pages before any retry.":jobStatus?.status==="COMPLETED"?"The shop confirmed the requested pages are printed and ready for collection.":jobStatus?.status==="LOCAL_QUEUE_CLAIMED"?"The shop's paired Windows computer is receiving the encrypted document. It is not being printed yet.":jobStatus?.status==="LOCAL_QUEUE_READY"?"The encrypted local copy is verified on the shop computer. Physical printing still requires the print operator.":jobStatus?.status==="AWAITING_PAYMENT"?"The shop approved this quote. Payment is collected by the shop directly and must be verified before the job is authorised.":jobStatus?.status==="PAYMENT_REPORTED"?"Your UPI reference is awaiting the shop's independent bank verification; submitting a reference does not confirm payment.":quote?"This quote is calculated by the shop's saved rate card. Accepting it sends a request to the shop; it does not collect payment or start printing.":"Your document is validated before pricing. You will see the shop's calculated price before any payment or print approval."}</span></div>
  </section>;
}

function UploadIcon() {
  return <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M12 16V4m0 0L7 9m5-5 5 5"/><path d="M4 16v3a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-3"/></svg>;
}
