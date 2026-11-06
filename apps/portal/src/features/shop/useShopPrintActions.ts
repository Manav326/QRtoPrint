import { useState } from "react";
import {
  applyShopPrintBatchReview, approveShopPrintJob, assignShopPrintJobPrinter, confirmShopPrintOutput,
  dispatchShopPrintJob, downloadShopDocument, getShopJobEvents, getShopPrintBatchJobs,
  recordShopPrintRefund, rejectShopPrintJob, replaceShopPhotoDocument, replaceShopPhotoDocumentLocally,
  retryShopPdfExport, updateShopPrintJobSettings,
  type CustomerPrintRequestSettings, type ShopJobEvent, type ShopPrintJob
} from "../../api";
import type { PreparedPhotoSheet } from "../../PhotoStudio";
import { formatMoney } from "../../shared/lib/formatting";

type PreviewState = {
  name:string;
  url:string;
  blob:Blob;
  mediaType:string;
  job:ShopPrintJob;
  readOnly:boolean;
  isSavedOutput?:boolean;
};
type ShopPhotoStudioState = {
  source:File;
  job:ShopPrintJob;
  returnPreview:{name:string;blob:Blob;mediaType:string;job:ShopPrintJob;readOnly:boolean};
};
type ShopPrintActionsOptions = {
  accessToken:string;
  reload:()=>Promise<void>;
  confirmInApp:(message:string,title?:string,confirmLabel?:string)=>Promise<boolean>;
  setError:(value:string)=>void;
  setToast:(value:string)=>void;
};

