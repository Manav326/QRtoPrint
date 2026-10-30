import { useMemo, useState, type ReactNode } from "react";
import {
  Activity, ArrowDownRight, ArrowUpRight, Bell, Check, ChevronDown,
  Clock3, FileText, Filter, LayoutDashboard, LifeBuoy, Menu, Moon,
  MoreHorizontal, Printer, QrCode, Search, Settings, ShieldCheck, Sun,
  Users, Wallet, X
} from "lucide-react";

type Section = "Overview" | "Print Queue" | "Printers" | "QR Stations" | "Customers" | "Reports" | "Settings";
type Workspace = "Shop" | "Customer" | "Platform";

const sections: { label: Section; icon: typeof LayoutDashboard }[] = [
  { label: "Overview", icon: LayoutDashboard },
  { label: "Print Queue", icon: FileText },
  { label: "Printers", icon: Printer },
  { label: "QR Stations", icon: QrCode },
  { label: "Customers", icon: Users },
  { label: "Reports", icon: Activity },
  { label: "Settings", icon: Settings }
];

const jobs = [
  { id: "SP-2048", customer: "Aarav Sharma", file: "Project_Report_Final.pdf", pages: "24 pages", mode: "Colour", amount: "₹186.00", status: "Printing", tone: "blue", time: "Just now" },
  { id: "SP-2047", customer: "Priya Kumari", file: "Assignment_Maths.pdf", pages: "12 pages", mode: "B&W", amount: "₹24.00", status: "Ready", tone: "green", time: "2 min ago" },
  { id: "SP-2046", customer: "Rohan Verma", file: "ID_Documents.pdf", pages: "6 pages", mode: "B&W", amount: "₹12.00", status: "Awaiting approval", tone: "amber", time: "5 min ago" },
  { id: "SP-2045", customer: "Ananya Singh", file: "Presentation.pdf", pages: "38 pages", mode: "Colour", amount: "₹304.00", status: "Completed", tone: "slate", time: "11 min ago" }
];

