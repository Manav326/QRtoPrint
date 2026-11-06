import { useEffect, useRef } from "react";
import { Check, Image as ImageIcon, ShieldCheck, Wallet, X, RotateCcw } from "lucide-react";
import type { ShopPrintJob } from "../../../api";
import { isDesktopApp, setDesktopContentProtection } from "../../../desktop-bridge";
import Modal from "../../../shared/ui/Modal";
import { formatMoney } from "../../../shared/lib/formatting";

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

