import { ArrowRight, Check, FileText, Printer, QrCode, Settings, Wallet } from "lucide-react";
import { type ShopPaymentSettings, type AgentDevice, type PrinterDevice, type QrStation, type ShopPricingProfile, type ShopPrintJob, type ShopProfile } from "../../api";
import Status from "../../shared/ui/Status";
import MetricCard from "../../shared/ui/MetricCard";
import { prettyStatus } from "../../shared/lib/formatting";
import { isPrinterAvailable } from "../../shared/lib/printers";
import { istDateKey } from "../../date-time";
import type { ShopSection } from "./shop-types";

export default function OverviewPage({ profile, printers, stations, printJobs, pricing, agentDevices, paymentSettings, onNavigate }: {
  profile: ShopProfile | null; printers: PrinterDevice[]; stations: QrStation[];
  printJobs: ShopPrintJob[]; pricing: ShopPricingProfile | null;
  agentDevices: AgentDevice[]; paymentSettings: ShopPaymentSettings | null;
  onNavigate: (section: ShopSection) => void;
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
  const setupChecks: { complete: boolean; title: string; detail: string; target: ShopSection }[] = [
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

function SetupCheck({complete,title,detail,onClick}:{complete:boolean;title:string;detail:string;onClick:()=>void}) {
  return <button className="setup-check" onClick={onClick}><span className={complete?"check-circle complete":"check-circle"}>{complete?<Check size={14}/>:null}</span><span className="setup-check-copy"><strong>{title}</strong><small>{detail}</small></span><ArrowRight size={16}/></button>;
}
function Device({name,detail,state,tone}:{name:string;detail:string;state:string;tone:string}) {
  return <div className="device-row"><div className="device-glyph"><Printer size={16}/></div><div className="device-copy"><strong>{name}</strong><span>{detail}</span></div><Status label={state} tone={tone}/></div>;
}
