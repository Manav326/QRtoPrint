import { useEffect, useState } from "react";
import type { Dispatch, SetStateAction } from "react";
import {
  ApiError, getCustomerJobStatus,
  type CustomerBatchPayment, type CustomerJobStatus, type CustomerPayment,
  type PrintQuoteResult, type UploadedPrintJob, type CustomerPrintRequestSettings
} from "../../api";
import type { CustomerSavedPrintSettings } from "./customer-print-utils";

type CustomerPrintStatusOptions = {
  sessionToken: string;
  uploaded: UploadedPrintJob | null;
  uploadedBatch: UploadedPrintJob[];
  setPrintSettingsByJobId: Dispatch<SetStateAction<Record<string, CustomerSavedPrintSettings>>>;
  setQuotesByJobId: Dispatch<SetStateAction<Record<string, PrintQuoteResult>>>;
  setQuote: Dispatch<SetStateAction<PrintQuoteResult | null>>;
  setBusy: Dispatch<SetStateAction<boolean>>;
  setError: Dispatch<SetStateAction<string>>;
};

export function useCustomerPrintStatus({
  sessionToken, uploaded, uploadedBatch, setPrintSettingsByJobId, setQuotesByJobId,
  setQuote, setBusy, setError
}: CustomerPrintStatusOptions) {
const [jobStatus,setJobStatus]=useState<CustomerJobStatus|null>(null);
const [batchStatuses,setBatchStatuses]=useState<Record<string,CustomerJobStatus>>({});
const [payment,setPayment]=useState<CustomerPayment|null>(null);
const [batchPayment,setBatchPayment]=useState<CustomerBatchPayment|null>(null);

function paymentTimestamp(payment: { updatedUtc?: string | null; createdUtc?: string | null }): number {
  const timestamp=Date.parse(payment.updatedUtc??payment.createdUtc??"");
  return Number.isFinite(timestamp)?timestamp:0;
}

function rememberBatchPayment(candidate?:CustomerBatchPayment|null) {
  if(!candidate)return;
  setBatchPayment(current=>
    !current||paymentTimestamp(candidate)>=paymentTimestamp(current)
      ?candidate:current);
}

function rememberCustomerPayment(candidate?:CustomerPayment|null) {
  if(!candidate)return;
  setPayment(current=>
    !current||current.id!==candidate.id||paymentTimestamp(candidate)>=paymentTimestamp(current)
      ?candidate:current);
}

function rememberBatchStatuses(candidates:Record<string,CustomerJobStatus>) {
  setBatchStatuses(current=>{
    const next={...current};
    for(const [id,candidate] of Object.entries(candidates)){
      const existing=current[id];
      if(!existing||Date.parse(candidate.updatedUtc)>=Date.parse(existing.updatedUtc))next[id]=candidate;
    }
    return next;
  });
}

function rememberCustomerStatus(candidate:CustomerJobStatus) {
  rememberBatchStatuses({[candidate.id]:candidate});
  setJobStatus(current=>
    !current||current.id!==candidate.id||Date.parse(candidate.updatedUtc)>=Date.parse(current.updatedUtc)
      ?candidate:current);
  if(candidate.pricingVersion&&candidate.quotedAmountMinor>0) {
    const paperSize=(["A4","A3","A5","LETTER","LEGAL"].includes(candidate.paperSize)
      ?candidate.paperSize:"A4") as CustomerPrintRequestSettings["paperSize"];
    const orientation=(["AUTO","PORTRAIT","LANDSCAPE"].includes(candidate.orientation??"AUTO")
      ?candidate.orientation??"AUTO":"AUTO") as CustomerPrintRequestSettings["orientation"];
    // A shop-side quote revision is authoritative. Keep the settings shown
    // to the customer in step with the revised amount before approval.
    setPrintSettingsByJobId(current=>{
      const previous=current[candidate.id];
      return {
        ...current,
        [candidate.id]:{
          ...previous,
          copies:candidate.copies,
          colourMode:candidate.colourMode==="Colour"?"Colour":"Mono",
          paperSize,
          isDuplex:candidate.isDuplex,
          orientation,
          advancedEnabled:Boolean(previous?.advancedEnabled)||paperSize!=="A4"||orientation!=="AUTO"
        }
      };
    });
    const revisedQuote:PrintQuoteResult={
      id:candidate.id,
      batchId:uploadedBatch.find(item=>item.job.id===candidate.id)?.job.batchId??uploaded?.job.batchId??null,
      jobNumber:candidate.jobNumber,
      status:candidate.status,
      pageCount:candidate.pageCount,
      copies:candidate.copies,
      colourMode:candidate.colourMode as "Mono"|"Colour",
      paperSize:candidate.paperSize as CustomerPrintRequestSettings["paperSize"],
      isDuplex:candidate.isDuplex,
      orientation:(candidate.orientation??"AUTO") as CustomerPrintRequestSettings["orientation"],
      printKind:(candidate.printKind??"DOCUMENT") as "DOCUMENT"|"PHOTO",
      photoSizeCode:candidate.photoSizeCode??null,
      photoCount:candidate.photoCount,
      photoUnitPriceMinor:candidate.photoUnitPriceMinorSnapshot,
      currency:candidate.currency,
      quotedAmountMinor:candidate.quotedAmountMinor,
      amountMajor:candidate.quotedAmountMinor/100,
      pricingVersion:candidate.pricingVersion,
      quoteExpiresUtc:candidate.quoteExpiresUtc??""
    };
    setQuotesByJobId(current=>({...current,[candidate.id]:revisedQuote}));
    setQuote(revisedQuote);
  }
  rememberCustomerPayment(candidate.payment);
  rememberBatchPayment(candidate.payment?.batchPayment);
}

const batchItemsForStatus=uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]);
const batchStatusKey=batchItemsForStatus.map(item=>item.job.id).join("|");

  useEffect(()=>{
    const items=uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]);
    if(items.length===0)return;
    let active=true;
    let refreshing=false;
    const refreshAll=async()=>{
      if(refreshing)return;
      refreshing=true;
      try{
        const results=await Promise.all(items.map(async item=>{
          try{return [item.job.id,await getCustomerJobStatus(sessionToken,item.job.id)] as const;}
          catch{return null;}
        }));
        if(!active)return;
        const fresh=results.reduce<Record<string,Awaited<ReturnType<typeof getCustomerJobStatus>>>>((map,item)=>{
          if(item)map[item[0]]=item[1];
          return map;
        },{});
        rememberBatchStatuses(fresh);
        const latestBatchPayment=results
          .map(item=>item?.[1].payment?.batchPayment??null)
          .filter((item):item is CustomerBatchPayment=>item!==null)
          .sort((left,right)=>paymentTimestamp(right)-paymentTimestamp(left))[0];
        rememberBatchPayment(latestBatchPayment);
        const selectedId=uploaded?.job.id;
        if(selectedId&&fresh[selectedId]){
          const selected=fresh[selectedId];
          if(selected.status==="Uploaded"||selected.status==="UPLOADED"||selected.status==="AWAITING_CONFIGURATION"){
            setJobStatus(null);
            setPayment(null);
          } else {
            rememberCustomerStatus(selected);
          }
        }
      } finally {refreshing=false;}
    };
    void refreshAll();
    const timer=window.setInterval(()=>void refreshAll(),30000);
    return ()=>{active=false;window.clearInterval(timer);};
  },[sessionToken,batchStatusKey,uploaded?.job.id]);

  useEffect(()=>{
    if(!uploaded)return;
    let active=true;
    void getCustomerJobStatus(sessionToken,uploaded.job.id).then(refreshed=>{
      if(!active)return;
      // A newly uploaded job starts in AWAITING_CONFIGURATION on the API.
      // Treat that state like the legacy Uploaded value so the customer can
      // choose print settings and request the first quote after a reload.
      if(refreshed.status==="Uploaded"||refreshed.status==="UPLOADED"||refreshed.status==="AWAITING_CONFIGURATION"){
        setJobStatus(null);
        setPayment(null);
        return;
      }
      rememberCustomerStatus(refreshed);
    }).catch(exception=>{
      if(active)setError(exception instanceof ApiError?exception.message:"Could not restore this print request.");
    });
    return ()=>{active=false;};
  },[sessionToken,uploaded?.job.id]);


  async function refreshJobStatus() {
    if(!uploaded)return;
    setBusy(true);setError("");
    try {
      const refreshed=await getCustomerJobStatus(sessionToken,uploaded.job.id);
      rememberCustomerStatus(refreshed);
    }
    catch(exception){setError(exception instanceof ApiError?exception.message:"Could not refresh job status.");}
    finally {setBusy(false);}
  }


  // Keep the customer informed as the shop reviews, verifies payment and prints.
  // Terminal states stop polling; transient network failures are retried silently.
  useEffect(()=>{
    if(!uploaded||!jobStatus)return;
    const liveStates=new Set([
      "AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT","AWAITING_COUNTER_PAYMENT",
      "AWAITING_CUSTOMER_PAYMENT","PAYMENT_REPORTED","PAYMENT_REJECTED",
      "PAID_AUTHORISED","LOCAL_QUEUE_CLAIMED","LOCAL_QUEUE_READY",
      "PRINT_AUTHORISED","PRINT_SUBMITTING","SPOOLER_ACCEPTED","PRINTING",
      "OUTPUT_UNCONFIRMED"
    ]);
    if(!liveStates.has(jobStatus.status))return;
    let active=true;
    const timer=window.setInterval(()=>{
      void getCustomerJobStatus(sessionToken,uploaded.job.id).then(refreshed=>{
        if(!active)return;
        rememberCustomerStatus(refreshed);
        rememberBatchPayment(refreshed.payment?.batchPayment);
      }).catch(()=>{/* Retry on the next interval without interrupting the flow. */});
    },8000);
    return ()=>{
      active=false;
      window.clearInterval(timer);
    };
  },[sessionToken,uploaded?.job.id,jobStatus?.status]);

  async function refreshBatchStatuses() {
    const items=uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]);
    const results=await Promise.all(items.map(async item=>{
      try{return [item.job.id,await getCustomerJobStatus(sessionToken,item.job.id)] as const;}
      catch{return null;}
    }));
    const fresh=results.reduce<Record<string,CustomerJobStatus>>((map,item)=>{
      if(item)map[item[0]]=item[1];
      return map;
    },{});
    rememberBatchStatuses(fresh);
    const latestBatchPayment=results
      .map(item=>item?.[1].payment?.batchPayment??null)
      .filter((item):item is CustomerBatchPayment=>item!==null)
      .sort((left,right)=>paymentTimestamp(right)-paymentTimestamp(left))[0];
    rememberBatchPayment(latestBatchPayment);
    const selected=uploaded?fresh[uploaded.job.id]:null;
    if(selected){
      rememberCustomerStatus(selected);
    }
  }


  return {
    jobStatus, setJobStatus, batchStatuses, payment, setPayment, batchPayment, setBatchPayment,
    rememberBatchPayment, rememberCustomerPayment, rememberBatchStatuses, rememberCustomerStatus,
    refreshJobStatus, refreshBatchStatuses
  };
}
