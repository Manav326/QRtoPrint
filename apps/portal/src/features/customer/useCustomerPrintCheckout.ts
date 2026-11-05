import { useEffect, useState } from "react";
import type { Dispatch, SetStateAction } from "react";
import {
  ApiError, acceptCustomerQuote, calculateCustomerQuote, getCustomerJobStatus,
  reportCustomerBatchPayment, reportCustomerUpiPayment, startCustomerBatchPayment, startCustomerPayment,
  type CustomerBatchPayment, type CustomerJobStatus, type CustomerPayment,
  type PrintQuoteResult, type UploadedPrintJob
} from "../../api";
import type { CustomerPhotoSettings, CustomerSavedPrintSettings } from "./customer-print-utils";

type Setter<T> = Dispatch<SetStateAction<T>>;
type CustomerPrintCheckoutOptions = {
  sessionToken: string;
  uploaded: UploadedPrintJob | null;
  uploadedBatch: UploadedPrintJob[];
  batchId: string;
  jobStatus: CustomerJobStatus | null;
  batchStatuses: Record<string, CustomerJobStatus>;
  quotesByJobId: Record<string, PrintQuoteResult>;
  setQuotesByJobId: Setter<Record<string, PrintQuoteResult>>;
  printSettingsByJobId: Record<string, CustomerSavedPrintSettings>;
  photoSettingsByJobId: Record<string, CustomerPhotoSettings>;
  quoteExpiredForBatchItem: (item: UploadedPrintJob) => boolean;
  statusForBatchItem: (item: UploadedPrintJob) => string;
  hasLegacyIndividualPayment: boolean;
  payment: CustomerPayment | null;
  batchPayment: CustomerBatchPayment | null;
  setBatchPayment: Setter<CustomerBatchPayment | null>;
  setQuote: Setter<PrintQuoteResult | null>;
  setJobStatus: Setter<CustomerJobStatus | null>;
  rememberBatchStatuses: (statuses: Record<string, CustomerJobStatus>) => void;
  rememberCustomerStatus: (status: CustomerJobStatus) => void;
  rememberCustomerPayment: (payment?: CustomerPayment | null) => void;
  rememberBatchPayment: (payment?: CustomerBatchPayment | null) => void;
  refreshBatchStatuses: () => Promise<void>;
  upiEnabled: boolean;
  cashEnabled: boolean;
  setBusy: Setter<boolean>;
  setError: Setter<string>;
};

