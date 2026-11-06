import {
  useEffect,
  useState } from "react";
import { useCustomerPrintStatus } from "./useCustomerPrintStatus";
import { useCustomerPrintUpload } from "./useCustomerPrintUpload";
import { useCustomerPrintCheckout } from "./useCustomerPrintCheckout";
import type { PrintQuoteResult, UploadedPrintJob } from "../../api";
import { deriveCustomerPrintSummary } from "./customer-print-summary";
import { isCustomerBatchFinished } from "../../customer-session-state";
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

  const batchItemsForStatus=uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]);
  const canStartNewSession=!!uploaded&&isCustomerBatchFinished(batchItemsForStatus,batchStatuses,uploaded.job.id,jobStatus?.status);

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

  const checkout = useCustomerPrintCheckout({
    sessionToken, uploaded, uploadedBatch, batchId, jobStatus, batchStatuses,
    quotesByJobId, setQuotesByJobId, printSettingsByJobId, photoSettingsByJobId,
    quoteExpiredForBatchItem, statusForBatchItem, hasLegacyIndividualPayment,
    payment, batchPayment, setBatchPayment, setQuote, setJobStatus,
    rememberBatchStatuses, rememberCustomerStatus, rememberCustomerPayment, rememberBatchPayment,
    refreshBatchStatuses, upiEnabled: paymentOptions.upiEnabled, cashEnabled: paymentOptions.cashEnabled, setBusy, setError
  });
  const {
    paymentReference, setPaymentReference, paymentMethod, setPaymentMethod,
    getQuote, acceptQuote, beginPayment, submitPaymentReference
  } = checkout;



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

  function selectUploadedFile(item:UploadedPrintJob) {
    setUploaded(item);setQuote(quotesByJobId[item.job.id]??null);setJobStatus(null);setPayment(null);setPaymentReference("");setError("");
  }

  const {
    upload, uploadProgress, applyPhotoSheets, openPhotoStudio, openCustomerPdfPreview,
    chooseFiles, removeSelectedFile, photoPreviewPagesByFingerprint
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
    photoPreviewPagesByFingerprint,
    canStartNewSession,
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
}
