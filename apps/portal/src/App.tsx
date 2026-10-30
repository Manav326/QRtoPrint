import { useEffect, useState, type ReactNode } from "react";
import {
  Activity, ArrowRight, Check, Clock3, Copy, FileText, FileUp, LayoutDashboard,
  LifeBuoy, LogOut, Menu, Moon, Plus, Printer, QrCode, Settings,
  ShieldCheck, Sun, Users, Wallet, X
} from "lucide-react";
import QRCode from "react-qr-code";
import AuthScreen from "./AuthScreen";
import {
  ApiError, acceptCustomerQuote, approveShopPrintJob, calculateCustomerQuote,
  createCustomerSession, createPrinter, createStation, downloadShopDocument,
  getCustomerJobStatus, loadShopData, logout, readStoredTokens, rejectShopPrintJob,
  rotateStationCode, storeTokens, updatePrinterActive, updateShopPricing,
  updateShopProfile, updateStationEnabled, uploadCustomerDocument,
  type AuthTokens, type CustomerJobStatus, type PrinterDevice, type PrintQuoteResult,
  type QrStation, type ShopPricingProfile, type ShopPrintJob, type ShopProfile,
  type UploadedPrintJob
} from "./api";

type Section = "Overview" | "Print Queue" | "Printers" | "QR Stations" | "Pricing" | "Customers" | "Reports" | "Settings";
type Modal = "printer" | "station" | null;

const sections: { label: Section; icon: typeof LayoutDashboard }[] = [
  { label: "Overview", icon: LayoutDashboard },
  { label: "Print Queue", icon: FileText },
  { label: "Printers", icon: Printer },
  { label: "QR Stations", icon: QrCode },
  { label: "Pricing", icon: Wallet },
  { label: "Customers", icon: Users },
  { label: "Reports", icon: Activity },
  { label: "Settings", icon: Settings }
];

export default function App() {
  const [tokens, setTokens] = useState<AuthTokens | null>(() => readStoredTokens());
  const path = window.location.pathname;
  const stationRoute = path.match(/^\/s\/([A-Za-z0-9_-]+)\/?$/);

  if (stationRoute) {
    return <CustomerEntry publicCode={stationRoute[1]} />;
  }

  if (!tokens) {
    return <AuthScreen onAuthenticated={setTokens} />;
  }

  return <ShopConsole tokens={tokens} onSignOut={() => { storeTokens(null); setTokens(null); }} />;
}