export function useCustomerPrintCheckout(options: CustomerPrintCheckoutOptions) {
  const {
    sessionToken, uploaded, uploadedBatch, batchId, jobStatus, batchStatuses,
    quotesByJobId, setQuotesByJobId, printSettingsByJobId, photoSettingsByJobId,
    quoteExpiredForBatchItem, statusForBatchItem, hasLegacyIndividualPayment,
    payment, batchPayment, setBatchPayment, setQuote, setJobStatus,
    rememberBatchStatuses, rememberCustomerStatus, rememberCustomerPayment, rememberBatchPayment,
    refreshBatchStatuses, upiEnabled, cashEnabled, setBusy, setError
  } = options;
  const [paymentReference, setPaymentReference] = useState("");
  const [paymentMethod, setPaymentMethod] = useState<"UPI" | "CASH">(upiEnabled ? "UPI" : "CASH");

  useEffect(() => {
    if (!upiEnabled && cashEnabled) setPaymentMethod("CASH");
    else if (upiEnabled) setPaymentMethod("UPI");
  }, [upiEnabled, cashEnabled]);

  async function getQuote(itemsOverride?:UploadedPrintJob[],settingsOverride?:Record<string,CustomerSavedPrintSettings>,photoSettingsOverride?:Record<string,CustomerPhotoSettings>) {
    const items=itemsOverride??(uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]));
    if(items.length===0)return;
    setBusy(true);setError("");setJobStatus(null);
    const nextQuotes={...quotesByJobId};
    const failures:string[]=[];
    try {
      for(const item of items) {
        const existingQuote=nextQuotes[item.job.id];
        const status=uploaded?.job.id===item.job.id&&jobStatus?.status
          ?jobStatus.status
          :batchStatuses[item.job.id]?.status??item.job.status;
        const quoteExpired=quoteExpiredForBatchItem(item);
        if(!quoteExpired&&existingQuote&&existingQuote.status!=="QUOTE_EXPIRED")continue;
        if(quoteExpired
          ? !["QUOTE_EXPIRED","AWAITING_CUSTOMER_APPROVAL","AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT","PAYMENT_REJECTED"].includes(status)
          : !["AWAITING_CONFIGURATION","UPLOADED","Uploaded"].includes(status))continue;
        const requestedSettings=settingsOverride?.[item.job.id]??printSettingsByJobId[item.job.id];
        const settings:CustomerSavedPrintSettings={
          copies:requestedSettings?.copies??1,
          colourMode:requestedSettings?.colourMode??"Mono",
          isDuplex:requestedSettings?.isDuplex??false,
          paperSize:requestedSettings?.paperSize??"A4",
          orientation:requestedSettings?.orientation??"AUTO",
          advancedEnabled:requestedSettings?.advancedEnabled??false
        };
        const photoSettings=photoSettingsOverride?.[item.job.id]??photoSettingsByJobId[item.job.id];
        try {
          nextQuotes[item.job.id]=await calculateCustomerQuote(sessionToken,item.job.id,{
            colourMode:photoSettings?"Colour":settings.colourMode,
            paperSize:photoSettings?"A4":settings.paperSize,
            isDuplex:photoSettings?false:settings.isDuplex,
            copies:photoSettings?1:settings.copies,
            orientation:photoSettings?"AUTO":settings.orientation,
            ...(photoSettings?{printKind:"PHOTO" as const,photoSizeCode:photoSettings.sizeCode,photoCount:photoSettings.photoCount,photosPerPage:photoSettings.photosPerPage??photoSettings.photoCount}:{printKind:"DOCUMENT" as const})
          });
        } catch(exception) {
          failures.push(item.document.originalFileName);
        }
      }
      setQuotesByJobId(nextQuotes);
      const statusResults=await Promise.all(items.map(async item=>{
        try{return [item.job.id,await getCustomerJobStatus(sessionToken,item.job.id)] as const;}
        catch{return null;}
      }));
      const freshStatuses=statusResults.reduce<Record<string,CustomerJobStatus>>((map,item)=>{
        if(item)map[item[0]]=item[1];
        return map;
      },{});
      rememberBatchStatuses(freshStatuses);
      if(uploaded&&freshStatuses[uploaded.job.id]){
        const selected=freshStatuses[uploaded.job.id];
        rememberCustomerStatus(selected);
      }
      const selectedQuote=uploaded?nextQuotes[uploaded.job.id]??null:null;
      setQuote(selectedQuote??Object.values(nextQuotes)[0]??null);
      if(failures.length) setError(`Could not quote: ${failures.join(", ")}. Retry the batch quote.`);
      else if(!Object.keys(nextQuotes).length) setError("No files are waiting for a quote.");
    } catch(exception) {
      setError(exception instanceof ApiError?exception.message:"The shop could not calculate the batch quote.");
    } finally {setBusy(false);}
  }

  async function acceptQuote() {
    const items=uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]);
    const pending=items.filter(item=>
      !["REJECTED","REFUNDED"].includes(statusForBatchItem(item))&&
      quotesByJobId[item.job.id]?.status==="AWAITING_CUSTOMER_APPROVAL");
    if(pending.length===0)return;
    setBusy(true);setError("");
    const nextQuotes={...quotesByJobId};
    const failures:string[]=[];
    try {
      for(const item of pending) {
        try {
          const accepted=await acceptCustomerQuote(sessionToken,item.job.id);
          const current=nextQuotes[item.job.id];
          if(current)nextQuotes[item.job.id]={...current,status:accepted.status};
        } catch {
          const fresh=await getCustomerJobStatus(sessionToken,item.job.id).catch(()=>null);
          if(fresh&&fresh.status!=="AWAITING_CUSTOMER_APPROVAL") {
            rememberBatchStatuses({[item.job.id]:fresh});
          } else failures.push(item.document.originalFileName);
        }
      }
      setQuotesByJobId(nextQuotes);
      if(uploaded) {
        const refreshed=await getCustomerJobStatus(sessionToken,uploaded.job.id);
        rememberCustomerStatus(refreshed);
      }
      const refreshedBatch=await Promise.all(items.map(async item=>{
        try{return [item.job.id,await getCustomerJobStatus(sessionToken,item.job.id)] as const;}
        catch{return null;}
      }));
      rememberBatchStatuses(Object.fromEntries(refreshedBatch.filter((entry):entry is NonNullable<typeof entry>=>entry!==null)));
      setQuote(uploaded?nextQuotes[uploaded.job.id]??null:null);
      if(failures.length)setError(`Could not send: ${failures.join(", ")}. Refresh and retry.`);
    } catch(exception) {
      setError(exception instanceof ApiError?exception.message:"The batch could not be sent to the shop.");
    } finally {setBusy(false);}
  }

  async function beginPayment(method:"UPI"|"CASH",forceBatchCheckout=false) {
    if(!uploaded)return;
    setBusy(true);setError("");
    try {
      if(batchId&&(forceBatchCheckout||!hasLegacyIndividualPayment)){
        const attempt=await startCustomerBatchPayment(sessionToken,batchId,method);
        setBatchPayment(attempt);
        setPaymentMethod(method);
        await refreshBatchStatuses();
      } else {
        const attempt=await startCustomerPayment(sessionToken,uploaded.job.id,method);
        rememberCustomerPayment(attempt);
        setPaymentMethod(method);
        const refreshed=await getCustomerJobStatus(sessionToken,uploaded.job.id);
        rememberCustomerStatus(refreshed);
      }
    } catch(exception) {
      if(batchId)await refreshBatchStatuses();
      setError(exception instanceof ApiError?exception.message:"Could not start this payment method.");
    } finally {setBusy(false);}
  }

  async function submitPaymentReference() {
    setBusy(true);setError("");
    try {
      if(batchPayment&&batchPayment.status==="AWAITING_CUSTOMER_PAYMENT"){
        await reportCustomerBatchPayment(sessionToken,batchPayment.id,paymentReference.trim());
        setPaymentReference("");
        await refreshBatchStatuses();
      } else {
        if(!payment||!uploaded)return;
        await reportCustomerUpiPayment(sessionToken,payment.id,paymentReference.trim());
        setPaymentReference("");
        const refreshed=await getCustomerJobStatus(sessionToken,uploaded.job.id);
        rememberCustomerStatus(refreshed);
        rememberBatchPayment(refreshed.payment?.batchPayment);
      }
    } catch(exception) {
      setError(exception instanceof ApiError?exception.message:"The payment reference could not be submitted.");
    } finally {setBusy(false);}
  }

  return {
    paymentReference, setPaymentReference, paymentMethod, setPaymentMethod,
    getQuote, acceptQuote, beginPayment, submitPaymentReference
  };
}