function App() {
  const [active, setActive] = useState<Section>("Overview");
  const [workspace, setWorkspace] = useState<Workspace>("Shop");
  const [dark, setDark] = useState(true);
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [search, setSearch] = useState("");
  const [showNotifications, setShowNotifications] = useState(false);

  const filteredJobs = useMemo(
    () => jobs.filter((job) => [job.id, job.customer, job.file, job.status].join(" ").toLowerCase().includes(search.toLowerCase())),
    [search]
  );

  const title = workspace === "Shop" ? "Good evening, Manav" : workspace === "Customer" ? "Your print workspace" : "Platform overview";
  const subtitle = workspace === "Shop"
    ? "Here’s what’s happening at your shop today."
    : workspace === "Customer"
      ? "A simple, secure way to get your documents printed."
      : "A clear view of your Smart Print network.";

  return (
    <div className={dark ? "app-shell dark" : "app-shell light"}>
      <aside className={sidebarOpen ? "sidebar sidebar-open" : "sidebar"}>
        <div className="brand-lockup">
          <div className="brand-mark"><QrCode size={21} strokeWidth={2.4} /></div>
          <div><div className="brand-name">smart<span>print</span></div><div className="brand-caption">BY QRtoPrint</div></div>
          <button className="icon-button sidebar-close" aria-label="Close navigation" onClick={() => setSidebarOpen(false)}><X size={18}/></button>
        </div>

        <div className="workspace-switcher">
          <div className="shop-avatar">S</div>
          <div className="workspace-copy"><strong>{workspace === "Shop" ? "Sujeet Prints" : workspace === "Customer" ? "Customer portal" : "Smart Print HQ"}</strong><span>{workspace === "Shop" ? "Patna, Bihar" : workspace === "Customer" ? "Guest session" : "Platform owner"}</span></div>
          <ChevronDown size={15} className="muted-icon"/>
        </div>

        <div className="nav-label">WORKSPACE</div>
        <nav className="primary-nav" aria-label="Main navigation">
          {sections.map(({ label, icon: Icon }) => (
            <button key={label} className={active === label ? "nav-item active" : "nav-item"} onClick={() => { setActive(label); setSidebarOpen(false); }}>
              <Icon size={18} strokeWidth={1.8}/><span>{label}</span>{label === "Print Queue" && <span className="nav-count">8</span>}
            </button>
          ))}
        </nav>

        <div className="sidebar-bottom">
          <div className="plan-card">
            <div className="plan-icon"><ShieldCheck size={17}/></div>
            <div><strong>Professional plan</strong><span>5 printer connections</span></div>
            <button className="tiny-arrow" aria-label="View plan" onClick={() => setActive("Settings")}>↗</button>
          </div>
          <button className="nav-item support-link" onClick={() => setActive("Settings")}><LifeBuoy size={18}/><span>Help & support</span></button>
          <div className="profile-row"><div className="profile-avatar">MG</div><div className="profile-copy"><strong>Manav Gupta</strong><span>Shop owner</span></div><MoreHorizontal size={19} className="muted-icon"/></div>
        </div>
      </aside>

      {sidebarOpen && <button className="mobile-scrim" aria-label="Close navigation overlay" onClick={() => setSidebarOpen(false)} />}

      <main className="main-panel">
        <header className="topbar">
          <div className="topbar-left">
            <button className="icon-button mobile-menu" aria-label="Open navigation" onClick={() => setSidebarOpen(true)}><Menu size={20}/></button>
            <div className="breadcrumb"><span>Workspace</span><span className="crumb-slash">/</span><strong>{active}</strong></div>
          </div>
          <div className="topbar-actions">
            <div className="connection-pill"><span className="live-dot"/><span>System operational</span></div>
            <button className="icon-button theme-button" aria-label={dark ? "Switch to light theme" : "Switch to dark theme"} onClick={() => setDark(!dark)}>{dark ? <Sun size={18}/> : <Moon size={18}/>}</button>
            <div className="notification-wrap">
              <button className="icon-button notification-button" aria-label="Notifications" onClick={() => setShowNotifications(!showNotifications)}><Bell size={18}/><i/></button>
              {showNotifications && <div className="notification-popover"><div className="popover-heading"><strong>Notifications</strong><button onClick={() => setShowNotifications(false)} aria-label="Close notifications"><X size={15}/></button></div><div className="notice"><span className="notice-dot green-dot"/><div><strong>Printer back online</strong><p>Canon imageRUNNER is ready.</p><small>8 minutes ago</small></div></div><div className="notice"><span className="notice-dot amber-dot"/><div><strong>Job needs approval</strong><p>SP-2046 is waiting in your queue.</p><small>5 minutes ago</small></div></div></div>}
            </div>
            <div className="topbar-divider"/>
            <button className="workspace-mode" onClick={() => setWorkspace(workspace === "Shop" ? "Customer" : workspace === "Customer" ? "Platform" : "Shop")}><span className="mode-indicator"/>{workspace} view <ChevronDown size={14}/></button>
          </div>
        </header>

        <div className="content-wrap">
          <div className="page-heading">
            <div><div className="eyebrow"><span className="eyebrow-line"/>{workspace === "Shop" ? "SHOP OVERVIEW" : workspace === "Customer" ? "CUSTOMER EXPERIENCE" : "PLATFORM CONTROL"}</div><h1>{active === "Overview" ? title : active}</h1><p>{active === "Overview" ? subtitle : pageDescription(active)}</p></div>
            <div className="heading-actions"><span className="date-chip"><Clock3 size={15}/> Friday, 30 October</span><button className="primary-button" onClick={() => setActive("Print Queue")}><FileText size={16}/> View print queue</button></div>
          </div>

          {active === "Overview" ? (
            <>
              <div className="stat-grid">
                <Metric icon={<FileText size={18}/>} label="Total print jobs" value="128" change="+12.8%" note="vs. yesterday" trend="up" accent="violet"/>
                <Metric icon={<Wallet size={18}/>} label="Today's revenue" value="₹8,460" change="+8.2%" note="vs. yesterday" trend="up" accent="mint"/>
                <Metric icon={<Printer size={18}/>} label="Active printers" value="4 / 5" change="1 needs attention" note="Canon IR offline" trend="down" accent="blue"/>
                <Metric icon={<Clock3 size={18}/>} label="Awaiting action" value="08" change="3 approvals" note="in the print queue" trend="neutral" accent="amber"/>
              </div>

              <div className="dashboard-grid">
                <section className="panel volume-panel">
                  <div className="panel-heading"><div><h2>Print activity</h2><p>Daily print volume across your shop</p></div><button className="select-button">This week <ChevronDown size={14}/></button></div>
                  <div className="chart-legend"><span><i className="legend-dot violet-dot-bg"/> Colour pages</span><span><i className="legend-dot grey-dot-bg"/> Black & white</span><strong>2,840 <small>pages</small></strong></div>
                  <div className="chart-area">
                    <div className="chart-y-labels"><span>800</span><span>600</span><span>400</span><span>200</span><span>0</span></div>
                    <div className="chart-plot"><div className="chart-gridlines"><i/><i/><i/><i/><i/></div><div className="bars">{[{d:"Mon",a:48,b:32},{d:"Tue",a:64,b:40},{d:"Wed",a:54,b:47},{d:"Thu",a:77,b:49},{d:"Fri",a:88,b:58},{d:"Sat",a:66,b:44},{d:"Sun",a:35,b:25}].map((v)=><div className="bar-group" key={v.d}><div className="bar-stack"><i className="bar-colour" style={{height:v.a+"%"}}/><i className="bar-bw" style={{height:v.b+"%"}}/></div><span>{v.d}</span></div>)}</div></div>
                  </div>
                </section>

                <section className="panel printer-panel">
                  <div className="panel-heading"><div><h2>Printer health</h2><p>Live device overview</p></div><button className="icon-button small-icon" aria-label="More printer options"><MoreHorizontal size={19}/></button></div>
                  <div className="printer-summary"><div className="printer-summary-icon"><Printer size={19}/></div><div><strong>4 printers online</strong><span>1 device needs attention</span></div><div className="health-score">80%</div></div>
                  <div className="device-list">
                    <Device name="HP LaserJet Pro MFP" detail="192.168.1.104 · Mono" state="Ready" tone="green"/>
                    <Device name="Epson EcoTank L6270" detail="192.168.1.108 · Colour" state="Printing" tone="blue"/>
                    <Device name="Ricoh MP 2014" detail="USB · Mono" state="Ready" tone="green"/>
                    <Device name="Canon imageRUNNER" detail="192.168.1.110 · Colour" state="Offline" tone="red"/>
                  </div>
                  <button className="text-action" onClick={() => setActive("Printers")}>Manage printers <span>→</span></button>
                </section>
              </div>

              <section className="panel queue-panel">
                <div className="panel-heading queue-heading"><div><h2>Recent print jobs</h2><p>Keep track of what is moving through your shop</p></div><div className="queue-tools"><label className="search-field"><Search size={16}/><input aria-label="Search print jobs" placeholder="Search jobs..." value={search} onChange={(e)=>setSearch(e.target.value)}/><kbd>⌘ K</kbd></label><button className="select-button filter-button"><Filter size={15}/> Filter</button><button className="text-action desktop-only" onClick={()=>setActive("Print Queue")}>View all <span>→</span></button></div></div>
                <div className="table-scroll"><table><thead><tr><th>JOB ID</th><th>CUSTOMER / FILE</th><th>PRINT DETAILS</th><th>AMOUNT</th><th>STATUS</th><th>UPDATED</th><th/></tr></thead><tbody>{filteredJobs.map((job)=><tr key={job.id}><td><strong className="job-id">{job.id}</strong></td><td><div className="customer-cell"><strong>{job.customer}</strong><span>{job.file}</span></div></td><td><div className="detail-cell"><strong>{job.pages}</strong><span>{job.mode}</span></div></td><td className="amount-cell">{job.amount}</td><td><Status label={job.status} tone={job.tone}/></td><td className="time-cell">{job.time}</td><td><button className="icon-button row-more" aria-label={"More options for "+job.id}><MoreHorizontal size={17}/></button></td></tr>)}</tbody></table>{filteredJobs.length===0 && <div className="empty-search">No jobs match “{search}”. Try another name or job ID.</div>}</div>
                <div className="table-footer"><span>Showing <strong>{filteredJobs.length}</strong> of 128 jobs</span><button onClick={()=>setActive("Print Queue")}>Open full queue <span>→</span></button></div>
              </section>
              <div className="footnote"><span className="footnote-mark"><Check size={12}/></span> Your local print agent is connected <span className="footnote-separator">·</span> Last sync 18 seconds ago <span className="footnote-right">Sample workspace data</span></div>
            </>
          ) : (
            <section className="panel section-placeholder">
              <div className="placeholder-icon">{active === "Print Queue" ? <FileText/> : active === "Printers" ? <Printer/> : active === "QR Stations" ? <QrCode/> : active === "Customers" ? <Users/> : active === "Reports" ? <Activity/> : <Settings/>}</div>
              <h2>{active}</h2><p>{pageDescription(active)}</p>
              <div className="placeholder-note"><ShieldCheck size={16}/> The shared application shell and design system are in place. This module will be connected to the tenant-aware API as its workflow is implemented.</div>
              <button className="secondary-button" onClick={()=>setActive("Overview")}>Return to overview</button>
            </section>
          )}
          <footer className="app-footer"><span>© 2026 Smart Print by QRtoPrint</span><span><span className="footer-status"/> All systems normal</span><span>Made for print shops, with care.</span></footer>
        </div>
      </main>
    </div>
  );
}

