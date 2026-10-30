import { useEffect, useState, type FormEvent, type ReactNode } from "react";
import {
  Activity, ArrowRight, Check, Clock3, Copy, FileText, LayoutDashboard,
  LifeBuoy, LogOut, Menu, Moon, Plus, Printer, QrCode, Settings,
  ShieldCheck, Sun, Users, Wallet, X
} from "lucide-react";
import QRCode from "react-qr-code";
import AuthScreen from "./AuthScreen";
import {
  ApiError, createCustomerSession, createPrinter, createStation,
  loadShopData, logout, readStoredTokens, rotateStationCode, storeTokens,
  updatePrinterActive, updateShopProfile, updateStationEnabled,
  type AuthTokens, type PrinterDevice, type QrStation, type ShopProfile
} from "./api";

type Section = "Overview" | "Print Queue" | "Printers" | "QR Stations" | "Customers" | "Reports" | "Settings";
type Modal = "printer" | "station" | null;

const sections: { label: Section; icon: typeof LayoutDashboard }[] = [
  { label: "Overview", icon: LayoutDashboard },
  { label: "Print Queue", icon: FileText },
  { label: "Printers", icon: Printer },
  { label: "QR Stations", icon: QrCode },
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
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [modal, setModal] = useState<Modal>(null);
  const [busy, setBusy] = useState(false);
  const [toast, setToast] = useState("");

  async function reload() {
    setLoading(true);
    setError("");
    try {
      const snapshot = await loadShopData(tokens.accessToken);
      setProfile(snapshot.profile);
      setPrinters(snapshot.printers);
      setStations(snapshot.stations);
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

          {active === "Overview" && <Overview profile={profile} printers={printers} stations={stations} onNavigate={navAction}/>}
          {active === "Printers" && <PrintersPage printers={printers} onAdd={() => setModal("printer")} onToggle={togglePrinter}/>}
          {active === "QR Stations" && <StationsPage stations={stations} printers={printers} onAdd={() => setModal("station")} onToggle={toggleStation} onGenerate={createFreshStationCode}/>}
          {active === "Settings" && profile && <SettingsPage profile={profile} onSave={saveProfile} busy={busy}/>}
          {active === "Print Queue" && <EmptyModule icon={<FileText size={24}/>} title="Your print queue will appear here" text="Customer uploads, approvals and physical print states will be shown here once the document and print-job workflow is connected."/>}
          {active === "Customers" && <EmptyModule icon={<Users size={24}/>} title="Customer directory is coming next" text="Customer profiles and print history will be connected to completed print jobs. No sample customer records are shown."/>}
          {active === "Reports" && <EmptyModule icon={<Activity size={24}/>} title="Reports will build from real activity" text="Revenue and print-volume reports will appear when job pricing, payment reconciliation and completion events are connected."/>}

          <footer className="app-footer"><span>© 2026 Smart Print by QRtoPrint</span><span><span className="footer-status"/> API session authenticated</span><span>Made for print shops, with care.</span></footer>
        </div>
      </main>

      {modal === "printer" && <PrinterModal busy={busy} onClose={() => setModal(null)} onSubmit={submitPrinter}/>}
      {modal === "station" && <StationModal busy={busy} printers={printers.filter(printer => printer.isActive)} onClose={() => setModal(null)} onSubmit={submitStation}/>}
    </div>
  );
}

function Overview({ profile, printers, stations, onNavigate }: {
  profile: ShopProfile | null; printers: PrinterDevice[]; stations: QrStation[]; onNavigate: (section: Section) => void;
}) {
  const activePrinters = printers.filter(printer => printer.isActive);
  return (
    <>
      <div className="stat-grid">
        <Metric icon={<Printer size={18}/>} label="Registered printers" value={String(printers.length)} note={`${activePrinters.length} enabled for use`} accent="blue"/>
        <Metric icon={<QrCode size={18}/>} label="QR stations" value={String(stations.length)} note={`${stations.filter(station=>station.isEnabled).length} currently enabled`} accent="violet"/>
        <Metric icon={<FileText size={18}/>} label="Print jobs today" value="—" note="Job workflow not connected yet" accent="amber"/>
        <Metric icon={<Wallet size={18}/>} label="Today's shop revenue" value="—" note="Payment integration not connected" accent="mint"/>
      </div>
      <div className="dashboard-grid">
        <section className="panel activity-empty-panel">
          <div className="panel-heading"><div><h2>Shop activity</h2><p>Real print activity will appear here</p></div><span className="neutral-chip">Awaiting first job</span></div>
          <div className="activity-empty-content"><div className="activity-empty-art"><div className="activity-art-sheet"><FileText size={25}/><i/><i/><i/></div><div className="activity-art-printer"><Printer size={27}/></div><span className="activity-art-spark">✦</span></div><h3>Ready when your first customer is</h3><p>Connect a printer and create a QR station. Your queue and activity insights will start filling in when customer print jobs are enabled.</p><button className="text-action" onClick={() => onNavigate(printers.length ? "QR Stations" : "Printers")}>{printers.length ? "Create your first QR station" : "Add your first printer"} <span>→</span></button></div>
        </section>
        <section className="panel printer-panel">
          <div className="panel-heading"><div><h2>Printer setup</h2><p>Registered shop devices</p></div><button className="text-action" onClick={() => onNavigate("Printers")}>Manage <span>→</span></button></div>
          <div className="printer-summary"><div className="printer-summary-icon"><Printer size={19}/></div><div><strong>{printers.length} registered</strong><span>Live hardware status awaits the local agent</span></div><div className="health-score neutral-score">—</div></div>
          <div className="device-list">{printers.slice(0,4).map(printer=><Device key={printer.id} name={printer.displayName} detail={`${printer.manufacturer} · ${printer.connectionType}`} state={printer.status} tone={printer.status.toLowerCase()==="ready"?"green":"slate"}/>)}{printers.length===0 && <div className="mini-empty">No printers registered yet.</div>}</div>
          <button className="text-action full-action" onClick={() => onNavigate("Printers")}>View all printers <span>→</span></button>
        </section>
      </div>
      <section className="panel setup-panel">
        <div className="panel-heading"><div><h2>Shop setup checklist</h2><p>Complete these steps before accepting customer print jobs.</p></div><span className="setup-count">{Number(printers.length>0)+Number(stations.length>0)} / 3 complete</span></div>
        <div className="setup-checks">
          <SetupCheck complete={!!profile?.displayName} title="Create your shop identity" detail="Your shop name and customer-facing brand." onClick={()=>onNavigate("Settings")}/>
          <SetupCheck complete={printers.length>0} title="Register your printers" detail="Add the printers available at your counter." onClick={()=>onNavigate("Printers")}/>
          <SetupCheck complete={stations.length>0} title="Create your first QR station" detail="Give customers a secure way to connect." onClick={()=>onNavigate("QR Stations")}/>
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

function StationsPage({ stations, printers, onAdd, onToggle, onGenerate }: {
  stations:QrStation[]; printers:PrinterDevice[]; onAdd:()=>void; onToggle:(station:QrStation)=>void; onGenerate:(station:QrStation)=>void;
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
    "Print Queue":"Review, approve and manage customer print jobs.",
    "Printers":"Manage connected devices, capabilities and health.",
    "QR Stations":"Create secure customer entry points for each counter.",
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
    void createCustomerSession(publicCode).then(value=>{if(mounted){setResult(value);sessionStorage.setItem("qrtoprint.customer-session",value.sessionToken);}})
      .catch(exception=>{if(mounted)setError(exception instanceof Error?exception.message:"This QR station is unavailable.");})
      .finally(()=>{if(mounted)setLoading(false);});
    return ()=>{mounted=false;};
  },[publicCode]);
  return <div className="customer-entry"><div className="customer-entry-glow"/><header className="customer-entry-brand"><div className="brand-mark"><QrCode size={21}/></div><div><div className="brand-name">smart<span>print</span></div><div className="brand-caption">BY QRtoPrint</div></div></header>
    <main className="customer-entry-card">{loading?<><div className="customer-entry-spinner"><span className="button-spinner"/></div><h1>Connecting to your print shop</h1><p>Creating a secure, time-limited session for this QR station.</p></>:error?<><div className="entry-error-icon"><X size={23}/></div><div className="entry-eyebrow">STATION UNAVAILABLE</div><h1>This QR station can't be reached</h1><p>{error}</p><button className="primary-button" onClick={()=>window.location.reload()}>Try again</button></>:result?<><div className="entry-success-icon"><Check size={23}/></div><div className="entry-eyebrow">SECURE SESSION CREATED</div><h1>Welcome to {result.shop.displayName}</h1><p>Your phone is connected to this shop's print station.</p><div className="entry-printer-card"><div className="entry-printer-icon"><Printer size={20}/></div><div><strong>{result.printer?.displayName||"Shop print counter"}</strong><span>{result.printer?result.printer.manufacturer+" · "+result.printer.model:"The shop will select a printer for your job."}</span></div><Status label={result.printer?.status||"Session ready"} tone="blue"/></div><div className="entry-session-note"><ShieldCheck size={16}/><span>This session expires at {new Date(result.expiresUtc).toLocaleTimeString([], {hour:"2-digit",minute:"2-digit"})}. Your document access stays limited to this shop session.</span></div><div className="entry-next-step"><strong>You're connected.</strong><span>Document upload and print configuration are the next customer workflow being integrated.</span></div></>:null}</main>
    <footer className="customer-entry-footer">© 2026 Smart Print · Secure customer printing</footer>
  </div>;
}