export function useShopPrintActions({accessToken,reload,confirmInApp,setError,setToast}:ShopPrintActionsOptions) {
  const [busyJobId,setBusyJobId]=useState<string|null>(null);
  const [preview,setPreview]=useState<PreviewState|null>(null);
  const [focusShopPhotoEdit,setFocusShopPhotoEdit]=useState(false);
  const [shopPhotoStudio,setShopPhotoStudio]=useState<ShopPhotoStudioState|null>(null);
  const [refundJob,setRefundJob]=useState<ShopPrintJob|null>(null);
  const [batchReviewJobs,setBatchReviewJobs]=useState<ShopPrintJob[]|null>(null);
  const [batchReviewDecisions,setBatchReviewDecisions]=useState<Record<string,boolean>>({});
  const [batchReviewRetry,setBatchReviewRetry]=useState(false);
  const [batchReviewRetryIds,setBatchReviewRetryIds]=useState<string[]>([]);
  const [batchReviewLoadingId,setBatchReviewLoadingId]=useState<string|null>(null);
  const [eventHistory,setEventHistory]=useState<{job:ShopPrintJob;events:ShopJobEvent[]}|null>(null);

  async function assignPrinter(job: ShopPrintJob, printerDeviceId: string) {
    if (!printerDeviceId || printerDeviceId === job.printerDeviceId) return;
    setBusyJobId(job.id);
    setError("");
    try {
      const assignment=await assignShopPrintJobPrinter(accessToken, job.id, printerDeviceId);
      setToast(`${assignment.printerName} assigned to ${assignment.assignedJobCount} file(s) in this batch. The paired computer can now receive the documents.`);
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not assign this printer.");
    } finally {
      setBusyJobId(null);
    }
  }


  async function updatePrintJobSettings(job:ShopPrintJob,settings:CustomerPrintRequestSettings) {
    setBusyJobId(job.id);
    setError("");
    try {
      const revised=await updateShopPrintJobSettings(accessToken,job.id,settings);
      setToast(`Revised quote ${formatMoney(revised.quotedAmountMinor,revised.currency)} sent to the customer for approval. Printing remains paused until approval and release.`);
      await reload();
    } catch(exception) {
      setError(exception instanceof Error?exception.message:"Could not revise this print request.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function dispatchPrintJob(job: ShopPrintJob) {
    const pdfExport = job.outputMode === "PDF_EXPORT";
    const ok = await confirmInApp(pdfExport
      ? `Save a PDF for ${job.jobNumber} in the shop's QRtoPrint Exports folder? No Windows printer dialog will be opened.`
      : `Start printing ${job.jobNumber} on ${job.printerName || "the assigned printer"}? The document will be submitted once to Windows. Spooler acceptance is not proof of paper output.`, pdfExport ? "Save PDF export?" : "Start printing?", pdfExport ? "Save PDF" : "Start printing");
    if (!ok) return;
    setBusyJobId(job.id); setError("");
    try {
      await dispatchShopPrintJob(accessToken, job.id);
      setToast(pdfExport
        ? "PDF export authorised. The Windows Agent will create the PDF in the shop's export folder."
        : "Print authorised. The Windows agent will submit this job once.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not start printing.");
    } finally { setBusyJobId(null); }
  }

  async function retryPdfExport(job: ShopPrintJob) {
    const ok = await confirmInApp(
      `Confirm that no PDF file was saved for ${job.jobNumber}? The Agent will create the PDF in the shop's QRtoPrint Exports folder. Do not continue if a file from this job already exists.`,
      "Retry PDF export?", "Retry export"
    );
    if (!ok) return;
    setBusyJobId(job.id);
    setError("");
    try {
      await retryShopPdfExport(accessToken, job.id);
      await dispatchShopPrintJob(accessToken, job.id);
      setToast("PDF export retry authorised. The Windows Agent will save the PDF locally.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not retry this PDF export.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function confirmPrintedOutput(job: ShopPrintJob) {
    const ok = await confirmInApp(`Have you physically checked all requested pages for ${job.jobNumber}? Only confirm after checking the actual paper output.`, "Confirm paper output", "Confirm output");
    if (!ok) return;
    setBusyJobId(job.id); setError("");
    try {
      await confirmShopPrintOutput(accessToken, job.id);
      setToast("Physical output confirmed by the shop operator.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not confirm physical output.");
    } finally { setBusyJobId(null); }
  }

  async function previewJob(
    job: ShopPrintJob,
    readOnly = false,
    variant: "original" | "output" = "original"
  ) {
    setBusyJobId(job.id);
    setError("");
    try {
      const blob = await downloadShopDocument(accessToken, job.id, variant);
      const isSavedOutput = variant === "output";
      const supportedTypes = new Set(["application/pdf", "image/jpeg", "image/png"]);
      const storedType=(job.document.mediaType||"").split(";")[0].trim().toLowerCase();
      const responseType = (blob.type || "").split(";")[0].trim().toLowerCase();
      const extension = job.document.originalFileName.toLowerCase().split(".").pop();
      const extensionType = extension === "pdf" ? "application/pdf" : extension === "jpg" || extension === "jpeg" ? "image/jpeg" : extension === "png" ? "image/png" : "";
      // Trust the persisted MIME type, then response headers, then the validated file extension.
      const mediaType = isSavedOutput ? "application/pdf" : supportedTypes.has(storedType) ? storedType : supportedTypes.has(responseType) ? responseType : extensionType;
      const previewBlob = mediaType && blob.type !== mediaType ? new Blob([blob], { type: mediaType }) : blob;
      const url = URL.createObjectURL(previewBlob);
      setPreview({
        name: isSavedOutput ? job.outputFileName || `${job.jobNumber}_SmartPrint.pdf` : job.document.originalFileName,
        url,
        blob: previewBlob,
        mediaType,
        job,
        readOnly: readOnly || isSavedOutput,
        isSavedOutput
      });
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not open the document.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function openShopPhotoStudioForJob(job:ShopPrintJob){
    setBusyJobId(job.id);setError("");
    try{
      const blob=await downloadShopDocument(accessToken,job.id,"original");
      const stored=(job.document.mediaType||"").split(";")[0].trim().toLowerCase();
      const response=(blob.type||"").split(";")[0].trim().toLowerCase();
      const extension=job.document.originalFileName.toLowerCase().split(".").pop();
      const mediaType=stored==="image/jpeg"||stored==="image/png"?stored:response==="image/jpeg"||response==="image/png"?response:extension==="png"?"image/png":extension==="jpg"||extension==="jpeg"?"image/jpeg":"";
      if(!mediaType)throw new Error("Photo Studio can open JPG and PNG originals. Preview this file first to confirm its format.");
      const sourceBlob=blob.type===mediaType?blob:new Blob([blob],{type:mediaType});
      const source=new File([sourceBlob],job.document.originalFileName,{type:mediaType,lastModified:Date.now()});
      const returnPreview={name:job.document.originalFileName,blob:sourceBlob,mediaType,job,readOnly:false};
      setFocusShopPhotoEdit(false);
      setShopPhotoStudio({source,job,returnPreview});
    }catch(exception){setError(exception instanceof Error?exception.message:"Could not open Photo Studio for this file.");}
    finally{setBusyJobId(null);}
  }

  function openShopPhotoStudio(){
    if(!preview||preview.readOnly||preview.mediaType!=="image/jpeg"&&preview.mediaType!=="image/png")return;
    const source=new File([preview.blob],preview.name,{type:preview.mediaType,lastModified:Date.now()});
    const returnPreview={name:preview.name,blob:preview.blob,mediaType:preview.mediaType,job:preview.job,readOnly:preview.readOnly};
    URL.revokeObjectURL(preview.url);
    setPreview(null);
    setFocusShopPhotoEdit(false);
    setShopPhotoStudio({source,job:preview.job,returnPreview});
  }

  function cancelShopPhotoStudio(){
    if(!shopPhotoStudio){setShopPhotoStudio(null);return;}
    const restored=shopPhotoStudio.returnPreview;
    setPreview({...restored,url:URL.createObjectURL(restored.blob)});
    setFocusShopPhotoEdit(true);
    setShopPhotoStudio(null);
  }

  async function applyShopPhotoStudio(sheets:PreparedPhotoSheet[]){
    const editor=shopPhotoStudio;
    if(!editor||sheets.length!==1)throw new Error("Prepare one multi-page A4 photo PDF before saving this job.");
    const sheet=sheets[0];
    setBusyJobId(editor.job.id);
    try{
      if(editor.job.storageMode==="LOCAL_AGENT") {
        await replaceShopPhotoDocumentLocally(accessToken,editor.job.id,sheet.file,sheet.sizeCode,sheet.photoCount,sheet.photosPerPage);
      } else {
        await replaceShopPhotoDocument(accessToken,editor.job.id,sheet.file,sheet.sizeCode,sheet.photoCount,sheet.photosPerPage);
      }
    }catch(exception){
      setError(exception instanceof Error?exception.message:"Could not save the enhanced photo sheet.");
      throw exception;
    }

    setShopPhotoStudio(null);
    setFocusShopPhotoEdit(false);
    setToast("The enhanced multi-page A4 photo PDF is saved to the same print job and its photo price has been recalculated.");
    try{
      await reload();
      const blob=await downloadShopDocument(accessToken,editor.job.id,"original");
      const previewBlob=blob.type==="application/pdf"?blob:new Blob([blob],{type:"application/pdf"});
      const savedJob:ShopPrintJob={...editor.job,printKind:"PHOTO",photoSizeCode:sheet.sizeCode,photoCount:sheet.photoCount,pageCount:Math.max(1,Math.ceil(sheet.photoCount/sheet.photosPerPage)),document:{...editor.job.document,originalFileName:sheet.file.name,mediaType:"application/pdf",sizeBytes:sheet.file.size}};
      setPreview({name:sheet.file.name,url:URL.createObjectURL(previewBlob),blob:previewBlob,mediaType:"application/pdf",job:savedJob,readOnly:true});
    }catch{
      setError("The A4 photo PDF was saved to the existing print job, but the queue could not refresh. Refresh the queue before approving or printing.");
    }finally{
      setBusyJobId(null);
    }
  }

  async function viewJobHistory(job: ShopPrintJob) {
    setBusyJobId(job.id);
    setError("");
    try {
      const events = await getShopJobEvents(accessToken, job.id);
      setEventHistory({ job, events });
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not load job history.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function approveJob(job: ShopPrintJob) {
    if (!(await confirmInApp(`Approve ${job.jobNumber} for payment? It will not be sent to a printer yet.`, "Accept this file", "Accept file"))) return;
    setBusyJobId(job.id);
    setError("");
    try {
      const result = await approveShopPrintJob(accessToken, job.id);
      setToast(result.nextStep || "Shop approval saved. This job is awaiting verified payment.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not approve this job.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function rejectJob(job: ShopPrintJob) {
    if (!(await confirmInApp(`Reject ${job.jobNumber}? The customer will see that the shop declined the request.`, "Reject this file?", "Reject file"))) return;
    setBusyJobId(job.id);
    setError("");
    try {
      await rejectShopPrintJob(accessToken, job.id);
      setToast("The print request was rejected.");
      await reload();
      if (preview?.job.id === job.id) {
        URL.revokeObjectURL(preview.url);
        setPreview(null);
      }
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not reject this job.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function applyBatchReview(batchJobs: ShopPrintJob[], acceptedJobIds: string[], pdfBrandingEnabled: boolean) {
    const batchId=batchJobs.find(job=>job.batchId)?.batchId;
    if(!batchId||batchJobs.length===0)return;
    const accepted=new Set(acceptedJobIds);
    const acceptCount=batchJobs.filter(job=>accepted.has(job.id)).length;
    const rejectCount=batchJobs.length-acceptCount;
    if (!(await confirmInApp(
      `Accept ${acceptCount} file(s) and reject ${rejectCount} file(s)? Previously rejected files you tick will be restored only if no payment has started. Expired quotes must be recalculated before checkout. PDF footer branding will be ${pdfBrandingEnabled ? "enabled on safe blank margins" : "off"} for this batch. Original uploads and normal paper prints remain unchanged. The decisions are applied together.`,
      "Confirm batch review",
      "Apply review"
    ))) return;

    setBusyJobId("batch-review");
    setError("");
    try {
      const result=await applyShopPrintBatchReview(
        accessToken,
        batchId,
        batchJobs.map(job=>job.id),
        acceptedJobIds,
        pdfBrandingEnabled
      );
      await reload();
      setBatchReviewJobs(null);
      setBatchReviewRetry(false);
      setBatchReviewRetryIds(current=>current.filter(id=>!batchJobs.some(job=>job.id===id)));
      setBatchReviewDecisions(current=>{
        const next={...current};
        for(const job of batchJobs)delete next[job.id];
        return next;
      });
      setToast(result.quoteRefreshRequiredCount>0
        ?`Batch review saved: ${acceptCount} accepted and ${rejectCount} rejected. ${result.quoteRefreshRequiredCount} accepted file(s) need a fresh quote before checkout.`
        :`Batch review saved: ${acceptCount} accepted and ${rejectCount} rejected. All accepted files remain part of one checkout.`);
    } catch(exception) {
      await reload().catch(()=>undefined);
      setError(exception instanceof Error?exception.message:"Could not apply this batch review. The decisions were not changed; refresh and retry the same review.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function recordFileRefund(job: ShopPrintJob, values: { refundMethod: "UPI" | "CASH"; refundReference: string | null; reason: string; rejectedPageNumbers: number[]; idempotencyKey: string }) {
    setBusyJobId(job.id);
    setError("");
    try {
      const result = await recordShopPrintRefund(accessToken, job.id, {
        ...values, confirmedSent: true
      });
      setToast(`Refund of ${formatMoney(result.amountMinor, result.currency)} recorded for ${job.document.originalFileName}. The return was logged under the signed-in shopkeeper.`);
      setRefundJob(null);
      if (preview?.job.id === job.id) {
        URL.revokeObjectURL(preview.url);
        setPreview(null);
      }
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not record this refund.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function openBatchReview(batchId: string) {
    setBatchReviewLoadingId(batchId);
    setError("");
    try {
      // Always load the complete batch from the tenant-scoped API. The queue
      // may currently be filtered or paginated, so its visible rows are not
      // a safe source for per-file acceptance/rejection decisions.
      const allBatchJobs = await getShopPrintBatchJobs(accessToken, batchId);
      const reviewableStatuses=new Set(["AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT","PAYMENT_REJECTED","REJECTED"]);
      const waiting = allBatchJobs.filter(job => reviewableStatuses.has(job.status));
      if (waiting.length === 0) {
        setBatchReviewJobs(null);
        setToast("This batch has no files eligible for pre-checkout review.");
        return;
      }
      setBatchReviewDecisions(current => {
        const next = { ...current };
        for (const job of waiting) {
          if (next[job.id] === undefined) next[job.id] = job.status !== "REJECTED";
        }
        return next;
      });
      setBatchReviewRetry(waiting.some(job => batchReviewRetryIds.includes(job.id)));
      setBatchReviewJobs(waiting);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not load every file in this batch. No review decisions were changed.");
    } finally {
      setBatchReviewLoadingId(current => current === batchId ? null : current);
    }
  }
  return {
    busyJobId,setBusyJobId,preview,setPreview,focusShopPhotoEdit,setFocusShopPhotoEdit,shopPhotoStudio,
    refundJob,setRefundJob,batchReviewJobs,setBatchReviewJobs,batchReviewDecisions,setBatchReviewDecisions,
    batchReviewRetry,batchReviewLoadingId,eventHistory,setEventHistory,
    assignPrinter,updatePrintJobSettings,dispatchPrintJob,retryPdfExport,confirmPrintedOutput,previewJob,
    openShopPhotoStudio,openShopPhotoStudioForJob,cancelShopPhotoStudio,applyShopPhotoStudio,viewJobHistory,approveJob,rejectJob,
    applyBatchReview,recordFileRefund,openBatchReview
  };
}
