import {
  useEffect,
  useState } from "react";
import { useCustomerPrintStatus } from "./useCustomerPrintStatus";
import {
  ApiError,
  acceptCustomerQuote,
  calculateCustomerQuote,
  getCustomerJobStatus,
  reportCustomerBatchPayment,
  reportCustomerUpiPayment,
  startCustomerBatchPayment,
  startCustomerPayment,
  type PrintQuoteResult,
  type UploadedPrintJob
} from "../../api";
import { deriveCustomerPrintSummary } from "./customer-print-summary";
import { useCustomerPrintUpload } from "./useCustomerPrintUpload";
import { resolveCustomerUploadMode } from "../../local-upload-routing";
import {
  customerFileFingerprint, isCustomerPdf, smartPrintNativePdfPreviewTokens, postSmartPrintNativeMessage,
  customerBatchGroupName, normalizeCustomerCopies, readStoredCustomerDraft,
  type CustomerGroupSettings, type CustomerFileSettings, type CustomerSavedPrintSettings,
  type CustomerPhotoSettings, type StoredCustomerDraft, type CustomerPrintFlowProps
} from "./customer-print-utils";


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
  const [quote,setQuote]=useState<PrintQuoteResult|null>(()=>initialDraft?.quote??null);
  const [paymentReference,setPaymentReference]=useState("");
  const [paymentMethod,setPaymentMethod]=useState<"UPI"|"CASH">(paymentOptions.upiEnabled?"UPI":"CASH");
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [uploadMode,setUploadMode]=useState<"LOCAL"|"CLOUD">(
    localAgent?.endpoints.length ? "LOCAL" : "CLOUD"
  );
  const [uploadModeManuallySelected,setUploadModeManuallySelected]=useState(false);

  const {
    jobStatus, setJobStatus, batchStatuses, payment, setPayment, batchPayment, setBatchPayment,
    rememberBatchPayment, rememberCustomerPayment, rememberBatchStatuses, rememberCustomerStatus,
    refreshJobStatus, refreshBatchStatuses
  } = useCustomerPrintStatus({
    sessionToken, uploaded, uploadedBatch, setPrintSettingsByJobId, setQuotesByJobId, setQuote, setBusy, setError
  });


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
      setError("This batch already has a payment attempt. StaretJobStatus(null);setPayment(null);setPaymentReference("");setError("");
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

  const {
    upload, uploadProgress, applyPhotoSheets, openPhotoStudio, openCustomerPdfPreview,
    chooseFiles, removeSelectedFile
  } = useCustomerPrintUpload({
    sessionToken, nativeAppMode, files, setFiles, selectedFileIndex, setSelectedFileIndex,
    uploaded, setUploaded, uploadedBatch, setUploadedBatch, uploadedFileFingerprints, setUploadedFileFingerprints,
    batchId, setBatchId, batchName, setBatchName, printSettingsByJobId, setPrintSettingsByJobId,
    photoSettingsByJobId, setPhotoSettingsByJobId, photoSettingsByFileFingerprint, setPhotoSettingsByFileFingerprint,
    fileGroupNames, setFileGroupNames, groupSettings, setGroupSettings, fileSettings, setFileSettings,
    photoStudioSourceFingerprint, setPhotoStudioSource, setPhotoStudioSourceFingerprint,
    uploadMode, setUploadMode, setUploadModeManuallySelected, secureQueueFallbackEnabled,
    cloudAssistedPrintingEnabled, localAgent, localReady, paymentOptions, photoRatesAvailable,
    batchLockedForPayment, busy, setBusy, setError, setQuote, setQuotesByJobId, setJobStatus,
    setPayment, setPaymentReference, groupNameForFile, defaultsForGroup, settingsForFile, getQuote
  });

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

  function applyPhotoSheets(sheets:PreparedPhotoSheet[]) {
    if(!photoStudioSourceFingerprint||!sheets.length)return;
 d, batchPayment,
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