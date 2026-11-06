import { useState } from "react";
import { FolderOpen } from "lucide-react";
import type { ShopPrintJob } from "../../../api";
import Status from "../../../shared/ui/Status";
import Modal from "../../../shared/ui/Modal";
import PrintKindTag from "../../../shared/ui/PrintKindTag";
import { formatMoney } from "../../../shared/lib/formatting";
import InfoTooltip from "../../../shared/ui/InfoTooltip";

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