function ShopConsole({ tokens, onSignOut }: { tokens: AuthTokens; onSignOut: () => void }) {
  const [active, setActive] = useState<Section>("Overview");
  const [dark, setDark] = useState(true);
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [profile, setProfile] = useState<ShopProfile | null>(null);
  const [printers, setPrinters] = useState<PrinterDevice[]>([]);
  const [stations, setStations] = useState<QrStation[]>([]);
  const [pricing, setPricing] = useState<ShopPricingProfile | null>(null);
  const [printJobs, setPrintJobs] = useState<ShopPrintJob[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [modal, setModal] = useState<Modal>(null);
  const [busy, setBusy] = useState(false);
  const [toast, setToast] = useState("");
  const [preview, setPreview] = useState<{ name: string; url: string; mediaType: string } | null>(null);
  const [busyJobId, setBusyJobId] = useState<string | null>(null);

  async function reload() {
    setLoading(true);
    setError("");
    try {
      const snapshot = await loadShopData(tokens.accessToken);
      setProfile(snapshot.profile);
      setPrinters(snapshot.printers);
      setStations(snapshot.stations);
      setPricing(snapshot.pricing);
      setPrintJobs(snapshot.printJobs);
    } catch (exception) {
      if (exception instanceof ApiError && exception.status === 401) {
        storeTokens(null);
        onSignOut();
      } else {
        setError(exception instanceof Error ? exception.message : "Could not load your shop.");
      }
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    void reload();
    // A token change is the only trigger; mutations call reload explicitly.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tokens.accessToken]);

  const today = new Intl.DateTimeFormat("en-IN", { weekday: "long", day: "numeric", month: "long" }).format(new Date());

  async function signOut() {
    try { await logout(tokens.accessToken); } catch { /* Local sign-out still clears the session. */ }
    onSignOut();
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
    const confirmed = window.confirm("Generate a fresh QR code? Any previously printed QR code for this station will stop working.");
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
      setPreview({ name: job.document.originalFileName, url, mediaType: blob.type || job.document.mediaType });
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not open the document.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function approveJob(job: ShopPrintJob) {
    if (!window.confirm(`Approve ${job.jobNumber} for payment? It will not be sent to a printer yet.`)) return;
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
    if (!window.confirm(`Reject ${job.jobNumber}? The customer will see that the shop declined the request.`)) return;
    setBusyJobId(job.id);
    setError("");
    try {
      await rejectShopPrintJob(tokens.accessToken, job.id);
      setToast("The print request was rejected.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not reject this job.");
    } finally {
      setBusyJobId(null);
    }
  }

  const navAction = (section: Section) => { setActive(section); setSidebarOpen(false); setError(""); };

  if (loading && !profile) {
    return <div className={dark ? "app-shell dark boot-screen" : "app-shell light boot-screen"}><div className="boot-card"><div className="brand-mark"><QrCode size={22}/></div><div className="boot-title">Preparing your workspace</div><div className="boot-subtitle">Connecting securely to your shop…</div><div className="button-spinner boot-spinner"/></div></div>;
  }

  return (
    <div className={dark ? "app-shell dark" : "app-shell light"}>
      <aside className={sidebarOpen ? "sidebar sidebar-open" : "sidebar"}>
        <div className="brand-lockup">
          <div className="brand-mark"><QrCode size={21} strokeWidth={2.4}/></div>
          <div><div className="brand-name">smart<span>print</span></div><div className="brand-caption">BY QRtoPrint</div></div>
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
            <button key={label} className={active === label ? "nav-item active" : "nav-item"} onClick={() => navAction(label)}>
              <Icon size={18} strokeWidth={1.8}/><span>{label}</span>{label === "Print Queue" && <span className="nav-soon">SOON</span>}
            </button>
          ))}
        </nav>
        <div className="sidebar-bottom">
          <div className="plan-card"><div className="plan-icon"><ShieldCheck size={17}/></div><div><strong>Trial workspace</strong><span>14-day introductory access</span></div></div>
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
            <button className="icon-button mobile-menu" aria-label="Open navigation" onClick={() => setSidebarOpen(true)}><Menu size={20}/></button>
            <div className="breadcrumb"><span>Shop workspace</span><span className="crumb-slash">/</span><strong>{active}</strong></div>
          </div>
          <div className="topbar-actions">
            <div className="connection-pill"><span className="live-dot"/><span>Cloud connected</span></div>
            <button className="icon-button theme-button" aria-label={dark ? "Switch to light theme" : "Switch to dark theme"} onClick={() => setDark(!dark)}>{dark ? <Sun size={18}/> : <Moon size={18}/>}</button>
            <div className="topbar-divider"/>
            <span className="topbar-product-tag"><span className="mode-indicator"/> Shop console</span>
          </div>
        </header>

        <div className="content-wrap">
          <div className="page-heading">
            <div><div className="eyebrow"><span className="eyebrow-line"/>SHOP WORKSPACE</div><h1>{active === "Overview" ? `Welcome, ${(profile?.ownerName || "there").split(" ")[0]}` : active}</h1><p>{active === "Overview" ? "Your shop setup and operations, all in one place." : pageDescription(active)}</p></div>
            <div className="heading-actions"><span className="date-chip"><Clock3 size={15}/>{today}</span>{active === "Printers" && <button className="primary-button" onClick={() => setModal("printer")}><Plus size={16}/> Add printer</button>}{active === "QR Stations" && <button className="primary-button" onClick={() => setModal("station")}><Plus size={16}/> Create QR station</button>}</div>
          </div>

          {error && <div className="inline-error" role="alert"><span>{error}</span><button onClick={() => setError("")} aria-label="Dismiss error"><X size={15}/></button></div>}
          {toast && <div className="inline-success" role="status"><Check size={15}/><span>{toast}</span><button onClick={() => setToast("")} aria-label="Dismiss message"><X size={15}/></button></div>}

          {active === "Overview" && <Overview profile={profile} printers={printers} stations={stations} printJobs={printJobs} pricing={pricing} onNavigate={navAction}/>}
          {active === "Printers" && <PrintersPage printers={printers} onAdd={() => setModal("printer")} onToggle={togglePrinter}/>}
          {active === "QR Stations" && <StationsPage stations={stations} onAdd={() => setModal("station")} onToggle={toggleStation} onGenerate={createFreshStationCode}/>}
          {active === "Pricing" && pricing && <PricingPage pricing={pricing} onSave={savePricing} busy={busy}/>}
          {active === "Settings" && profile && <SettingsPage profile={profile} onSave={saveProfile} busy={busy}/>}
          {active === "Print Queue" && <PrintQueuePage jobs={printJobs} onPreview={previewJob} onApprove={approveJob} onReject={rejectJob} busyJobId={busyJobId}/>}
          {active === "Customers" && <EmptyModule icon={<Users size={24}/>} title="Customer directory is coming next" text="Customer profiles and print history will be connected to completed print jobs. No sample customer records are shown."/>}
          {active === "Reports" && <EmptyModule icon={<Activity size={24}/>} title="Reports will build from real activity" text="Revenue and print-volume reports will appear when job pricing, payment reconciliation and completion events are connected."/>}

          <footer className="app-footer"><span>© 2026 Smart Print by QRtoPrint</span><span><span className="footer-status"/> API session authenticated</span><span>Made for print shops, with care.</span></footer>
        </div>
      </main>

      {modal === "printer" && <PrinterModal busy={busy} onClose={() => setModal(null)} onSubmit={submitPrinter}/>}
      {modal === "station" && <StationModal busy={busy} printers={printers.filter(printer => printer.isActive)} onClose={() => setModal(null)} onSubmit={submitStation}/>}
      {preview && <DocumentPreviewModal name={preview.name} url={preview.url} mediaType={preview.mediaType} onClose={() => { URL.revokeObjectURL(preview.url); setPreview(null); }}/>}
    </div>
  );
}

function Overview({ profile, printers, stations, printJobs, pricing, onNavigate }: {
  profile: ShopProfile | null; printers: PrinterDevice[]; stations: QrStation[];
  printJobs: ShopPrintJob[]; pricing: ShopPricingProfile | null; onNavigate: (section: Section) => void;
}) {
  const activePrinters = printers.filter(printer => printer.isActive);
  const todayKey = new Date().toDateString();
  const jobsToday = printJobs.filter(job => new Date(job.createdUtc).toDateString() === todayKey);
  const quotesAwaiting = printJobs.filter(job => job.status === "AWAITING_CUSTOMER_APPROVAL");
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
              {printJobs.slice(0,4).map(job=><div className="activity-job-row" key={job.id}><div className="activity-job-icon"><FileText size={16}/></div><div className="activity-job-copy"><strong>{job.jobNumber}</strong><span>{job.document.originalFileName} · {job.pageCount} pages</span></div><div className="activity-job-status"><Status label={prettyStatus(job.status)} tone={job.status==="AWAITING_CUSTOMER_APPROVAL"?"amber":"blue"}/><small>{new Date(job.createdUtc).toLocaleTimeString([], {hour:"2-digit",minute:"2-digit"})}</small></div></div>)}
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
        <div className="panel-heading"><div><h2>Shop setup checklist</h2><p>Complete these steps before accepting customer print jobs.</p></div><span className="setup-count">{Number(!!profile?.displayName)+Number(printers.length>0)+Number(stations.length>0)+Number(!!pricing?.isConfigured)} / 4 complete</span></div>
        <div className="setup-checks">
          <SetupCheck complete={!!profile?.displayName} title="Create your shop identity" detail="Your shop name and customer-facing brand." onClick={()=>onNavigate("Settings")}/>
          <SetupCheck complete={printers.length>0} title="Register your printers" detail="Add the printers available at your counter." onClick={()=>onNavigate("Printers")}/>
          <SetupCheck complete={stations.length>0} title="Create your first QR station" detail="Give customers a secure way to connect." onClick={()=>onNavigate("QR Stations")}/>
          <SetupCheck complete={!!pricing?.isConfigured} title="Set your print prices" detail="Configure mono, colour and duplex rates." onClick={()=>onNavigate("Pricing")}/>
        </div>
      </section>
    </>
  );
}

