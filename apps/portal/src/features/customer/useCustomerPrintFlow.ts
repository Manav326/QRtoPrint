import { useEffect, useState } from "react";
import {
  ApiError, LocalUploadOutcomeUnknownError, LocalUploadPreflightError, acceptCustomerQuote,
  calculateCustomerQuote, getCustomerJobStatus, reportCustomerBatchPayment, reportCustomerUpiPayment,
  startCustomerBatchPayment, startCustomerPayment, uploadCustomerDocument, uploadCustomerDocumentLocally,
  type CustomerBatchPayment, type CustomerPayment, type CustomerPhotoPricing, type CustomerJobStatus,
  type PrintQuoteResult, type UploadedPrintJob, type CustomerPrintRequestSettings
} from "../../api";
import { deriveCustomerPrintSummary } from "./customer-print-summary";
import { canFallbackToCloudFromLocalFailure, resolveCustomerUploadMode } from "../../local-upload-routing";
import {
  customerFileFingerprint, isCustomerPdf, smartPrintNativePdfPreviewTokens, postSmartPrintNativeMessage,
  customerBatchGroupName, normalizeCustomerCopies, readStoredCustomerDraft,
  type CustomerGroupSettings, type CustomerFileSettings, type CustomerSavedPrintSettings,
  type CustomerPhotoSettings, type StoredCustomerDraft, type CustomerPrintFlowProps
} from "./customer-print-utils";
import type { PreparedPhotoSheet } from "../../PhotoStudio";

