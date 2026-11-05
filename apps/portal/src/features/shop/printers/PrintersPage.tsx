import { Plus, Printer } from "lucide-react";
import type { PrinterDevice } from "../../../api";
import EmptyModule from "../../../shared/ui/EmptyModule";
import Status from "../../../shared/ui/Status";

export interface PrintersPageProps {
  printers: PrinterDevice[];
  busyResourceId: string | null;
  onAdd: () => void;
  onToggle: (printer: PrinterDevice) => void;
}

export default function PrintersPage({ printers, busyResourceId, onAdd, onToggle }: PrintersPageProps) {
  return (
    <section className="panel collection-panel">
      <div className="collection-header"><div><h2>Your printers</h2><p>Each Windows queue stays separate. SmartPrint will never merge printers automatically, even when two queues share a port.</p></div><button className="primary-button" onClick={onAdd}><Plus size={16}/> Add printer</button></div>
      {printers.length===0 ? <EmptyModule icon={<Printer size={24}/>} title="No printers registered yet" text="Add your first printer. It will appear here as awaiting local-agent discovery until the Windows Print Agent reports its capabilities and live status." actionLabel="Add your first printer" onAction={onAdd}/> :
      <div className="printer-grid">{printers.map(printer=>{
        const possibleDuplicates=printer.outputMode!=="PDF_EXPORT"&&printer.agentDeviceId&&printer.connectionAddress
          ?printers.filter(other=>other.id!==printer.id&&other.outputMode!=="PDF_EXPORT"&&other.agentDeviceId===printer.agentDeviceId&&
            !!other.connectionAddress&&other.connectionAddress.trim().toLowerCase()===printer.connectionAddress!.trim().toLowerCase())
          :[];
        return <article className="printer-card" key={printer.id}>
          <div className="printer-card-top"><div className="printer-card-icon"><Printer size={20}/></div><Status label={printer.status} tone={printer.status.toLowerCase()==="ready"?"green":"slate"}/></div>
          <h3>{printer.outputMode==="PDF_EXPORT"?"PDF export · "+printer.displayName:printer.displayName}</h3>
          <p>{printer.outputMode==="PDF_EXPORT"?"Direct local PDF generation · no Windows Save As dialog":printer.manufacturer+" · "+printer.model}</p>
          <div className="printer-meta">
            <span>{printer.outputMode==="PDF_EXPORT"?"PDF export destination":printer.connectionType}{printer.isDefault?(printer.outputMode==="PDF_EXPORT"?" · Default output destination":" · Default Windows queue"):""}</span>
            <span>{printer.queueName || printer.connectionAddress || "Connection not verified"}</span>
          </div>
          {possibleDuplicates.length>0&&<div className="printer-duplicate-warning" role="note">
            <strong>Possible duplicate printer</strong>
            <span>{possibleDuplicates.map(item=>item.displayName).join(", ")} uses the same Windows port. SmartPrint keeps both queues separate; confirm whether they are alternate queues for one physical printer.</span>
          </div>}
          <div className="printer-card-footer"><span className={printer.isActive?"enabled-label":"disabled-label"}>{printer.isActive?"Enabled":"Disabled"}</span><button className="subtle-button" disabled={!!busyResourceId} onClick={()=>onToggle(printer)}>{busyResourceId===printer.id?<span className="button-spinner"/>:printer.isActive?"Disable":"Enable"}</button></div>
        </article>;
      })}</div>}
    </section>
  );
}