function PrintersPage({ printers, onAdd, onToggle }: { printers: PrinterDevice[]; onAdd:()=>void; onToggle:(printer:PrinterDevice)=>void }) {
  return (
    <section className="panel collection-panel">
      <div className="collection-header"><div><h2>Your printers</h2><p>Register different printer manufacturers in the same shop workspace.</p></div><button className="primary-button" onClick={onAdd}><Plus size={16}/> Add printer</button></div>
      {printers.length===0 ? <EmptyModule icon={<Printer size={24}/>} title="No printers registered yet" text="Add your first printer. It will appear here as awaiting local-agent discovery until the Windows Print Agent reports its capabilities and live status." actionLabel="Add your first printer" onAction={onAdd}/> :
      <div className="printer-grid">{printers.map(printer=><article className="printer-card" key={printer.id}><div className="printer-card-top"><div className="printer-card-icon"><Printer size={20}/></div><Status label={printer.status} tone={printer.status.toLowerCase()==="ready"?"green":"slate"}/></div><h3>{printer.displayName}</h3><p>{printer.manufacturer} · {printer.model}</p><div className="printer-meta"><span>{printer.connectionType}</span><span>{printer.queueName || printer.connectionAddress || "Connection not verified"}</span></div><div className="printer-card-footer"><span className={printer.isActive?"enabled-label":"disabled-label"}>{printer.isActive?"Enabled":"Disabled"}</span><button className="subtle-button" onClick={()=>onToggle(printer)}>{printer.isActive?"Disable":"Enable"}</button></div></article>)}</div>}
    </section>
  );
}

