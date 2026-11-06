import { useEffect, useState } from "react";
import { Copy, Plus, QrCode } from "lucide-react";
import QRCode from "react-qr-code";
import type { QrStation, ShopProfile } from "../../../api";
import QrStationPrintDocument from "../../../QrStationPrintDocument";
import EmptyModule from "../../../shared/ui/EmptyModule";
import Status from "../../../shared/ui/Status";

interface StationsPageProps {
  stations: QrStation[];
  profile: ShopProfile | null;
  busyResourceId: string | null;
  onAdd: () => void;
  onToggle: (station: QrStation) => void;
  onGenerate: (station: QrStation) => void;
  onCopyLink: (value: string) => void;
}

export default function StationsPage({ stations, profile, busyResourceId, onAdd, onToggle, onGenerate, onCopyLink }: StationsPageProps) {
  const [printingStationId,setPrintingStationId]=useState<string|null>(null);
  useEffect(()=>{
    if(!printingStationId)return;
    let cancelled=false;
    const onAfterPrint=()=>setPrintingStationId(null);
    const printSelectedStation=async()=>{
      const images=Array.from(document.querySelectorAll<HTMLImageElement>(".qr-print-document img"));
      await Promise.all(images.map(async image=>{
        try {
          if(!image.complete) await image.decode();
        } catch {
          // Shop name and the text wordmark remain available if a brand image fails.
        }
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
    <>
    <section className="panel collection-panel">
      <div className="collection-header"><div><h2>Customer QR stations</h2><p>Each station has a permanent public link. Every scan creates a separate short-lived customer session.</p></div><button className="primary-button" onClick={onAdd}><Plus size={16}/> Create station</button></div>
      {stations.length===0 ? <EmptyModule icon={<QrCode size={24}/>} title="Your first QR station starts here" text="Create a station for a counter or printer. The QR code identifies the station; every scan gets a separate expiring customer session." actionLabel="Create QR station" onAction={onAdd}/> :
      <div className="station-grid">{stations.map(station=><article id={`qr-station-${station.id}`} className="station-card" key={station.id}><div className="station-card-head"><div className="station-icon"><QrCode size={18}/></div><div className="station-head-copy"><strong>{station.displayName}</strong><span>{station.isEnabled?"Accepting new sessions":"Disabled"}</span></div><Status label={station.isEnabled?"Enabled":"Disabled"} tone={station.isEnabled?"green":"slate"}/></div>
        <div className="station-qr-area">{station.publicUrl ? <div className="qr-paper">
          <div className="qr-shop-brand">{profile?.logoUrl?<img src={profile.logoUrl} alt={profile.displayName+" logo"}/>:<img src="/smartprint-mark.svg" alt="Smart Print"/>}<div><strong>{profile?.displayName||station.displayName}</strong><small>DOCUMENT PRINTING STATION</small></div></div>
          <div className="qr-code-frame"><QRCode value={station.publicUrl} size={172} level="H" fgColor="#101c35" bgColor="#ffffff"/></div>
          <span className="qr-scan-label">SCAN TO PRINT</span>
          <span className="qr-scan-hint">Scan to upload documents and send them to this shop</span>
          <div className="qr-shop-station">{station.displayName}</div>
          <div className="qr-powered"><small>POWERED BY</small><div className="qr-powered-lockup" aria-label="QuantaEdge, Smarter Decisions. Greater Growth."><img className="qr-quantaedge-mark" src="/branding/quantaedge-mark.png" alt="" onError={event=>{event.currentTarget.hidden=true;const fallback=event.currentTarget.nextElementSibling;if(fallback instanceof HTMLElement)fallback.hidden=false;}}/><span className="qr-quantaedge-mark-fallback" hidden aria-hidden="true">Q</span><div className="qr-powered-wordmark"><strong>Quanta<span>Edge</span></strong><small>Smarter Decisions. Greater Growth.</small></div></div></div>
        </div> : <div className="qr-missing"><QrCode size={34}/><strong>QR code not stored</strong><span>This older station needs a one-time QR replacement. Its existing printed code cannot be recovered from the old hash.</span><button className="secondary-button" disabled={!!busyResourceId} onClick={()=>onGenerate(station)}>{busyResourceId===station.id?<span className="button-spinner"/>:"Reissue permanent QR"}</button></div>}</div>
        <div className="station-foot"><span>Code ending ···{station.publicCodeHint}</span><button className="subtle-button" disabled={!!busyResourceId} onClick={()=>onToggle(station)}>{busyResourceId===station.id?<span className="button-spinner"/>:station.isEnabled?"Disable station":"Enable station"}</button></div>
        {station.publicUrl && <div className="station-actions"><button className="secondary-button" onClick={()=>onCopyLink(station.publicUrl!)}> <Copy size={14}/> Copy link</button><button className="secondary-button" onClick={()=>setPrintingStationId(station.id)}>Print / download QR poster</button></div>}
      </article>)}</div>}
    </section>
    <QrStationPrintDocument station={stations.find(station => station.id === printingStationId) ?? null} profile={profile}/>
    </>
  );
}