function Metric({icon,label,value,change,note,trend,accent}:{icon:ReactNode;label:string;value:string;change:string;note:string;trend:"up"|"down"|"neutral";accent:string}) {
  return <section className="metric-card"><div className="metric-top"><div className={"metric-icon "+accent}>{icon}</div><button className="icon-button metric-more" aria-label={"More about "+label}><MoreHorizontal size={17}/></button></div><div className="metric-label">{label}</div><div className="metric-value">{value}</div><div className="metric-foot"><span className={"metric-change "+trend}>{trend==="up"?<ArrowUpRight size={13}/>:trend==="down"?<ArrowDownRight size={13}/>:<Activity size={13}/>} {change}</span><span>{note}</span></div></section>;
}
function Device({name,detail,state,tone}:{name:string;detail:string;state:string;tone:string}) {
  return <div className="device-row"><div className="device-glyph"><Printer size={16}/></div><div className="device-copy"><strong>{name}</strong><span>{detail}</span></div><Status label={state} tone={tone}/></div>;
}
function Status({label,tone}:{label:string;tone:string}) { return <span className={"status-badge "+tone}><i/>{label}</span>; }
function pageDescription(section:Section) {
  const descriptions:Record<Section,string> = {
    "Overview":"A live view of your shop's printing activity.",
    "Print Queue":"Review, approve and manage every print job.",
    "Printers":"Manage connected devices, capabilities and health.",
    "QR Stations":"Create and manage secure customer entry points.",
    "Customers":"View customer activity and print history.",
    "Reports":"Understand print volume, revenue and shop performance.",
    "Settings":"Configure your shop, team, pricing and preferences."
  };
  return descriptions[section];
}

export default App;