export function useCustomerPrintFlow(props:CustomerPrintFlowProps) {
  const {dark,publicCode,sessionToken,paymentOptions,localAgent,localReady,localFallbackSuggested,cloudAssistedPrintingEnabled,photoPricing,hasAssignedPrinter,printerCapabilitiesJson} = props;
  const nativeAppMode = navigator.userAgent.includes("SmartPrintAndroid/1");
  // Customer requests are captured independently of current hardware reports.
  // The shop may wait, reassign, or request an approved change before dispatch.
  void hasAssignedPrinter;
  void printerCapabilitiesJson;
  const photoRatesAvailable=Boolean(photoPricing&&[photoPricing.photo35x45Minor,photoPricing.photo25x35Minor,photoPricing.photo2x2Minor,photoPricing.photo4x6Minor].some(rate=>Number.isFinite(rate)&&rate>0));
  const initialDraft=readStoredCustomerDraft(publicCode,sessionToken);
  const [files,setFiles]=useState<File[]>([]);
  const [selectedFileIndex,setSelectedFileIndex]=useState(0);
  const file=files[selectedFileIndex]??null;
  const [previewUrl,setPreviewUrl]=useState<string|null>(null);
  const [uploaded,setUploaded]=useState<UploadedPrintJob|null>(()=>initialDraft?.uploaded??initialDraft?.uploadedBatch?.[0]??null);
  const [uploadedBatch,setUploadedBatch]=useState<UploadedPrintJob[]>(()=>initialDraft?.uploadedBatch??(initialDraft?.uploaded?[initialDraft.uploaded]:[]));
  const [uploadedFileFingerprints,setUploadedFileFingerprints]=useState<string[]>(()=>initialDraft?.uploadedFileFingerprints??[]);
  const [batchId,setBatchId]=useState(()=>initialDraft?.batchId??"");
  const [batchName,setBatchName]=useState(()=>initialDraft?.batchName??initialDraft?.uploadedBatch?.[0]?.job?.batchName??initialDraft?.uploaded?.job?.batchName??"");
  const [fileGroupNames,setFileGroupNames]=useState<Record<string,string>>(()=>initialDraft?.fileGroupNames??{});
  const [groupSettings,setGroupSettings]=useState<Record<string,CustomerGroupSettings>>(()=>initialDraft?.groupSettings??{});
  const [fileSettings,setFileSettings]=useState<Record<string,CustomerFileSettings>>(()=>initialDraft?.fileSettings??{});
  const [printSettingsByJobId,setPrintSettingsByJobId]=useState<Record<string,CustomerSavedPrintSettings>>(()=>initialDraft?.printSettingsByJobId??{});
  const [quotesByJobId,setQuotesByJobId]=useState<Record<string,PrintQuoteResult>>(()=>initialDraft?.quotesByJobId??(initialDraft?.quote&&initialDraft?.uploaded?{[initialDraft.uploaded.job.id]:initialDraft.quote}:{}));
  const [photoSettingsByFileFingerprint,setPhotoSettingsByFileFingerprint]=useState<Record<string,CustomerPhotoSettings>>({});
  const [photoSettingsByJobId,setPhotoSettingsByJobId]=useState<Record<string,CustomerPhotoSettings>>(()=>initialDraft?.photoSettingsByJobId??{});
  const [photoStudioSource,setPhotoStudioSource]=useState<File|null>(null);
  const [photoStudioSourceFingerprint,setPhotoStudioSourceFingerprint]=useState<string|null>(null);
  const [uploadProgress,setUploadProgress]=useState<{completed:number;total:number;currentFile:string|null}|null>(null);
  const [quote,setQuote]=useState<PrintQuoteResult|null>(()=>initialDraft?.quote??null);
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
  const [paymentReference,setPaymentReference]=useState("");
  const [paymentMethod,setPaymentMethod]=useState<"UPI"|"CASH">(paymentOptions.upiEnabled?"UPI":"CASH");
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [uploadMode,setUploadMode]=useState<"LOCAL"|"CLOUD">(
    localAgent?.endpoints.length ? "LOCAL" : "CLOUD"
  );
  const [uploadModeManuallySelected,setUploadModeManuallySelected]=useState(false);

  const secureQueueFallbackEnabled = cloudAssistedPrintingEnabled || !localAgent?.endpoints.length || localFallbackSuggested;

  useEffect(()=>{
    setUploadMode(current=>resolveCustomerUploadMode(
      current,
      Boolean(localAgent?.endpoints?.length),
      secureQueueFallbackEnabled,
      uploadModeManuallySelected
    ));
  },[secureQueueFallbackEnabled,localAgent?.endpoints.join("|"),uploadModeManuallySelected]);

  useEffect(()=>{
    if(uploadModeManuallySelected)return;
    if((localFallbackSuggested||!localReady)&&secureQueueFallbackEnabled) {
      setUploadMode("CLOUD");
    } else if(!localFallbackSuggested&&localReady&&localAgent?.endpoints.length) {
      setUploadMode("LOCAL");
    }
  },[localFallbackSuggested,localReady,localAgent?.endpoints.length,secureQueueFallbackEnabled,uploadModeManuallySelected]);

  const customerUploadUnavailable = !secureQueueFallbackEnabled && !localAgent?.endpoints.length;

  useEffect(()=>{
    if(!paymentOptions.upiEnabled&&paymentOptions.cashEnabled)setPaymentMethod("CASH");
    else if(paymentOptions.upiEnabled)setPaymentMethod("UPI");
  },[paymentOptions.upiEnabled,paymentOptions.cashEnabled]);

  useEffect(()=>{
    const storageKey=`qrtoprint.customer-active-job:${publicCode}`;
    if(!uploaded&&uploadedBatch.length===0&&!batchName.trim()){
      localStorage.removeItem(storageKey);
      return;
    }
    // Store only job metadata, never the bearer session capability.
    localStorage.setItem(storageKey,JSON.stringify({
      sessionMarker:sessionToken.slice(-16),
      uploaded: uploaded??uploadedBatch[0],
      uploadedBatch,
      uploadedFileFingerprints,
      batchId,
      batchName,
      quote,
      fileGroupNames,
      groupSettings,
      fileSettings,
      printSettingsByJobId,
      quotesByJobId,
      photoSettingsByJobId
    } satisfies StoredCustomerDraft));
  },[publicCode,sessionToken,uploaded,uploadedBatch,uploadedFileFingerprints,batchId,batchName,quote,fileGroupNames,groupSettings,fileSettings,printSettingsByJobId,quotesByJobId,photoSettingsByJobId]);

  const batchItemsForStatus=uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]);
  const batchStatusKey=batchItemsForStatus.map(item=>item.job.id).join("|");
  const groupNameForFile=(candidate:File)=>fileGroupNames[customerFileFingerprint(candidate)]??customerBatchGroupName(candidate);
  const defaultsForGroup=(name:string):CustomerGroupSettings=>{
    const baseline:CustomerGroupSettings={
      copies:"1",colourMode:"Mono",isDuplex:false,advancedEnabled:false,paperSize:"A4",orientation:"AUTO"
    };
    return {...baseline,...(groupSettings[name]??{})};
  };
  const settingsForFile=(candidate:File):CustomerSavedPrintSettings=> {
    const fingerprint=customerFileFingerprint(candidate);
    const defaults=defaultsForGroup(groupNameForFile(candidate));
    const own=fileSettings[fingerprint];
    if(photoSettingsByFileFingerprint[fingerprint])return {
      copies:1,colourMode:"Colour",paperSize:"A4",isDuplex:false,orientation:"AUTO",advancedEnabled:false
    };
    const selected=own?.custom?{...defaults,...own}:defaults;
    return {
      copies:Number(normalizeCustomerCopies(selected.copies)),
      colourMode:selected.colourMode,
      isDuplex:selected.isDuplex,
      paperSize:selected.advancedEnabled?selected.paperSize:"A4",
      orientation:selected.advancedEnabled?selected.orientation:"AUTO",
      advancedEnabled:selected.advancedEnabled
    };
  };

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

  useEffect(()=>{
    if(!nativeAppMode)return;
    const tokens=files
      .map(candidate=>smartPrintNativePdfPreviewTokens.get(candidate))
      .filter((token):token is string=>Boolean(token));
    postSmartPrintNativeMessage("retainCustomerPdfPreviews|"+tokens.join(","));
  },[files,nativeAppMode]);

  useEffect(()=>{
    if(!nativeAppMode)return;
    return ()=>{postSmartPrintNativeMessage("retainCustomerPdfPreviews|none");};
  },[nativeAppMode]);

  useEffect(()=>{
    if(!file){setPreviewUrl(null);return;}
    // Some Android document providers expose PDFs with an empty or generic
    // MIME type. Preserve the existing blob preview with the correct PDF type.
    const previewSource=isCustomerPdf(file)&&file.type.toLowerCase()!=="application/pdf"
      ?new Blob([file],{type:"application/pdf"})
      :file;
    const url=URL.createObjectURL(previewSource);
    setPreviewUrl(url);
    return ()=>URL.revokeObjectURL(url);
  },[file]);

  async function upload() {
    if(batchLockedForPayment){
      setError("This batch already has a payment attempt. Start a new batch for additional files.");
      return;
    }
    if(files.length===0)return;
    if(uploadMode==="LOCAL"&&!localReady) {
      setError(nativeAppMode ? "Check shop Wi-Fi." : "Check shop Wi-Fi access above before starting a direct local upload.");
      return;
    }
    let currentBatchId=batchId||crypto.randomUUID();
    let currentBatchName=batchName.trim();
    setBatchId(currentBatchId);
    const completedFingerprints=new Set(uploadedFileFingerprints);
    const results=[...uploadedBatch];
    const savedSettings={...printSettingsByJobId};
    const savedPhotoSettings={...photoSettingsByJobId};
    const pending=files.filter(candidate=>!completedFingerprints.has(customerFileFingerprint(candidate)));
    if(pending.length===0){setError(nativeAppMode ? "Files already uploaded. Choose more." : "All selected files are already uploaded. Choose another document or folder.");return;}
    setBusy(true);setError("");setQuote(null);setQuotesByJobId({});setJobStatus(null);setUploadProgress({completed:0,total:pending.length,currentFile:pending[0]?.name??null});
    try {
      for(let index=0;index<pending.length;index++){
        const candidate=pending[index];
        setUploadProgress({completed:index,total:pending.length,currentFile:candidate.name});
        const fingerprint=customerFileFingerprint(candidate);
        const batchGroupName=groupNameForFile(candidate);
        const settings=settingsForFile(candidate);
        const created=uploadMode==="LOCAL"&&localAgent
          ? await uploadCustomerDocumentLocally(sessionToken,candidate,localAgent,currentBatchId,batchGroupName,currentBatchName||undefined,cloudAssistedPrintingEnabled,settings)
          : await uploadCustomerDocument(sessionToken,candidate,currentBatchId,batchGroupName,currentBatchName||undefined,settings);
        if(created.job.batchId&&created.job.batchId!==currentBatchId) {
          if(results.length>0) {
            throw new ApiError("This file belongs to an earlier batch. Refresh the original print request before adding more files; SmartPrint will not mix two batches.",409);
          }
          currentBatchId=created.job.batchId;
          setBatchId(currentBatchId);
        }
        if(created.job.batchName) {
          currentBatchName=created.job.batchName;
          setBatchName(currentBatchName);
        }
        if(!results.some(item=>item.job.id===created.job.id))results.push(created);
        savedSettings[created.job.id]=settings;
        const photoSettings=photoSettingsByFileFingerprint[fingerprint];
        if(photoSettings)savedPhotoSettings[created.job.id]=photoSettings;
        completedFingerprints.add(fingerprint);
        setUploadedBatch([...results]);
        setUploadedFileFingerprints([...completedFingerprints]);
        setUploadProgress({completed:index+1,total:pending.length,currentFile:pending[index+1]?.name??null});
      }
      setPrintSettingsByJobId(savedSettings);
      setPhotoSettingsByJobId(savedPhotoSettings);
      setUploadedBatch(results);
      setUploaded(results[0]??null);
      setQuote(null);setJobStatus(null);setPayment(null);setPaymentReference("");
      setSelectedFileIndex(0);
      setFiles([]);
      if(paymentOptions.instantPaymentEnabled)await getQuote(results,savedSettings,savedPhotoSettings);
    } catch(exception) {
      setPrintSettingsByJobId(savedSettings);
      setPhotoSettingsByJobId(savedPhotoSettings);
      setUploadedBatch(results);
      setUploadedFileFingerprints([...completedFingerprints]);
      if(results.length>0)setUploaded(results[0]);
      const confirmedPreTicketRejection=uploadMode==="LOCAL"&&exception instanceof LocalUploadPreflightError;
      const agentUnavailable=exception instanceof LocalUploadPreflightError&&
        ["local_print_agent_unavailable","local_print_printer_unavailable"].includes(exception.code??"");
      const batchCheckoutLocked=exception instanceof LocalUploadPreflightError&&
        exception.code==="batch_upload_locked_after_payment_start";
      const canFallbackToCloud=!batchCheckoutLocked&&canFallbackToCloudFromLocalFailure(
        confirmedPreTicketRejection,
        cloudAssistedPrintingEnabled||agentUnavailable
      );
      if(canFallbackToCloud){setUploadModeManuallySelected(true);setUploadMode("CLOUD");}
      if(exception instanceof LocalUploadOutcomeUnknownError&&uploadMode==="LOCAL"&&secureQueueFallbackEnabled) {
        // Local ticket and grant are preserved. The next retry will resume that
        // same PrintJob over HTTPS rather than creating a second cloud upload.
        setUploadModeManuallySelected(true);
        setUploadMode("CLOUD");
      }
      const uploadError=exception instanceof LocalUploadOutcomeUnknownError
        ? cloudAssistedPrintingEnabled
          ? "Shop Wi-Fi was interrupted. Cloud-assisted upload is now selected for secure HTTPS continuation. SmartPrint has kept this file attached to its original job; tap Retry to resume that same upload without creating another print job."
          : "Shop Wi-Fi was interrupted. SmartPrint has kept this file attached to its original job. Reconnect to the shop computer and tap Retry; the same upload will resume without creating a second print job."
        : batchCheckoutLocked
          ? "Checkout has already started for this batch, so this unfinished file was not included. Start a new batch for this file."
          : confirmedPreTicketRejection
          ? exception instanceof LocalUploadPreflightError && exception.code==="local_upload_ticket_expired"
            ? canFallbackToCloud
              ? "The earlier shop Wi-Fi attempt expired before a job was created. Cloud-assisted HTTPS is now selected; tap Retry to start a fresh transfer."
              : "The earlier shop Wi-Fi attempt expired before a job was created. Reconnect to the shop computer, check Wi-Fi, then tap Retry to start a fresh transfer."
            : exception instanceof LocalUploadPreflightError && exception.code==="batch_local_agent_mismatch"
              ? canFallbackToCloud
                ? "Some files in this batch are stored on another shop computer. Cloud-assisted upload is now selected; retry the remaining files in this same batch. The shop can route the complete batch to the computer holding the existing files."
                : "Some files in this batch are on another shop computer. Cloud-assisted upload is not included in this shop's plan. Ask the shop to restore the original computer before retrying the same batch."
              : canFallbackToCloud
                ? "The API rejected local upload before creating a ticket. Cloud-assisted HTTPS upload is now selected; retry the remaining files in this same batch."
                : "The shop cannot accept a local upload right now, and Cloud-assisted upload is not included in its plan. Ask the shop operator to restore the local Agent/printer connection, then retry the same batch."
          : exception instanceof ApiError ? exception.message
          : "Upload stopped after "+completedFingerprints.size+" file(s). Retry the selected files to continue; completed files will be skipped.";
      setError(uploadError);
    } finally {setBusy(false);setUploadProgress(null);}
  }

  function selectUploadedFile(item:UploadedPrintJob) {
    setUploaded(item);setQuote(quotesByJobId[item.job.id]??null);setJobStatus(null);setPayment(null);setPaymentReference("");setError("");
  }

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

  function applyPhotoSheets(sheets:PreparedPhotoSheet[]) {
    if(!photoStudioSourceFingerprint||!sheets.length)return;
    const index=files.findIndex(candidate=>customerFileFingerprint(candidate)===photoStudioSourceFingerprint);
    if(index<0){setError("The source photo is no longer in this batch. Select it again.");setPhotoStudioSource(null);setPhotoStudioSourceFingerprint(null);return;}
    const generated=sheets.map(sheet=>sheet.file),nextFiles=[...files.slice(0,index),...generated,...files.slice(index+1)];
    const pending=nextFiles.filter(candidate=>!uploadedFileFingerprints.includes(customerFileFingerprint(candidate)));
    const bytes=uploadedBatch.reduce((sum,item)=>sum+item.document.sizeBytes,0)+pending.reduce((sum,file)=>sum+file.size,0);
    if(uploadedBatch.length+pending.length>20||bytes>50*1024*1024){setError("Prepared photo sheets exceed the batch limit. Reduce the copies or start a new batch.");setPhotoStudioSource(null);setPhotoStudioSourceFingerprint(null);return;}
    const group=groupNameForFile(files[index]),groups={...fileGroupNames},settings={...fileSettings},photos={...photoSettingsByFileFingerprint};
    delete groups[photoStudioSourceFingerprint];delete settings[photoStudioSourceFingerprint];delete photos[photoStudioSourceFingerprint];
    for(const sheet of sheets){const fp=customerFileFingerprint(sheet.file);groups[fp]=group;settings[fp]={copies:"1",custom:true,colourMode:"Colour",isDuplex:false,advancedEnabled:false,paperSize:"A4",orientation:"AUTO"};photos[fp]={sizeCode:sheet.sizeCode,photoCount:sheet.photoCount,photosPerPage:sheet.photosPerPage};}
    setFiles(nextFiles);setSelectedFileIndex(index);setFileGroupNames(groups);setFileSettings(settings);setPhotoSettingsByFileFingerprint(photos);setPhotoStudioSource(null);setPhotoStudioSourceFingerprint(null);setError("");
  }
  function openPhotoStudio(candidate:File){
    const fingerprint=customerFileFingerprint(candidate);
    if(uploadedFileFingerprints.includes(fingerprint)){
      setError("This photo has already been sent to the shop. The shop can prepare it from the review queue; reopening it here would create a duplicate print request.");
      return;
    }
    if(!photoRatesAvailable){setError("Photo printing is not configured at this shop yet. Please ask the shop owner to enable photo pricing.");return;}
    setPhotoStudioSource(candidate);
    setPhotoStudioSourceFingerprint(fingerprint);
    setError("");
  }
  function openCustomerPdfPreview(candidate:File) {
    const token=smartPrintNativePdfPreviewTokens.get(candidate);
    if(!token||!postSmartPrintNativeMessage("previewCustomerPdf|"+token)) {
      setError("Please select this PDF again before opening its preview.");
      return;
    }
    setError("");
  }
  function chooseFiles(next:File[], nativeGroupNames?:string[], nativePreviewTokens?:Array<string|null>) {
    if(batchLockedForPayment){
      setError("This batch already has a payment attempt. Start a new batch for additional files.");
      return;
    }
    if(nativePreviewTokens?.length===next.length) {
      next.forEach((candidate,index)=>{
        const token=nativePreviewTokens[index];
        if(token&&isCustomerPdf(candidate))smartPrintNativePdfPreviewTokens.set(candidate,token);
      });
    }
    let nestedSkipped=0;
    const rootFiles=next.filter(candidate=>{
      const relativePath=(candidate as File & {webkitRelativePath?:string}).webkitRelativePath??"";
      const parts=relativePath.split("/").filter(Boolean);
      if(relativePath&&parts.length>2){nestedSkipped++;return false;}
      return true;
    });
    const supported=rootFiles.filter(candidate=>/\.(pdf|jpe?g|png)$/i.test(candidate.name));
    for(const candidate of supported){
      const nativeGroup=nativeGroupNames?.[next.indexOf(candidate)]?.trim().slice(0,80);
      if(nativeGroup)(candidate as File & {__smartPrintBatchGroup?:string}).__smartPrintBatchGroup=nativeGroup;
    }
    const unsupportedSkipped=rootFiles.length-supported.length;
    if(supported.length===0){setError("Choose printable PDF, JPG or PNG files.");return;}
    const oversized=supported.find(candidate=>candidate.size<=0||candidate.size>20*1024*1024);
    if(oversized){setError(`“${oversized.name}” exceeds the 20 MB per-file limit.`);return;}
    const merged=new Map(files.map(candidate=>[customerFileFingerprint(candidate),candidate]));
    for(const candidate of supported)merged.set(customerFileFingerprint(candidate),candidate);
    const selected=Array.from(merged.values());
    const alreadyUploaded=new Set(uploadedFileFingerprints);
    const pending=selected.filter(candidate=>!alreadyUploaded.has(customerFileFingerprint(candidate)));
    const totalBytes=uploadedBatch.reduce((sum,item)=>sum+item.document.sizeBytes,0)+pending.reduce((sum,candidate)=>sum+candidate.size,0);
    if(uploadedBatch.length+pending.length>20||totalBytes>50*1024*1024){
      setError(nativeAppMode ? "20 files · 50 MB max." : "This batch can contain up to 20 files and 50 MB in total.");
      return;
    }
    setFileGroupNames(current=>{
      const updated={...current};
      for(const candidate of supported){
        const nativeGroup=nativeGroupNames?.[next.indexOf(candidate)]?.trim();
        updated[customerFileFingerprint(candidate)]=nativeGroup||customerBatchGroupName(candidate);
      }
      return updated;
    });
    setGroupSettings(current=>{
      const updated={...current};
      for(const candidate of supported) {
        const nativeGroup=nativeGroupNames?.[next.indexOf(candidate)]?.trim();
        const group=nativeGroup||customerBatchGroupName(candidate);
        if(!updated[group])updated[group]={copies:"1",colourMode:"Mono",isDuplex:false,advancedEnabled:false,paperSize:"A4",orientation:"AUTO"};
      }
      return updated;
    });
    setFileSettings(current=>{
      const updated={...current};
      for(const candidate of supported) {
        const key=customerFileFingerprint(candidate);
        if(!updated[key])updated[key]={...defaultsForGroup(fileGroupNames[key]??customerBatchGroupName(candidate)),custom:false};
      }
      return updated;
    });
    setFiles(selected);setSelectedFileIndex(0);
    setError(nestedSkipped? "Nested folders are skipped; only files directly inside each selected folder are included.":unsupportedSkipped? "Unsupported file types were skipped.": "");
  }

  function removeSelectedFile(fingerprint: string) {
    if (busy) return;
    const index = files.findIndex(candidate => customerFileFingerprint(candidate) === fingerprint);
    if (index < 0) return;
    const nextFiles = files.filter(candidate => customerFileFingerprint(candidate) !== fingerprint);
    setFiles(nextFiles);
    setSelectedFileIndex(current => current === index
      ? Math.min(index, Math.max(0, nextFiles.length - 1))
      : current > index ? current - 1 : current);
    setFileGroupNames(current => {
      const next = { ...current };
      delete next[fingerprint];
      return next;
    });
    setFileSettings(current => {
      const next = { ...current };
      delete next[fingerprint];
      return next;
    });
    setPhotoSettingsByFileFingerprint(current=>{const next={...current};delete next[fingerprint];return next;});
    setError("");
  }

  const summary = deriveCustomerPrintSummary({
    files, file, uploaded, uploadedBatch, uploadedFileFingerprints, batchId, batchPayment,
    payment, jobStatus, batchStatuses, quotesByJobId, photoPricing, groupNameForFile
  });

  const {
    pendingFilesCount, uploadGroups, quoteItems, batchLockedForPayment, statusForBatchItem,
    quoteExpiredForBatchItem, batchPayableItems, batchPayableTotal, hasLegacyIndividualPayment,
    hasSelectedLegacyIndividualPayment, batchPaymentStarted, acceptedQuoteExpired,
    batchCheckoutVisible, batchCheckoutUsesPayment, legacyIndividualPayment, batchCheckoutAmount,
    batchCheckoutFileCount, batchCheckoutCurrency, customerPaymentForBatchItem,
    batchCheckoutLineItems, batchPaymentRefundedTotal, batchPaymentAllocationComplete,
    batchCheckoutHeading, quoteEntries, acceptedQuoteEntries, batchQuoteTotal,
    batchDocumentSourcePages, batchPhotoCount, batchPhotoAmount, batchMonoPrintedPages,
    batchColourPrintedPages, batchPrintedPages, batchMonoAmount, batchColourAmount,
    canSendBatch, canCalculateBatch, photoPriceMap, photoAlreadyUploaded
  } = summary;


  return {
    files,
    selectedFileIndex,
    setSelectedFileIndex,
    previewUrl,
    uploaded,
    uploadedBatch,
    batchId,
    batchName,
    setBatchName,
    setGroupSettings,
    fileSettings,
    setFileSettings,
    printSettingsByJobId,
    quotesByJobId,
    setQuotesByJobId,
    photoSettingsByFileFingerprint,
    photoSettingsByJobId,
    photoStudioSource,
    setPhotoStudioSource,
    setPhotoStudioSourceFingerprint,
    uploadProgress,
    quote,
    setQuote,
    jobStatus,
    batchStatuses,
    payment,
    batchPayment,
    paymentReference,
    setPaymentReference,
    paymentMethod,
    setPaymentMethod,
    busy,
    error,
    uploadMode,
    setUploadMode,
    setUploadModeManuallySelected,
    nativeAppMode,
    photoRatesAvailable,
    file,
    secureQueueFallbackEnabled,
    customerUploadUnavailable,
    defaultsForGroup,
    pendingFilesCount,
    uploadGroups,
    quoteItems,
    batchLockedForPayment,
    statusForBatchItem,
    quoteExpiredForBatchItem,
    batchPayableItems,
    batchPayableTotal,
    hasLegacyIndividualPayment,
    batchCheckoutVisible,
    batchCheckoutUsesPayment,
    legacyIndividualPayment,
    batchCheckoutAmount,
    batchCheckoutFileCount,
    batchCheckoutCurrency,
    customerPaymentForBatchItem,
    batchCheckoutLineItems,
    batchPaymentRefundedTotal,
    batchPaymentAllocationComplete,
    batchCheckoutHeading,
    quoteEntries,
    batchQuoteTotal,
    batchDocumentSourcePages,
    batchPhotoCount,
    batchPhotoAmount,
    batchMonoPrintedPages,
    batchColourPrintedPages,
    batchPrintedPages,
    batchMonoAmount,
    batchColourAmount,
    canSendBatch,
    canCalculateBatch,
    photoPriceMap,
    photoAlreadyUploaded,
    upload,
    selectUploadedFile,
    getQuote,
    acceptQuote,
    refreshJobStatus,
    beginPayment,
    submitPaymentReference,
    applyPhotoSheets,
    openPhotoStudio,
    openCustomerPdfPreview,
    chooseFiles,
    removeSelectedFile
  };