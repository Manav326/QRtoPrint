import { useEffect, useRef, useState } from "react";
import { previewShopPrintRefund, type ShopPrintJob } from "../../../api";
import Modal from "../../../shared/ui/Modal";
import { formatMoney } from "../../../shared/lib/formatting";

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


function formatBytes(bytes:number) {
  return bytes<1024*1024?`${(bytes/1024).toFixed(0)} KB`:`${(bytes/(1024*1024)).toFixed(2)} MB`;
}
