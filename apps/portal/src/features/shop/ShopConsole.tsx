import { useEffect, useRef, useState } from "react";
import { useShopData } from "./useShopData";
import { useDesktopAgent } from "./useDesktopAgent";
import { createShopDeviceActions } from "./shop-device-actions";
import { createShopBillingActions } from "./shop-billing-actions";
import { useShopPrintActions } from "./useShopPrintActions";
import { useShopDialogs } from "./useShopDialogs";
import { useShopNavigation } from "./useShopNavigation";
import { shopSectionFromPath } from "../../portal-routing";
import type { ShopModal, ShopSection } from "./shop-types";
import OverviewPage from "./OverviewPage";
import { BatchReviewModal, JobHistoryModal, DocumentPreviewModal, RefundModal, ShopOwnerAccountModal, HelpSupportModal, PrinterModal, StationModal } from "./ShopOverlays";
import { Activity, Check, Clock3, CreditCard, FileText, LayoutDashboard, ChevronDown, LifeBuoy, LogOut, Menu, Monitor, Plus, Printer, QrCode, Settings, UserRound, ShieldCheck, Users, Wallet, X, PanelLeftClose, PanelLeftOpen } from "lucide-react";
import PhotoStudio from "../../PhotoStudio";
import { logout, type AuthTokens } from "../../api";
import { isDesktopApp } from "../../desktop-bridge";
import OrderJourneyPage from "../../OrderJourneyPage";
import SubscriptionPage from "../subscription/SubscriptionPage";
import PrintersPage from "./printers/PrintersPage";
import StationsPage from "./stations/StationsPage";
import ReportsPage from "./reports/ReportsPage";
import CustomersPage from "./customers/CustomersPage";
import Modal from "../../shared/ui/Modal";
import PricingPage from "./pricing/PricingPage";
import SettingsPage from "./settings/SettingsPage";
import PrintAgentPage from "./agent/PrintAgentPage";
import PaymentsPage from "./payments/PaymentsPage";
import PrintQueuePage from "./queue/PrintQueuePage";
import { isPrinterAvailable } from "../../shared/lib/printers";
import { IST_TIME_ZONE } from "../../date-time";