function StationsPage({ stations, onAdd, onToggle, onGenerate }: {
  stations:QrStation[]; onAdd:()=>void; onToggle:(station:QrStation)=>void; onGenerate:(station:QrStation)=>void;
}) {
  return (
    <section className="panel collection-panel">
      <div className="collection-header"><div><h2>Customer QR stations</h2><p>Each station has its own identifier. Scanning it creates a short-lived customer session.</p></div><button className="primary-button" onClick={onAdd}><Plus size={16}/> Create station</button></div>
      {stations.length===0 ? <EmptyModule icon={<QrCode size={24}/>} title="Your first QR station starts here" text="Create a station for a counter or printer. The QR code identifies the station; every scan gets a separate expiring customer session." actionLabel="Create QR station" onAction={onAdd}/> :
      <div className="station-grid">{stations.map(station=><article className="station-card" key={station.id}><div className="station-card-head"><div className="station-icon"><QrCode size={18}/></div><div className="station-head-copy"><strong>{station.displayName}</strong><span>{station.isEnabled?"Accepting new sessions":"Disabled"}</span></div><Status label={station.isEnabled?"Enabled":"Disabled"} tone={station.isEnabled?"green":"slate"}/></div>
        <div className="station-qr-area">{station.publicUrl ? <div className="qr-paper"><QRCode value={station.publicUrl} size={148} level="M" fgColor="#121722" bgColor="#ffffff"/><span>SCAN TO PRINT</span></div> : <div className="qr-missing"><QrCode size={34}/><strong>QR code not stored</strong><span>Generate a fresh code to replace the old one.</span><button className="secondary-button" onClick={()=>onGenerate(station)}>Generate fresh QR</button></div>}</div>
        <div className="station-foot"><span>Code ending ···{station.publicCodeHint}</span><button className="subtle-button" onClick={()=>onToggle(station)}>{station.isEnabled?"Disable station":"Enable station"}</button></div>
        {station.publicUrl && <div className="station-actions"><button className="secondary-button" onClick={()=>void copyText(station.publicUrl!)}> <Copy size={14}/> Copy link</button><button className="secondary-button" onClick={()=>window.print()}>Print QR</button></div>}
      </article>)}</div>}
    </section>
  );
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

function PrintQueuePage({ jobs, onPreview, onApprove, onReject, busyJobId }: {
  jobs: ShopPrintJob[];
  onPreview: (job: ShopPrintJob) => void;
  onApprove: (job: ShopPrintJob) => void;
  onReject: (job: ShopPrintJob) => void;
  busyJobId: string | null;
}) {
  return <section className="panel queue-panel live-queue-panel">
    <div className="collection-header"><div><h2>Customer print jobs</h2><p>Review real requests. Shop approval moves a job to payment; it does not trigger physical printing.</p></div><span className="setup-count">{jobs.length} recent jobs</span></div>
    {jobs.length===0 ? <EmptyModule icon={<FileText size={24}/>} title="Your queue is clear" text="When a customer uploads a document through one of your QR stations, the job and its current state will appear here."/> :
      <div className="table-scroll"><table><thead><tr><th>JOB</th><th>DOCUMENT</th><th>PRINT SETTINGS</th><th>QUOTE</th><th>STATUS</th><th>CREATED</th><th>ACTIONS</th></tr></thead><tbody>
        {jobs.map(job=><tr key={job.id}>
          <td><strong className="job-id">{job.jobNumber}</strong></td>
          <td><div className="customer-cell"><strong>{job.document.originalFileName}</strong><span>{formatBytes(job.document.sizeBytes)} · {job.document.mediaType}</span></div></td>
          <td><div className="detail-cell"><strong>{job.pageCount} pages × {job.copies}</strong><span>{job.colourMode} · {job.paperSize} · {job.isDuplex?"Duplex":"Single-sided"}</span></div></td>
          <td className="amount-cell">{job.quotedAmountMinor>0?formatMoney(job.quotedAmountMinor,job.currency):"Not quoted"}</td>
          <td><Status label={prettyStatus(job.status)} tone={job.status==="AWAITING_CUSTOMER_APPROVAL"||job.status==="AWAITING_SHOP_APPROVAL"?"amber":job.status==="COMPLETED"?"green":"blue"}/></td>
          <td className="time-cell">{new Date(job.createdUtc).toLocaleString()}</td>
          <td><div className="queue-actions"><button className="subtle-button" disabled={busyJobId===job.id} onClick={()=>onPreview(job)}>Preview</button>{job.status==="AWAITING_SHOP_APPROVAL"&&<><button className="subtle-button approve-action" disabled={busyJobId===job.id} onClick={()=>onApprove(job)}>Approve</button><button className="subtle-button reject-action" disabled={busyJobId===job.id} onClick={()=>onReject(job)}>Reject</button></>}</div></td>
        </tr>)}
      </tbody></table></div>}
  </section>;
}

function DocumentPreviewModal({name,url,mediaType,onClose}:{name:string;url:string;mediaType:string;onClose:()=>void}) {
  const isPdf=mediaType==="application/pdf";
  return <Modal title={name} subtitle="Private shop document preview. The encrypted original remains in protected shop storage." onClose={onClose}>
    <div className="shop-document-preview">{isPdf?<iframe src={url} title={name}/>:<img src={url} alt={name}/>}</div>
    <div className="modal-actions"><button className="secondary-button" onClick={onClose}>Close preview</button></div>
  </Modal>;
}

function prettyStatus(status:string) {
  const names:Record<string,string>={
    AWAITING_CONFIGURATION:"Configure options",
    AWAITING_CUSTOMER_APPROVAL:"Quote awaiting customer",
    QUOTE_EXPIRED:"Quote expired",
    AWAITING_SHOP_APPROVAL:"Awaiting shop approval",
    REJECTED:"Rejected",

    UPLOADED:"Uploaded",
    VALIDATING:"Validating",
    ANALYSING:"Analysing",
    AWAITING_PAYMENT:"Awaiting payment",
    AWAITING_COUNTER_PAYMENT:"Pay at counter",
    AUTHORISED:"Authorised",
    QUEUED:"Queued",
    SUBMITTING_TO_SPOOLER:"Sending to printer",
    SPOOLER_ACCEPTED:"Accepted by spooler",
    PRINTING:"Printing",
    COMPLETED:"Completed",
    OUTPUT_UNCONFIRMED:"Output unconfirmed",
    FAILED:"Failed",
    CANCELLED:"Cancelled"
  };
  return names[status] || status.replaceAll("_"," ").toLowerCase().replace(/\\b\\w/g,letter=>letter.toUpperCase());
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
function pageDescription(section:Section) {
  const descriptions:Record<Section,string> = {
    "Overview":"Your shop setup and operations, all in one place.",
    "Print Queue":"Review real customer uploads and server-calculated print quotes.",
    "Printers":"Manage connected devices, capabilities and health.",
    "QR Stations":"Create secure customer entry points for each counter.",
    "Pricing":"Set your mono, colour and duplex rates. Payment collection is a separate step.",
    "Customers":"View customer activity and print history.",
    "Reports":"Understand print volume, shop revenue and performance.",
    "Settings":"Configure your shop identity and preferences."
  };
  return descriptions[section];
}
async function copyText(value:string) {
  try { await navigator.clipboard.writeText(value); window.alert("Copied to clipboard."); }
  catch { window.alert(value); }
}

function CustomerEntry({publicCode}:{publicCode:string}) {
  const [result,setResult]=useState<Awaited<ReturnType<typeof createCustomerSession>>|null>(null);
  const [error,setError]=useState("");
  const [loading,setLoading]=useState(true);
  useEffect(()=>{
    let mounted=true;
    void createCustomerSession(publicCode).then(value=>{
      if(mounted){
        setResult(value);
        sessionStorage.setItem("qrtoprint.customer-session",value.sessionToken);
      }
    }).catch(exception=>{
      if(mounted)setError(exception instanceof Error?exception.message:"This QR station is unavailable.");
    }).finally(()=>{if(mounted)setLoading(false);});
    return ()=>{mounted=false;};
  },[publicCode]);

  return <div className="customer-entry"><div className="customer-entry-glow"/>
    <header className="customer-entry-brand"><div className="brand-mark"><QrCode size={21}/></div><div><div className="brand-name">smart<span>print</span></div><div className="brand-caption">BY QRtoPrint</div></div></header>
    <main className="customer-entry-card">
      {loading?<><div className="customer-entry-spinner"><span className="button-spinner"/></div><h1>Connecting to your print shop</h1><p>Creating a secure, time-limited session for this QR station.</p></>:
      error?<><div className="entry-error-icon"><X size={23}/></div><div className="entry-eyebrow">STATION UNAVAILABLE</div><h1>This QR station can't be reached</h1><p>{error}</p><button className="primary-button" onClick={()=>window.location.reload()}>Try again</button></>:
      result?<><div className="entry-success-icon"><Check size={23}/></div><div className="entry-eyebrow">SECURE SESSION CREATED</div><h1>Welcome to {result.shop.displayName}</h1><p>Your phone is connected to this shop's print station.</p>
        <div className="entry-printer-card"><div className="entry-printer-icon"><Printer size={20}/></div><div><strong>{result.printer?.displayName||"Shop print counter"}</strong><span>{result.printer?result.printer.manufacturer+" · "+result.printer.model:"The shop will select a printer for your job."}</span></div><Status label={result.printer?.status||"Session ready"} tone="blue"/></div>
        <div className="entry-session-note"><ShieldCheck size={16}/><span>This session expires at {new Date(result.expiresUtc).toLocaleTimeString([], {hour:"2-digit",minute:"2-digit"})}. Your document access stays limited to this shop session.</span></div>
        <CustomerPrintFlow sessionToken={result.sessionToken}/>
      </>:null}
    </main>
    <footer className="customer-entry-footer">© 2026 Smart Print · Secure customer printing</footer>
  </div>;
}

function CustomerPrintFlow({sessionToken}:{sessionToken:string}) {
  const [file,setFile]=useState<File|null>(null);
  const [previewUrl,setPreviewUrl]=useState<string|null>(null);
  const [uploaded,setUploaded]=useState<UploadedPrintJob|null>(null);
  const [quote,setQuote]=useState<PrintQuoteResult|null>(null);
  const [jobStatus,setJobStatus]=useState<CustomerJobStatus|null>(null);
  const [colourMode,setColourMode]=useState<"Mono"|"Colour">("Mono");
  const [isDuplex,setIsDuplex]=useState(false);
  const [copies,setCopies]=useState(1);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");

  useEffect(()=>{
    if(!file){setPreviewUrl(null);return;}
    const url=URL.createObjectURL(file);
    setPreviewUrl(url);
    return ()=>URL.revokeObjectURL(url);
  },[file]);

  async function upload() {
    if(!file)return;
    setBusy(true);setError("");setQuote(null);setJobStatus(null);
    try {
      const created=await uploadCustomerDocument(sessionToken,file);
      setUploaded(created);
    } catch(exception) {
      setError(exception instanceof ApiError?exception.message:"The document could not be uploaded.");
    } finally {setBusy(false);}
  }

  async function getQuote() {
    if(!uploaded)return;
    setBusy(true);setError("");setJobStatus(null);
    try {
      const calculated=await calculateCustomerQuote(sessionToken,uploaded.job.id,{colourMode,paperSize:"A4",isDuplex,copies});
      setQuote(calculated);
    } catch(exception) {
      setError(exception instanceof ApiError?exception.message:"The shop could not calculate a quote.");
    } finally {setBusy(false);}
  }

  async function acceptQuote() {
    if(!uploaded||!quote)return;
    setBusy(true);setError("");
    try {
      const accepted=await acceptCustomerQuote(sessionToken,uploaded.job.id);
      setJobStatus(await getCustomerJobStatus(sessionToken,uploaded.job.id));
      setQuote({...quote,status:accepted.status});
    } catch(exception) {
      setError(exception instanceof ApiError?exception.message:"The quote could not be accepted.");
    } finally {setBusy(false);}
  }

  async function refreshJobStatus() {
    if(!uploaded)return;
    setBusy(true);setError("");
    try {setJobStatus(await getCustomerJobStatus(sessionToken,uploaded.job.id));}
    catch(exception){setError(exception instanceof ApiError?exception.message:"Could not refresh job status.");}
    finally {setBusy(false);}
  }

  function chooseFile(next:File|null) {
    setFile(next);setUploaded(null);setQuote(null);setJobStatus(null);setError("");
  }

  return <section className="customer-print-flow">
    <div className="customer-flow-heading"><div><span>STEP 1</span><h2>Add your document</h2></div><span className="upload-limit">PDF, JPG or PNG · Up to 20 MB</span></div>
    <label className="customer-upload-zone">
      <input type="file" accept=".pdf,.jpg,.jpeg,.png,application/pdf,image/jpeg,image/png" onChange={event=>chooseFile(event.target.files?.[0]||null)}/>
      <span className="customer-upload-icon"><FileUp size={21}/></span>
      <strong>{file?file.name:"Choose a document to print"}</strong>
      <small>{file?formatBytes(file.size)+" · Select another file to replace":"Tap to browse files on your phone"}</small>
    </label>
    {previewUrl&&file&&<div className="customer-file-preview"><div className="customer-file-label"><FileText size={15}/><span>Document preview</span></div>{file.type==="application/pdf"?<iframe src={previewUrl} title="PDF document preview"/>:<img src={previewUrl} alt="Selected document preview"/>}</div>}
    {file&&!uploaded&&<button className="customer-flow-button" disabled={busy} onClick={()=>void upload()}>{busy?<span className="button-spinner"/>:<><UploadIcon/>Upload document</>}</button>}
    {uploaded&&<>
      <div className="uploaded-document-card"><div className="uploaded-document-icon"><FileText size={18}/></div><div><strong>{uploaded.document.originalFileName}</strong><span>{uploaded.job.pageCount} {uploaded.job.pageCount===1?"page":"pages"} · {formatBytes(uploaded.document.sizeBytes)} · Validated</span></div><Status label="Uploaded" tone="green"/></div>
      {!quote&&!jobStatus&&<>
        <div className="customer-flow-heading customer-config-heading"><div><span>STEP 2</span><h2>Choose print settings</h2></div></div>
        <div className="customer-option-group"><span>Colour mode</span><div className="customer-choice-row"><button className={colourMode==="Mono"?"customer-choice selected":"customer-choice"} onClick={()=>{setColourMode("Mono");setQuote(null);}}>Black & white</button><button className={colourMode==="Colour"?"customer-choice selected":"customer-choice"} onClick={()=>{setColourMode("Colour");setQuote(null);}}>Colour</button></div></div>
        <div className="customer-option-grid"><label>Copies<input type="number" min="1" max="100" value={copies} onChange={event=>{setCopies(Math.max(1,Math.min(100,Number(event.target.value)||1)));setQuote(null);}}/></label><label className="customer-duplex-toggle"><input type="checkbox" checked={isDuplex} onChange={event=>{setIsDuplex(event.target.checked);setQuote(null);}}/><span><strong>Double-sided</strong><small>Print on both sides of each sheet</small></span></label></div>
        <div className="customer-paper-note"><Check size={14}/> A4 paper · Page order preserved · {colourMode==="Mono"?"Black & white":"Colour"} printing</div>
        <button className="customer-flow-button" disabled={busy||copies<1} onClick={()=>void getQuote()}>{busy?<span className="button-spinner"/>:"Calculate print price"}</button>
      </>}
    </>}
    {error&&<div className="customer-flow-error" role="alert">{error}</div>}
    {quote&&<div className="customer-quote-card"><div><span>YOUR PRINT QUOTE</span><strong>{formatMoney(quote.quotedAmountMinor,quote.currency)}</strong><small>{quote.pageCount} pages · {quote.copies} copies · {quote.colourMode} · {quote.isDuplex?"Duplex":"Single-sided"}</small></div><div className="quote-validity"><ShieldCheck size={15}/><span>Valid until {new Date(quote.quoteExpiresUtc).toLocaleTimeString([], {hour:"2-digit",minute:"2-digit"})}</span></div></div>}
    {quote&&quote.status!=="AWAITING_SHOP_APPROVAL"&&!jobStatus&&<button className="customer-flow-button" disabled={busy} onClick={()=>void acceptQuote()}>{busy?<span className="button-spinner"/>:"Accept quote and send to shop"}</button>}
    {jobStatus&&<div className="customer-job-state"><div className="customer-job-state-top"><Status label={prettyStatus(jobStatus.status)} tone={jobStatus.status==="REJECTED"?"red":jobStatus.status==="AWAITING_PAYMENT"?"green":"amber"}/><button className="customer-refresh-status" disabled={busy} onClick={()=>void refreshJobStatus()}>{busy?<span className="button-spinner"/>:"Refresh status"}</button></div>
      <strong>{jobStatus.status==="AWAITING_SHOP_APPROVAL"?"Your request is with the shop":jobStatus.status==="AWAITING_PAYMENT"?"The shop accepted your request":jobStatus.status==="REJECTED"?"The shop could not accept this request":jobStatus.status==="QUOTE_EXPIRED"?"Your quote has expired":"Your request is being reviewed"}</strong>
      <p>{jobStatus.status==="AWAITING_SHOP_APPROVAL"?"The shop will review your request next. You have not been charged and printing has not started.":jobStatus.status==="AWAITING_PAYMENT"?"The shop approved this quote. Payment setup is not available yet, so no money has been collected and the document has not been printed.":jobStatus.status==="REJECTED"?"Please speak with the shop operator if you need help with this request.":"Check back for the latest request status."}</p>
    </div>}
    <div className="customer-payment-boundary"><ShieldCheck size={15}/><span>{jobStatus?.status==="AWAITING_PAYMENT"?"Shop approval is recorded, but payment collection is not configured. This job cannot be printed until the shop's payment flow is integrated and verified.":quote?"This quote is calculated by the shop's saved rate card. Accepting it sends a request to the shop; it does not collect payment or start printing.":"Your document is validated before pricing. You will see the shop's calculated price before any payment or print approval."}</span></div>
  </section>;
}

function UploadIcon() {
  return <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M12 16V4m0 0L7 9m5-5 5 5"/><path d="M4 16v3a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-3"/></svg>;
}
