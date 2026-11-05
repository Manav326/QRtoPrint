import { useEffect, useRef, useState } from "react";
import { ArrowRight, Check, FileText, FolderOpen, Monitor, Printer, QrCode, Image as ImageIcon, ShieldCheck, Wallet, X, RotateCcw } from "lucide-react";
import { previewShopPrintRefund, type PrinterDevice, type ShopJobEvent, type ShopPrintJob, type ShopProfile } from "../../api";
import { isDesktopApp, setDesktopContentProtection } from "../../desktop-bridge";
import Status from "../../shared/ui/Status";
import Modal from "../../shared/ui/Modal";
import PrintKindTag from "../../shared/ui/PrintKindTag";
import { formatMoney, prettyStatus } from "../../shared/lib/formatting";
import InfoTooltip from "../../shared/ui/InfoTooltip";
import { formatIstDateTime } from "../../date-time";
import type { ShopSection } from "./shop-types";

export function BatchReviewModal({jobs,busy,decisions,retrying,onDecision,onClose,onPreview,onApply}:{
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

export function JobHistoryModal({job,events,onClose}:{job:ShopPrintJob;events:ShopJobEvent[];onClose:()=>void}) {
  return <Modal title={job.jobNumber} subtitle={`Audit history · ${job.document.originalFileName}`} onClose={onClose}>
    <div className="shop-history-list">{events.length===0?<p>No recorded events yet.</p>:events.map(event=><div key={event.id}><i/><span><strong>{prettyStatus(event.newStatus)}</strong><small>{event.actorType==="ShopUser"?"Shop team":event.actorType==="Customer"?"Customer":"System"} · {formatIstDateTime(event.createdUtc)}</small>{event.reasonCode&&<small>{event.reasonCode.replaceAll("_"," ").toLowerCase()}</small>}</span></div>)}</div>
    <div className="modal-actions"><button className="secondary-button" onClick={onClose}>Close history</button></div>
  </Modal>;
}

export function DocumentPreviewModal({name,url,mediaType,job,readOnly=false,isSavedOutput=false,focusEditOnMount=false,onClose,onApprove,onReject,onRefund,onEditPhoto}:{
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

export function RefundModal({job,token,busy,onClose,onSubmit}:{
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

export function ShopOwnerAccountModal({ profile, busy, onClose, onSave }: {
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

export function HelpSupportModal({ onClose, onNavigate }: { onClose: () => void; onNavigate: (section: ShopSection) => void }) {
  const topics: { section: ShopSection; title: string; description: string; icon: typeof FileText }[] = [
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

export function PrinterModal({ busy, onClose, onSubmit }: {
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

export function StationModal({ busy, printers, onClose, onSubmit }: {
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