const sections: { label: ShopSection; icon: typeof LayoutDashboard }[] = [
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

const sectionGroups: { label: string; items: ShopSection[] }[] = [
  { label: "DAILY WORK", items: ["Order Journey", "Overview", "Print Queue", "Customers"] },
  { label: "DEVICES & CONNECTIVITY", items: ["Printers", "QR Stations", "Print Agent"] },
  { label: "BUSINESS", items: ["Pricing", "Payments"] },
  { label: "INSIGHTS", items: ["Reports"] }
];

export default function ShopConsole({ tokens, onSignOut, dark, onToggleTheme }: { tokens: AuthTokens; onSignOut: () => Promise<void>; dark: boolean; onToggleTheme: () => void }) {
  const [active, setActive] = useState<ShopSection>(() => shopSectionFromPath(window.location.pathname));
  const desktopAgentScheduleRef = useRef<(delayMs: number) => void>(() => {});

  const {
    cloudStatus, profile, setProfile, printers, setPrinters, stations, setStations, pricing, setPricing,
    printJobs, journeyJobs, journeyPayments, journeyRefreshing,
    hasMoreQueueJobs, loadingOlderQueueJobs, queueSearch, agentDevices, setAgentDevices,
    agentDevicesRef, paymentSettings, setPaymentSettings, payments, refunds, subscription,
    apiConnectionState, loading, error, setError,
    reload, refreshPrintQueue, loadOlderQueueJobs, searchPrintQueue, refreshOrderJourney
  } = useShopData({
    accessToken: tokens.accessToken,
    onSignOut,
    active,
    onDesktopAgentSync: () => desktopAgentScheduleRef.current(0)
  });
  const {
    sidebarOpen,setSidebarOpen,sidebarCollapsed,setSidebarCollapsed,accountMenuOpen,setAccountMenuOpen,
    helpOpen,setHelpOpen,ownerAccountOpen,setOwnerAccountOpen,accountMenuRef,modal,setModal,
    navAction,toggleSidebarCollapsed
  } = useShopNavigation({setActive,setError});



  const [busy, setBusy] = useState(false);
  const [toast, setToast] = useState("");
  const {
    actionDialog,actionDialogValue,setActionDialogValue,confirmInApp,promptInApp,closeActionDialog,submitActionDialog
  } = useShopDialogs();
  const [busyResourceId, setBusyResourceId] = useState<string | null>(null);
  const {
    busyJobId,setBusyJobId,preview,setPreview,focusShopPhotoEdit,setFocusShopPhotoEdit,shopPhotoStudio,
    refundJob,setRefundJob,batchReviewJobs,setBatchReviewJobs,batchReviewDecisions,setBatchReviewDecisions,
    batchReviewRetry,batchReviewLoadingId,eventHistory,setEventHistory,
    assignPrinter,updatePrintJobSettings,dispatchPrintJob,retryPdfExport,confirmPrintedOutput,previewJob,
    openShopPhotoStudio,openShopPhotoStudioForJob,cancelShopPhotoStudio,applyShopPhotoStudio,viewJobHistory,approveJob,rejectJob,
    applyBatchReview,recordFileRefund,openBatchReview
  } = useShopPrintActions({
    accessToken: tokens.accessToken, reload, confirmInApp, setError, setToast
  });
  const {
    localAgentStatus, localAgentConflict, localAgentRevoked, localAgentNeedsRepair,
    localAgentEnrollmentIssue, localAgentOwnershipChecking, enrollmentCode, agentBusy,
    generateAgentCode, connectLocalComputer, revokeAgent, scheduleDesktopAgentSync
  } = useDesktopAgent({
    accessToken: tokens.accessToken, onSignOut, confirmInApp, agentDevicesRef, setAgentDevices, setPrinters,
    reload, setError, setToast
  });
  desktopAgentScheduleRef.current = scheduleDesktopAgentSync;
  const {
    submitPrinter, submitStation, togglePrinter, toggleStation, createFreshStationCode
  } = createShopDeviceActions({
    accessToken: tokens.accessToken, busy, setBusy, busyResourceId, setBusyResourceId,
    setModal, setStations, setError, setToast, reload, confirmInApp
  });
  const {
    savePaymentSettings, verifyPayment, rejectPayment, saveProfile, saveOwnerAccount, savePricing
  } = createShopBillingActions({
    accessToken: tokens.accessToken, setBusy, setBusyJobId, setError, setToast, reload,
    setPaymentSettings, setProfile, setPricing, setOwnerAccountOpen, confirmInApp, promptInApp
  });








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
          {active === "Overview" && <OverviewPage profile={profile} printers={printers} stations={stations} printJobs={printJobs} pricing={pricing} agentDevices={agentDevices} paymentSettings={paymentSettings} onNavigate={navAction}/>}
          {active === "Printers" && <PrintersPage printers={printers} busyResourceId={busyResourceId} onAdd={() => setModal("printer")} onToggle={togglePrinter}/>}
          {active === "QR Stations" && <StationsPage stations={stations} profile={profile} busyResourceId={busyResourceId} onAdd={() => setModal("station")} onToggle={toggleStation} onGenerate={createFreshStationCode} onCopyLink={value=>void copyText(value)}/>}
          {active === "Print Agent" && <PrintAgentPage agents={agentDevices} enrollmentCode={enrollmentCode} busy={agentBusy} onGenerate={generateAgentCode} onRevoke={revokeAgent} desktop={isDesktopApp()} localAgentStatus={localAgentStatus} localAgentConflict={localAgentConflict} localAgentRevoked={localAgentRevoked} localAgentNeedsRepair={localAgentNeedsRepair} localAgentEnrollmentIssue={localAgentEnrollmentIssue} localAgentOwnershipChecking={localAgentOwnershipChecking} onConnect={()=>void connectLocalComputer()} onReconnectAfterRevocation={()=>void connectLocalComputer(false,agentDevices,true)} onCopyCode={value=>void copyText(value)}/>}
          {active === "Pricing" && pricing && <PricingPage pricing={pricing} onSave={savePricing} busy={busy}/>}
          {active === "Subscription" && subscription && <SubscriptionPage summary={subscription}/>}
          {active === "Payments" && paymentSettings && <PaymentsPage settings={paymentSettings} payments={payments} refunds={refunds} onSave={savePaymentSettings} onVerify={verifyPayment} onReject={rejectPayment} busy={busy} busyJobId={busyJobId}/>}
          {active === "Settings" && profile && <SettingsPage profile={profile} onSave={saveProfile} busy={busy}/>}
          {active === "Print Queue" && <PrintQueuePage jobs={printJobs} printers={printers} agentDevices={agentDevices} searchQuery={queueSearch} loadingBatchId={batchReviewLoadingId} onSearch={searchPrintQueue} onPreview={previewJob} onPreviewOutput={job=>void previewJob(job,true,"output")} onApprove={approveJob} onReject={rejectJob} onRefund={job=>setRefundJob(job)} onReviewBatch={openBatchReview} onHistory={viewJobHistory} onOpenPhotoStudio={job=>void openShopPhotoStudioForJob(job)} onAssignPrinter={assignPrinter} onUpdateSettings={updatePrintJobSettings} onDispatch={dispatchPrintJob} onConfirmOutput={confirmPrintedOutput} onRetryPdfExport={retryPdfExport} onRefresh={refreshPrintQueue} onLoadOlder={loadOlderQueueJobs} hasMoreJobs={hasMoreQueueJobs} loadingOlderJobs={loadingOlderQueueJobs} busyJobId={busyJobId}/>} 
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

function initials(value:string) { return value.trim().split(/\s+/).slice(0,2).map(part=>part[0]?.toUpperCase()||"").join("") || "SP"; }
function pageDescription(section:ShopSection) {
  const descriptions:Record<ShopSection,string> = {
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
