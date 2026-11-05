import {
  rejectedPageList, formatBytes, consumeSmartPrintNativePickerGroups, customerPhotoSizeLabel,
  isCustomerImage, customerFileFingerprint, isCustomerPdf, smartPrintNativePdfPreviewTokens,
  postSmartPrintNativeMessage, consumeSmartPrintNativePdfPreviews, customerBatchGroupName,
  normalizeCustomerCopies, readStoredCustomerDraft, smartPrintNativeHeadline, smartPrintNativeStatus,
  smartPrintNativeBoundary, type CustomerLocalAgent, type CustomerGroupSettings, type CustomerFileSettings,
  type CustomerSavedPrintSettings, type CustomerPhotoSettings, type StoredCustomerDraft
} from "./customer-print-utils";

export default function CustomerPrintFlow({dark,publicCode,sessionToken,paymentOptions,localAgent,localReady,localFallbackSuggested,cloudAssistedPrintingEnabled,photoPricing,hasAssignedPrinter,printerCapabilitiesJson}:{dark:boolean;publicCode:string;sessionToken:string;paymentOptions:{upiEnabled:boolean;upiId?:string|null;payeeName?:string|null;cashEnabled:boolean;instantPaymentEnabled:boolean};localAgent:CustomerLocalAgent|null;localReady:boolean;localFallbackSuggested:boolean;cloudAssistedPrintingEnabled:boolean;photoPricing:CustomerPhotoPricing|null;hasAssignedPrinter:boolean;printerCapabilitiesJson?:string|null}) {
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

  const pendingFilesCount=files.filter(candidate=>!uploadedFileFingerprints.includes(customerFileFingerprint(candidate))).length;
  const uploadGroups=Array.from(new Set(files.map(candidate=>groupNameForFile(candidate)))).map(name=>({
    name, files:files.filter(candidate=>groupNameForFile(candidate)===name)
  }));
  const quoteItems=uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]);
  const batchLockedForPayment=(batchPayment!==null&&batchPayment.status!=="REJECTED")||
    (payment!==null&&!payment.batchPayment&&["AWAITING_CUSTOMER_PAYMENT","CUSTOMER_REPORTED","CASH_AWAITING_PAYMENT","VERIFIED","PARTIALLY_REFUNDED"].includes(payment.status));
  const statusForBatchItem=(item:UploadedPrintJob)=>
    uploaded?.job.id===item.job.id&&jobStatus?.status
      ? jobStatus.status
      : batchStatuses[item.job.id]?.status??item.job.status;
  const quoteExpiredForBatchItem=(item:UploadedPrintJob)=>{
    const status=statusForBatchItem(item);
    if(status==="QUOTE_EXPIRED")return true;
    const expiry=quotesByJobId[item.job.id]?.quoteExpiresUtc??batchStatuses[item.job.id]?.quoteExpiresUtc;
    if(!expiry)return ["AWAITING_CUSTOMER_APPROVAL","AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT","PAYMENT_REJECTED"].includes(status);
    const expiresAt=Date.parse(expiry);
    return !Number.isFinite(expiresAt)||expiresAt<=Date.now();
  };
  const batchCheckoutStatuses=new Set([
    "REJECTED","REFUNDED","AWAITING_PAYMENT","PAYMENT_REJECTED",
    "AWAITING_CUSTOMER_PAYMENT","PAYMENT_REPORTED","AWAITING_COUNTER_PAYMENT",
    "PAID_AUTHORISED","LOCAL_QUEUE_CLAIMED","LOCAL_QUEUE_READY","PRINT_AUTHORISED",
    "PRINT_SUBMITTING","SPOOLER_ACCEPTED","PRINTING","OUTPUT_UNCONFIRMED","COMPLETED"
  ]);
  const batchCheckoutReady=quoteItems.length>0&&quoteItems.every(item=>batchCheckoutStatuses.has(statusForBatchItem(item)));
  const batchPayableItems=quoteItems.filter(item=>["AWAITING_PAYMENT","PAYMENT_REJECTED"].includes(statusForBatchItem(item)));
  const batchPayableTotal=batchPayableItems.reduce((total,item)=>
    total+(batchStatuses[item.job.id]?.quotedAmountMinor??quotesByJobId[item.job.id]?.quotedAmountMinor??item.job.quotedAmountMinor??0),0);
  const hasLegacyIndividualPayment=quoteItems.some(item=>{
    const itemPayment=uploaded?.job.id===item.job.id&&payment
      ?payment
      :batchStatuses[item.job.id]?.payment??null;
    return Boolean(itemPayment&&!itemPayment.batchPayment&&
      ["AWAITING_CUSTOMER_PAYMENT","CUSTOMER_REPORTED","CASH_AWAITING_PAYMENT","VERIFIED","PARTIALLY_REFUNDED"].includes(itemPayment.status));
  });
  const hasSelectedLegacyIndividualPayment=payment!==null&&!payment.batchPayment&&
    ["AWAITING_CUSTOMER_PAYMENT","CUSTOMER_REPORTED","CASH_AWAITING_PAYMENT","VERIFIED","PARTIALLY_REFUNDED"].includes(payment.status);
  const batchPaymentStarted=batchPayment!==null&&batchPayment.status!=="REJECTED";
  const acceptedQuoteExpired=shouldBlockBatchCheckoutForExpiredQuote(
    batchPayment?.status,
    quoteItems.some(item=>!["REJECTED","REFUNDED"].includes(statusForBatchItem(item))&&quoteExpiredForBatchItem(item))
  );
  const batchCheckoutVisible=Boolean(batchId)&&!hasLegacyIndividualPayment&&
    (batchPaymentStarted||(!acceptedQuoteExpired&&batchCheckoutReady&&batchPayableItems.length>0));
  const batchCheckoutUsesPayment=batchPayment!==null&&batchPayment.status!=="REJECTED";
  const legacyIndividualPayment=!batchId||hasSelectedLegacyIndividualPayment;
  const batchCheckoutAmount=batchCheckoutUsesPayment?batchPayment!.amountMinor:batchPayableTotal;
  const batchCheckoutFileCount=batchCheckoutUsesPayment?batchPayment!.fileCount:batchPayableItems.length;
  const batchCheckoutCurrency=batchCheckoutUsesPayment?batchPayment!.currency:(batchPayableItems[0]?.job.currency??"INR");
  const customerPaymentForBatchItem=(item:UploadedPrintJob)=>
    uploaded?.job.id===item.job.id&&payment
      ?payment
      :batchStatuses[item.job.id]?.payment??null;
  const batchPaymentItems=batchCheckoutUsesPayment
    ?quoteItems.filter(item=>customerPaymentForBatchItem(item)?.batchPaymentId===batchPayment!.id)
    :batchPayableItems;
  const batchCheckoutLineItems=batchCheckoutUsesPayment?batchPaymentItems:batchPayableItems;
  const batchPaymentAllocatedTotal=batchPaymentItems.reduce((total,item)=>
    total+(customerPaymentForBatchItem(item)?.amountMinor??0),0);
  const batchPaymentRefundedTotal=batchPaymentItems.reduce((total,item)=>
    total+(customerPaymentForBatchItem(item)?.refundedAmountMinor??0),0);
  const batchPaymentAllocationComplete=batchCheckoutUsesPayment&&
    batchPaymentItems.length===batchPayment!.fileCount&&
    batchPaymentAllocatedTotal===batchPayment!.amountMinor;
  const batchCheckoutHeading=batchCheckoutUsesPayment
    ?batchPayment!.status==="VERIFIED"?"BATCH PAYMENT VERIFIED"
      :batchPayment!.status==="CUSTOMER_REPORTED"?"PAYMENT REFERENCE SUBMITTED"
      :batchPayment!.status==="CASH_AWAITING_PAYMENT"?"PAY AT SHOP COUNTER"
      :"PAYMENT FOR ACCEPTED FILES"
    :"PAY FOR ACCEPTED FILES";
  const quoteEntries=quoteItems.map(item=>({item,quote:quotesByJobId[item.job.id]}))
    .filter(entry=>!!entry.quote&&!quoteExpiredForBatchItem(entry.item));
  const acceptedQuoteEntries=quoteEntries.filter(entry=>
    !["REJECTED","REFUNDED"].includes(statusForBatchItem(entry.item))
  );
  const batchQuoteTotal=acceptedQuoteEntries.reduce((total,entry)=>
    total+(entry.quote?.quotedAmountMinor??0),0);
  const acceptedDocumentEntries=acceptedQuoteEntries.filter(entry=>entry.quote?.printKind!=="PHOTO");
  const acceptedPhotoEntries=acceptedQuoteEntries.filter(entry=>entry.quote?.printKind==="PHOTO");
  const batchDocumentSourcePages=acceptedDocumentEntries.reduce((total,entry)=>
    total+(entry.quote?.pageCount??entry.item.job.pageCount),0);
  const batchPhotoCount=acceptedPhotoEntries.reduce((total,entry)=>total+(entry.quote?.photoCount??0),0);
  const batchPhotoAmount=acceptedPhotoEntries.reduce((total,entry)=>total+(entry.quote?.quotedAmountMinor??0),0);
  const batchMonoPrintedPages=acceptedDocumentEntries.reduce((total,entry)=>
    total+(entry.quote?.colourMode==="Mono"?(entry.quote!.pageCount*entry.quote!.copies):0),0);
  const batchColourPrintedPages=acceptedDocumentEntries.reduce((total,entry)=>
    total+(entry.quote?.colourMode==="Colour"?(entry.quote!.pageCount*entry.quote!.copies):0),0);
  const batchPrintedPages=batchMonoPrintedPages+batchColourPrintedPages+
    acceptedPhotoEntries.reduce((total,entry)=>total+(entry.quote?.pageCount??0),0);
  const batchMonoAmount=acceptedDocumentEntries.reduce((total,entry)=>
    total+(entry.quote?.colourMode==="Mono"?(entry.quote!.quotedAmountMinor??0):0),0);
  const batchColourAmount=acceptedDocumentEntries.reduce((total,entry)=>
    total+(entry.quote?.colourMode==="Colour"?(entry.quote!.quotedAmountMinor??0):0),0);
  const quoteEligibleItems=quoteItems.filter(item=>!["REJECTED","REFUNDED"].includes(statusForBatchItem(item)));
  const canSendBatch=quoteEligibleItems.length>0&&
    acceptedQuoteEntries.length===quoteEligibleItems.length&&
    acceptedQuoteEntries.some(entry=>entry.quote?.status==="AWAITING_CUSTOMER_APPROVAL");
  const canCalculateBatch=quoteItems.some(item=>{
    const status=statusForBatchItem(item);
    if(quoteExpiredForBatchItem(item))return true;
    if(quotesByJobId[item.job.id]&&quotesByJobId[item.job.id].status!=="QUOTE_EXPIRED")return false;
    return !quotesByJobId[item.job.id]||quotesByJobId[item.job.id].status==="QUOTE_EXPIRED"||
      ["AWAITING_CONFIGURATION","UPLOADED","Uploaded"].includes(status);
  });

  const photoPriceMap={"35x45":photoPricing?.photo35x45Minor??0,"25x35":photoPricing?.photo25x35Minor??0,"2x2":photoPricing?.photo2x2Minor??0,"4x6":photoPricing?.photo4x6Minor??0};
  const photoAlreadyUploaded=Boolean(file&&uploadedFileFingerprints.includes(customerFileFingerprint(file)));
  return <>{photoStudioSource&&<PhotoStudio source={photoStudioSource} mode="customer" dark={dark} photoPrices={photoPriceMap} onCancel={()=>{setPhotoStudioSource(null);setPhotoStudioSourceFingerprint(null);}} onApply={applyPhotoSheets}/>}<section className="customer-print-flow">
    <div className="customer-flow-heading"><div><span>STEP 1</span><h2>{nativeAppMode?"Add files":"Add documents or images"}</h2></div><span className="upload-limit">PDF documents · JPG/PNG images · Up to 20 files / 50 MB per batch</span></div>
    <div className="customer-upload-modes">
      {localAgent?.endpoints.length
        ? <button type="button" aria-pressed={uploadMode==="LOCAL"} className={uploadMode==="LOCAL"?"customer-upload-mode selected":"customer-upload-mode"} onClick={()=>{setUploadModeManuallySelected(true);setUploadMode("LOCAL");}}><strong>Shop Wi-Fi · local transfer</strong><small>Direct local HTTP · not encrypted in transit</small></button>
        : <button type="button" className="customer-upload-mode customer-upload-mode-unavailable" disabled aria-disabled="true"><strong>Shop Wi-Fi · local transfer</strong><small>Temporarily unavailable · the shop computer has not reported a reachable local connection</small></button>}
      {secureQueueFallbackEnabled&&<button type="button" aria-pressed={uploadMode==="CLOUD"} className={uploadMode==="CLOUD"?"customer-upload-mode selected":"customer-upload-mode"} onClick={()=>{setUploadModeManuallySelected(true);setUploadMode("CLOUD");}}><strong>{cloudAssistedPrintingEnabled?"Cloud-assisted · HTTPS":"Secure queue · HTTPS"}</strong><small>Upload securely now; the shop can print when its Agent and printer are ready</small></button>}
    </div>
    {customerUploadUnavailable&&<div className="customer-upload-local-note" role="status">Secure upload is temporarily unavailable. Please ask the shop to reconnect its print service.</div>}
    <div className="customer-upload-picker-grid"
      onDragOver={event=>{if(!batchLockedForPayment)event.preventDefault();}}
      onDrop={event=>{event.preventDefault();if(batchLockedForPayment)return;chooseFiles(Array.from(event.dataTransfer.files||[]));}}>
      <label className="customer-upload-zone">
        <input type="file" multiple disabled={batchLockedForPayment} accept=".pdf,.jpg,.jpeg,.png,application/pdf,image/jpeg,image/png" onChange={event=>{const selected=Array.from(event.currentTarget.files||[]);chooseFiles(selected,consumeSmartPrintNativePickerGroups(),consumeSmartPrintNativePdfPreviews());event.currentTarget.value="";}}/>
        <span className="customer-upload-icon"><FileUp size={21}/></span>
        <strong>Select documents or images</strong>
        <small>Choose PDF documents or JPG/PNG images</small>
      </label>
      <label className="customer-upload-zone folder-upload-zone">
        <input type="file" multiple disabled={batchLockedForPayment} accept={nativeAppMode?"application/x-smartprint-folder-selection":".pdf,.jpg,.jpeg,.png,application/pdf,image/jpeg,image/png"} {...({webkitdirectory:"true",directory:"true"} as any)} onChange={event=>{const selected=Array.from(event.currentTarget.files||[]);chooseFiles(selected,consumeSmartPrintNativePickerGroups(),consumeSmartPrintNativePdfPreviews());event.currentTarget.value="";}}/>
        <span className="customer-upload-icon"><FolderOpen size={21}/></span>
        <strong>{nativeAppMode?"Choose folders":"Add a folder"}</strong>
        <small>{nativeAppMode?"Select multiple folders at once":"Choose one folder; select again to add another to this batch"}</small>
      </label>
    </div>
    {files.length>0&&<div className="customer-selected-files">
      <div className="customer-selected-files-head"><strong>{files.length} selected</strong><span>{formatBytes(files.reduce((total,item)=>total+item.size,0))} in this selection</span></div>
      <div className="customer-batch-groups">{uploadGroups.map(group=>{
        const defaults=defaultsForGroup(group.name);
        return <section className="customer-folder-group" key={group.name}>
          <div className="customer-folder-group-head">
            <div className="customer-folder-title"><FolderOpen size={16}/><span><strong>{group.name}</strong><small>{group.files.length} file(s)</small></span></div>
            <div className="customer-folder-defaults">
              <div><span>Colour</span><div className="customer-choice-row">
                <button type="button" className={defaults.colourMode==="Mono"?"customer-choice selected":"customer-choice"} onClick={()=>setGroupSettings(current=>({...current,[group.name]:{...defaults,colourMode:"Mono"}}))}>B&W</button>
                <button type="button" className={defaults.colourMode==="Colour"?"customer-choice selected":"customer-choice"} onClick={()=>setGroupSettings(current=>({...current,[group.name]:{...defaults,colourMode:"Colour"}}))}>Colour</button>
              </div></div>
              <div><span>Sides</span><div className="customer-choice-row">
                <button type="button" className={!defaults.isDuplex?"customer-choice selected":"customer-choice"} onClick={()=>setGroupSettings(current=>({...current,[group.name]:{...defaults,isDuplex:false}}))}>Single</button>
                <button type="button" className={defaults.isDuplex?"customer-choice selected":"customer-choice"} onClick={()=>setGroupSettings(current=>({...current,[group.name]:{...defaults,isDuplex:true}}))}>Double</button>
              </div></div>
              <label className="customer-folder-copies"><span>Copies · all files</span><input type="text" inputMode="numeric" pattern="[0-9]*" maxLength={3} value={defaults.copies}
                 onChange={event=>{
                   const copies=event.currentTarget.value.replace(/\D/g,"").slice(0,3);
                   setGroupSettings(current=>({...current,[group.name]:{...(current[group.name]??defaults),copies}}));
                 }}
                 onBlur={event=>{
                   const copies=normalizeCustomerCopies(event.currentTarget.value);
                   setGroupSettings(current=>({...current,[group.name]:{...(current[group.name]??defaults),copies}}));
                 }}/></label>
              <div><span>Paper</span><strong className="customer-paper-default">{defaults.advancedEnabled?defaults.paperSize:"A4"}</strong></div>
              <label className="customer-advanced-toggle"><input type="checkbox" checked={defaults.advancedEnabled} onChange={event=>{
                 const advancedEnabled=event.currentTarget.checked;
                 setGroupSettings(current=>({...current,[group.name]:{...(current[group.name]??defaults),advancedEnabled}}));
               }}/><span>Advanced settings</span></label>
            </div>
            {defaults.advancedEnabled&&<div className="customer-file-overrides customer-folder-advanced">
              <label><span>Paper size</span><select value={defaults.paperSize} onChange={event=>{
                 const paperSize=event.currentTarget.value as CustomerGroupSettings["paperSize"];
                 setGroupSettings(current=>({...current,[group.name]:{...(current[group.name]??defaults),paperSize}}));
               }}>
                <option value="A4">A4</option><option value="A3">A3</option><option value="A5">A5</option><option value="LETTER">Letter</option><option value="LEGAL">Legal</option>
              </select></label>
              <label><span>Orientation</span><select value={defaults.orientation} onChange={event=>{
                 const orientation=event.currentTarget.value as CustomerGroupSettings["orientation"];
                 setGroupSettings(current=>({...current,[group.name]:{...(current[group.name]??defaults),orientation}}));
               }}>
                <option value="AUTO">Auto · follow document</option><option value="PORTRAIT">Portrait</option><option value="LANDSCAPE">Landscape</option>
              </select></label>
            </div>}
          </div>
          <div className="customer-folder-file-list">{group.files.map(selected=>{
            const fingerprint=customerFileFingerprint(selected);
            const savedOwn=fileSettings[fingerprint]??{...defaults,custom:false};
            const own={...defaults,...savedOwn};
            const index=files.findIndex(candidate=>customerFileFingerprint(candidate)===fingerprint);
            return <div className={selectedFileIndex===index?"customer-folder-file-row selected":"customer-folder-file-row"} key={fingerprint}>
              <button type="button" className="customer-folder-file-name" onClick={()=>setSelectedFileIndex(index)}><FileText size={15}/><span><strong>{selected.name}</strong><small>{formatBytes(selected.size)}</small></span><PrintKindTag printKind={photoSettingsByFileFingerprint[fingerprint]?"PHOTO":"DOCUMENT"}/></button>
              <button type="button" className={own.custom?"customer-file-custom active":"customer-file-custom"} onClick={()=>setFileSettings(current=>({...current,[fingerprint]:{...defaults,...(current[fingerprint]??{}),custom:!own.custom,...(!own.custom?{copies:defaults.copies,colourMode:defaults.colourMode,isDuplex:defaults.isDuplex,advancedEnabled:defaults.advancedEnabled,paperSize:defaults.paperSize,orientation:defaults.orientation}:{})}}))}>{own.custom?"Custom":"Folder"}</button>
              <button type="button" className="customer-file-remove" aria-label={`Remove ${selected.name} from this selection`} title="Remove from this selection" disabled={busy} onClick={()=>removeSelectedFile(fingerprint)}><X size={13}/><span>Remove</span></button>
              {own.custom&&<div className="customer-file-overrides">
                <label className="customer-folder-copies"><span>Copies</span><input type="text" inputMode="numeric" pattern="[0-9]*" maxLength={3} value={own.copies}
                   onChange={event=>{
                     const copies=event.currentTarget.value.replace(/\D/g,"").slice(0,3);
                     setFileSettings(current=>({...current,[fingerprint]:{...(current[fingerprint]??own),custom:true,copies}}));
                   }}
                   onBlur={event=>{
                     const copies=normalizeCustomerCopies(event.currentTarget.value);
                     setFileSettings(current=>({...current,[fingerprint]:{...(current[fingerprint]??own),custom:true,copies}}));
                   }}/></label>
                <div className="customer-choice-row">
                  <button type="button" className={own.colourMode==="Mono"?"customer-choice selected":"customer-choice"} onClick={()=>setFileSettings(current=>({...current,[fingerprint]:{...own,custom:true,colourMode:"Mono"}}))}>B&W</button>
                  <button type="button" className={own.colourMode==="Colour"?"customer-choice selected":"customer-choice"} onClick={()=>setFileSettings(current=>({...current,[fingerprint]:{...own,custom:true,colourMode:"Colour"}}))}>Colour</button>
                </div>
                <div className="customer-choice-row">
                  <button type="button" className={!own.isDuplex?"customer-choice selected":"customer-choice"} onClick={()=>setFileSettings(current=>({...current,[fingerprint]:{...own,custom:true,isDuplex:false}}))}>Single</button>
                  <button type="button" className={own.isDuplex?"customer-choice selected":"customer-choice"} onClick={()=>setFileSettings(current=>({...current,[fingerprint]:{...own,custom:true,isDuplex:true}}))}>Double</button>
                </div>
                <div><span>Paper</span><strong className="customer-paper-default">{own.advancedEnabled?own.paperSize:"A4"}</strong></div>
                <label className="customer-advanced-toggle"><input type="checkbox" checked={own.advancedEnabled} onChange={event=>{
                   const advancedEnabled=event.currentTarget.checked;
                   setFileSettings(current=>({...current,[fingerprint]:{...(current[fingerprint]??own),custom:true,advancedEnabled}}));
                 }}/><span>Advanced settings</span></label>
                {own.advancedEnabled&&<div className="customer-file-overrides customer-folder-advanced">
                  <label><span>Paper size</span><select value={own.paperSize} onChange={event=>{
                     const paperSize=event.currentTarget.value as CustomerFileSettings["paperSize"];
                     setFileSettings(current=>({...current,[fingerprint]:{...(current[fingerprint]??own),custom:true,paperSize}}));
                   }}>
                    <option value="A4">A4</option><option value="A3">A3</option><option value="A5">A5</option><option value="LETTER">Letter</option><option value="LEGAL">Legal</option>
                  </select></label>
                  <label><span>Orientation</span><select value={own.orientation} onChange={event=>{
                     const orientation=event.currentTarget.value as CustomerFileSettings["orientation"];
                     setFileSettings(current=>({...current,[fingerprint]:{...(current[fingerprint]??own),custom:true,orientation}}));
                   }}>
                    <option value="AUTO">Auto · follow document</option><option value="PORTRAIT">Portrait</option><option value="LANDSCAPE">Landscape</option>
                  </select></label>
                </div>}
              </div>}
            </div>;
          })}</div>
        </section>;
      })}</div>
    </div>}
    {files.length>0&&<label className="customer-batch-name-field">
  <span>Give this batch a name <small>Optional · the shop will see it in the print queue</small></span>
  <input type="text" value={batchName} maxLength={50}
    placeholder="e.g. Rahul – College Notes"
    disabled={busy||uploadedBatch.length>0}
    onChange={event=>setBatchName(event.target.value)}
  />
</label>}
{files.length>0&&<small className="customer-upload-local-note">Folder defaults apply to every file. Set an individual file to Custom for an exception.</small>}
    {previewUrl&&file&&<div className="customer-file-preview"><div className="customer-file-label"><FileText size={15}/><span>{photoSettingsByFileFingerprint[customerFileFingerprint(file)]?"Prepared photo sheet":"Selected document"} · {file.name}</span>{isCustomerImage(file)&&photoRatesAvailable&&<button type="button" className="customer-file-studio-button" disabled={busy||batchLockedForPayment||photoAlreadyUploaded} title={photoAlreadyUploaded?"This photo has already been submitted to the shop.":"Open Photo Studio"} onClick={()=>openPhotoStudio(file)}><ImageIcon size={14}/> {photoAlreadyUploaded?"Already sent to shop":"Open Photo Studio"}</button>}</div>{isCustomerImage(file)&&!photoRatesAvailable&&<small className="customer-upload-local-note">Photo printing is not enabled at this shop yet. Ask the shop owner to configure photo sizes and rates.</small>}{isCustomerImage(file)&&photoAlreadyUploaded&&photoRatesAvailable&&<small className="customer-upload-local-note">This photo is already with the shop. The shop owner can prepare it from the review queue without creating another print job.</small>}{isCustomerPdf(file)?photoSettingsByFileFingerprint[customerFileFingerprint(file)]?<div className="customer-private-photo-preview"><ShieldCheck size={20}/><strong>Photo sheet prepared for this shop</strong><span>It will be uploaded with your print request. The finished sheet is not available as a separate download.</span></div>:nativeAppMode?smartPrintNativePdfPreviewTokens.has(file)?<button type="button" className="customer-flow-button" onClick={()=>openCustomerPdfPreview(file)}><FileText size={16}/> Preview PDF</button>:<small className="customer-upload-local-note">PDF selected. Preview is unavailable for this file, but you can still upload it.</small>:<iframe src={previewUrl} title={file.name+" preview"}/>:<div className="customer-private-photo-preview"><ShieldCheck size={20}/><strong>Photo held in the shop workflow</strong><span>Open Photo Studio to view and prepare this image. The original is not displayed as a downloadable preview.</span></div>}</div>}
    {files.length>0&&<button className="customer-flow-button" disabled={busy||customerUploadUnavailable||pendingFilesCount===0||(uploadMode==="LOCAL"&&!localReady)} onClick={()=>void upload()}>{busy?<span className="button-spinner"/>:<><UploadIcon/>{uploadMode==="LOCAL"&&localAgent?"Upload "+pendingFilesCount+" file(s) to shop":"Upload "+pendingFilesCount+" file(s)"}</>}</button>}
    {uploadProgress&&<div className="customer-upload-progress"><span>{uploadProgress.currentFile?`Uploading ${Math.min(uploadProgress.completed+1,uploadProgress.total)} of ${uploadProgress.total}: ${uploadProgress.currentFile}`:`${uploadProgress.completed} of ${uploadProgress.total} files uploaded`}</span><div><i style={{width:`${uploadProgress.total?uploadProgress.completed/uploadProgress.total*100:0}%`}}/>{uploadProgress.currentFile&&<b aria-hidden="true"/>}</div><small>The count advances when each document finishes uploading.</small></div>}
    {files.length>0&&uploadMode==="LOCAL"&&localAgent&&<small className="customer-upload-local-note">{nativeAppMode?(localReady?"Direct shop Wi-Fi transfer uses local HTTP and is not encrypted in transit. Choose Cloud-assisted for HTTPS.":"Check shop Wi-Fi."):localReady?"Direct shop Wi-Fi transfer uses local HTTP and is not encrypted in transit. Choose Cloud-assisted for HTTPS encryption.":"Tap Check shop Wi-Fi above first to grant the browser permission."}</small>}
    {files.length>0&&uploadMode==="CLOUD"&&<small className="customer-upload-local-note">{nativeAppMode?"Encrypted HTTPS secure upload.":"Files are encrypted in transit and stored securely in the existing print queue until the shop computer is ready."}</small>}
    {uploaded&&<>
      {uploadedBatch.length>0&&<div className="customer-uploaded-batch">
        <div className="customer-uploaded-batch-head"><strong>{batchName.trim()?"Batch · "+batchName.trim():"Batch · "+uploadedBatch.length+" files"}</strong><span>{uploadedBatch[0]?.job.jobNumber?"First job · "+uploadedBatch[0].job.jobNumber+" · ":""}Select a file to view its status. Pay once for all accepted files.</span></div>
        {batchLockedForPayment&&<p className="customer-payment-model-note">This batch is locked because a payment attempt has started. Add more documents in a new batch to keep each checkout total accurate.</p>}
        {Array.from(new Set(uploadedBatch.map(item=>item.job.batchGroupName??"Loose files"))).map(groupName=><section className="customer-uploaded-folder" key={groupName}>
          <strong>{groupName}</strong>
          {uploadedBatch.filter(item=>(item.job.batchGroupName??"Loose files")===groupName).map(item=><button type="button" key={item.job.id} className={uploaded.job.id===item.job.id?"customer-uploaded-batch-item active":"customer-uploaded-batch-item"} onClick={()=>selectUploadedFile(item)}>
            <span className="batch-file-index"><FileText size={14}/></span><span className="batch-file-copy"><span className="batch-file-title-row"><strong>{item.document.originalFileName}</strong><PrintKindTag printKind={(photoSettingsByJobId[item.job.id]||batchStatuses[item.job.id]?.printKind==="PHOTO"||item.job.printKind==="PHOTO")?"PHOTO":"DOCUMENT"}/></span><small>{(photoSettingsByJobId[item.job.id]||batchStatuses[item.job.id]?.printKind==="PHOTO"||item.job.printKind==="PHOTO")?`${photoSettingsByJobId[item.job.id]?.photoCount??batchStatuses[item.job.id]?.photoCount??item.job.photoCount??0} photos · ${photoSettingsByJobId[item.job.id]?.sizeCode??batchStatuses[item.job.id]?.photoSizeCode??item.job.photoSizeCode??""} · ${item.job.pageCount} A4 pages`:`${item.job.pageCount} pages × ${printSettingsByJobId[item.job.id]?.copies??item.job.copies} copies = ${item.job.pageCount*(printSettingsByJobId[item.job.id]?.copies??item.job.copies)} pages requested · ${printSettingsByJobId[item.job.id]?.colourMode??"Mono"} · ${(printSettingsByJobId[item.job.id]?.isDuplex??false)?"Double-sided":"Single-sided"} · ${printSettingsByJobId[item.job.id]?.paperSize??item.job.paperSize??"A4"} · ${(printSettingsByJobId[item.job.id]?.orientation??item.job.orientation??"AUTO")==="LANDSCAPE"?"Landscape":(printSettingsByJobId[item.job.id]?.orientation??item.job.orientation??"AUTO")==="PORTRAIT"?"Portrait":"Auto orientation"}`} · {(batchStatuses[item.job.id]?.quotedAmountMinor??item.job.quotedAmountMinor)>0?formatMoney(batchStatuses[item.job.id]?.quotedAmountMinor??item.job.quotedAmountMinor,item.job.currency):"Awaiting quote"}</small></span>
            <Status
              label={quoteExpiredForBatchItem(item)&&!["REJECTED","REFUNDED"].includes(statusForBatchItem(item))?"Quote expired":prettyStatus(batchStatuses[item.job.id]?.status??item.job.status??"UPLOADED")}
              tone={quoteExpiredForBatchItem(item)&&!["REJECTED","REFUNDED"].includes(statusForBatchItem(item))?"amber":
                ["REJECTED","PAYMENT_REJECTED"].includes((batchStatuses[item.job.id]?.status??item.job.status??"").toUpperCase())?"red":
                ["COMPLETED","PAID_AUTHORISED","AWAITING_PAYMENT"].includes((batchStatuses[item.job.id]?.status??item.job.status??"").toUpperCase())?"green":"amber"}
            />
          </button>)}
        </section>)}
      </div>}
      <div className="uploaded-document-card"><div className="uploaded-document-icon"><FileText size={18}/></div><div><div className="uploaded-document-title"><strong>{uploaded.document.originalFileName}</strong><PrintKindTag printKind={(photoSettingsByJobId[uploaded.job.id]||batchStatuses[uploaded.job.id]?.printKind==="PHOTO"||uploaded.job.printKind==="PHOTO")?"PHOTO":"DOCUMENT"}/></div><span>{(photoSettingsByJobId[uploaded.job.id]||batchStatuses[uploaded.job.id]?.printKind==="PHOTO"||uploaded.job.printKind==="PHOTO")?`${photoSettingsByJobId[uploaded.job.id]?.photoCount??batchStatuses[uploaded.job.id]?.photoCount??uploaded.job.photoCount??0} photos · ${photoSettingsByJobId[uploaded.job.id]?.sizeCode??batchStatuses[uploaded.job.id]?.photoSizeCode??uploaded.job.photoSizeCode??""} · ${uploaded.job.pageCount} A4 pages`:`${uploaded.job.pageCount} ${uploaded.job.pageCount===1?"page":"pages"}`} · {formatBytes(uploaded.document.sizeBytes)} · Validated</span></div><Status label={prettyStatus(batchStatuses[uploaded.job.id]?.status??jobStatus?.status??uploaded.job.status??"UPLOADED")} tone={["REJECTED","PAYMENT_REJECTED"].includes((batchStatuses[uploaded.job.id]?.status??jobStatus?.status??uploaded.job.status??"").toUpperCase())?"red":["COMPLETED","PAID_AUTHORISED","AWAITING_PAYMENT"].includes((batchStatuses[uploaded.job.id]?.status??jobStatus?.status??uploaded.job.status??"").toUpperCase())?"green":"amber"}/></div>
      {batchId&&hasLegacyIndividualPayment&&<p className="customer-payment-warning">A file in this batch already has a separate individual payment. Complete or resolve that attempt before starting another payment for this batch. A second collection path is blocked to prevent duplicate charges.</p>}
      {canCalculateBatch&&<div className="customer-batch-quote-action">
        <div className="customer-flow-heading customer-config-heading"><div><span>STEP 2</span><h2>Review batch settings</h2></div><small>{quoteItems.length} files</small></div>
        <button className="customer-flow-button" disabled={busy} onClick={()=>void getQuote()}>{busy?<span className="button-spinner"/>:"Calculate batch quote"}</button>
      </div>}
    </>}
    {error&&<div className="customer-flow-error" role="alert">{error}</div>}
    {quoteEntries.length>0&&<div className="customer-quote-card customer-batch-quote-card">
      <div><span>BATCH QUOTE</span><strong>{formatMoney(batchQuoteTotal,quoteEntries[0].quote!.currency)}</strong><small>{quoteEntries.length} files · {batchDocumentSourcePages} document pages · {batchPhotoCount} photos · {batchPrintedPages} output pages/sheets</small></div>
      <div className="customer-batch-quote-lines">{quoteEntries.map(entry=><div key={entry.item.job.id}><span><span className="customer-quote-line-title">{entry.item.document.originalFileName}<PrintKindTag printKind={entry.quote!.printKind}/>{["REJECTED","REFUNDED"].includes(statusForBatchItem(entry.item))?" · Rejected":""}</span><small>{entry.quote!.printKind==="PHOTO"?(entry.quote!.photoCount??0)+" × "+customerPhotoSizeLabel(entry.quote!.photoSizeCode)+" · "+formatMoney(entry.quote!.photoUnitPriceMinor??0,entry.quote!.currency)+" per photo":entry.quote!.pageCount+" pages × "+entry.quote!.copies+" copies · "+entry.quote!.colourMode+" · "+(entry.quote!.isDuplex?"Double-sided":"Single-sided")+" · "+entry.quote!.paperSize+" · "+(entry.quote!.orientation==="LANDSCAPE"?"Landscape":entry.quote!.orientation==="PORTRAIT"?"Portrait":"Auto orientation")}</small></span><strong>{["REJECTED","REFUNDED"].includes(statusForBatchItem(entry.item))?"Excluded":formatMoney(entry.quote!.quotedAmountMinor,entry.quote!.currency)}</strong></div>)}</div>
      <div className="customer-batch-print-breakdown">
        {batchMonoPrintedPages>0&&<div><span>B&W · {batchMonoPrintedPages} document pages</span><strong>{formatMoney(batchMonoAmount,quoteEntries[0].quote!.currency)}</strong></div>}
        {batchColourPrintedPages>0&&<div><span>Colour · {batchColourPrintedPages} document pages</span><strong>{formatMoney(batchColourAmount,quoteEntries[0].quote!.currency)}</strong></div>}
        {batchPhotoCount>0&&<div><span>Photo printing · {batchPhotoCount} finished photos</span><strong>{formatMoney(batchPhotoAmount,quoteEntries[0].quote!.currency)}</strong></div>}
        <div className="customer-batch-print-total"><span>Total output pages/sheets</span><strong>{batchPrintedPages}</strong></div>
        <div className="customer-batch-print-total"><span>Total payable</span><strong>{formatMoney(batchQuoteTotal,quoteEntries[0].quote!.currency)}</strong></div>
      </div>
      <p className="customer-payment-model-note">Document pages use the shop's B&W/Colour rate card. Photo sheets use the saved per-photo rate for the selected size, shown on each line. Rejected files are excluded. After review, pay once for all accepted files.</p>
    </div>}
    {canSendBatch&&<button className="customer-flow-button" disabled={busy} onClick={()=>void acceptQuote()}>{busy?<span className="button-spinner"/>:"Send batch to shop"}</button>}
      {jobStatus?.status==="QUOTE_EXPIRED"&&<button className="customer-flow-button" disabled={busy} onClick={()=>{setQuote(null);setQuotesByJobId(current=>{const next={...current};if(uploaded)delete next[uploaded.job.id];return next;});void getQuote(uploadedBatch.length?uploadedBatch:uploaded?[uploaded]:[]);}}>Request a fresh quote</button>}
    {jobStatus&&<div className="customer-job-state"><div className="customer-job-state-top"><Status label={prettyStatus(jobStatus.status)} tone={jobStatus.status==="REJECTED"?"red":jobStatus.status==="AWAITING_PAYMENT"?"green":"amber"}/><button className="customer-refresh-status" disabled={busy} onClick={()=>void refreshJobStatus()}>{busy?<span className="button-spinner"/>:"Refresh status"}</button></div>
      <strong>{nativeAppMode?smartPrintNativeHeadline(jobStatus.status):jobStatus.status==="AWAITING_SHOP_APPROVAL"?"Your request is with the shop":jobStatus.status==="AWAITING_PAYMENT"?"Your quote is ready for payment":jobStatus.status==="LOCAL_QUEUE_CLAIMED"?"Your document is reaching the shop":jobStatus.status==="LOCAL_QUEUE_READY"?"Your document is secured at the shop":jobStatus.status==="PRINT_AUTHORISED"?"The shop started printing":jobStatus.status==="PRINT_SUBMITTING"?"Your document is being sent to the printer":jobStatus.status==="SPOOLER_ACCEPTED"?"The printer queue accepted your document":jobStatus.status==="PRINTING"?"Your document is printing":jobStatus.status==="OUTPUT_UNCONFIRMED"?"The shop is checking your print":jobStatus.status==="COMPLETED"?"Your print is ready":jobStatus.status==="PAID_AUTHORISED"?"Your payment is verified":jobStatus.status==="REJECTED"?"The shop could not accept this request":jobStatus.status==="QUOTE_EXPIRED"?"Your quote has expired":"Your request is being reviewed"}</strong>
      <p>{nativeAppMode ? smartPrintNativeStatus(jobStatus.status) : jobStatus.status==="AWAITING_SHOP_APPROVAL"?"The shop will review your request next. No payment has been collected.":jobStatus.status==="AWAITING_PAYMENT"?"Your quote is ready. Choose one of the payment methods enabled by the shop; the shop must verify payment before authorising the job.":jobStatus.status==="LOCAL_QUEUE_CLAIMED"?"Your paid document is being transferred securely to the shop's Windows computer. It has not been sent to the printer.":jobStatus.status==="LOCAL_QUEUE_READY"?"The document is safely stored in the shop's local queue and is waiting for the print operator. Physical printing has not started.":jobStatus.status==="PRINT_AUTHORISED"?"The shop has authorised printing. The Windows agent will submit the document once.":jobStatus.status==="PRINT_SUBMITTING"?"The Windows agent is submitting your document to the printer.":jobStatus.status==="SPOOLER_ACCEPTED"?"Windows accepted the job into the printer queue. This does not yet confirm paper output.":jobStatus.status==="PRINTING"?"The printer driver reports that the job is printing. The shop will confirm the pages before marking it complete.":jobStatus.status==="OUTPUT_UNCONFIRMED"?"The printer queue could not confirm the final output. The shop operator is checking the pages before any retry.":jobStatus.status==="COMPLETED"?"The shop has checked the physical pages and marked your print ready for collection.":jobStatus.status==="AWAITING_COUNTER_PAYMENT"?"Please pay the shop at the counter. The job is not authorised until the shop confirms receipt.":jobStatus.status==="AWAITING_CUSTOMER_PAYMENT"?"Complete the UPI payment and submit its transaction reference. The shop must independently verify it.":jobStatus.status==="PAYMENT_REPORTED"?"Your UPI reference was sent to the shop. It is not marked paid until the shop verifies the transaction.":jobStatus.status==="PAID_AUTHORISED"?"The shop has verified your payment. The print queue is being prepared; physical printing has not yet started.":jobStatus.status==="PAYMENT_REJECTED"?"The shop could not verify that payment. Check with the shop before trying another method.":jobStatus.status==="REJECTED"?"Please speak with the shop operator if you need help with this request.":jobStatus.status==="QUOTE_EXPIRED"?"This quote is no longer valid. Please ask the shop for a fresh quote.":"Check back for the latest request status."}</p>
      {payment&&!!payment.refundedAmountMinor&&payment.refundedAmountMinor>0&&<div className="customer-refund-note"><Wallet size={16}/><span><strong>{formatMoney(payment.refundedAmountMinor,payment.currency)} returned</strong> for rejected pages ({rejectedPageList(jobStatus.rejectedPageNumbersJson)}). {formatMoney(Math.max(0,payment.amountMinor-payment.refundedAmountMinor),payment.currency)} remains paid; only accepted pages proceed to printing.</span></div>}
      {jobStatus.pdfBrandingEnabled&&<div className="customer-branding-note">
        <ShieldCheck size={16}/>
        <span><strong>PDF footer selected by the shop.</strong> <InfoTooltip label="PDF footer details"><span>Footer only appears on PDFs with a verified blank bottom margin.</span><span>Digitally signed or interactive PDFs remain unchanged.</span><span>Original uploads and physical prints are never altered.</span><span>Image-to-PDF exports currently remain unbranded.</span><span>Branding off preserves a single-copy full-document PDF byte-for-byte.</span><span>Reformatted or multi-copy exports may be flattened.</span><span>Ask for an unbranded single-copy export when PDF structure matters.</span></InfoTooltip></span>
      </div>}
      {!!jobStatus.timeline?.length&&<div className="customer-state-timeline">{jobStatus.timeline.map((event,index)=><div key={index}><i/><span><strong>{prettyStatus(event.newStatus)}</strong><small>{event.actorType==="ShopUser"?"Shop team":event.actorType==="Customer"?"You":"System"} · {formatIstDateTime(event.createdUtc)}</small></span></div>)}</div>}

      {legacyIndividualPayment&&(jobStatus.status==="AWAITING_PAYMENT"||jobStatus.status==="PAYMENT_REJECTED")&&<div className="customer-pay-panel">
        <div className="customer-pay-heading"><span>PAY THE SHOP</span><strong>{formatMoney(jobStatus.quotedAmountMinor,jobStatus.currency)}</strong></div>
        {!paymentOptions.cashEnabled&&paymentOptions.upiEnabled&&<small className="customer-payment-method-note">Cash at counter unavailable. <InfoTooltip label="Why cash is unavailable"><span>The shop must enable Cash at counter in Payments settings.</span></InfoTooltip></small>}
        {!paymentOptions.cashEnabled&&!paymentOptions.upiEnabled&&<small className="customer-payment-method-note">The shop has not enabled UPI or cash payment. Please contact the shop to arrange payment.</small>}
        {paymentOptions.upiEnabled&&<button className={paymentMethod==="UPI"?"customer-choice selected":"customer-choice"} onClick={()=>setPaymentMethod("UPI")}>UPI payment</button>}
        {paymentOptions.cashEnabled&&<button className={paymentMethod==="CASH"?"customer-choice selected":"customer-choice"} onClick={()=>setPaymentMethod("CASH")}>Cash at counter</button>}
        <button className="customer-flow-button" disabled={busy||(!paymentOptions.upiEnabled&&!paymentOptions.cashEnabled)} onClick={()=>void beginPayment(paymentMethod)}>{busy?<span className="button-spinner"/>:nativeAppMode?"Continue":"Continue with "+(paymentMethod==="UPI"?"UPI":"cash at counter")}</button>
        {!paymentOptions.upiEnabled&&!paymentOptions.cashEnabled&&<p className="customer-flow-error">{nativeAppMode?"No payment method. Ask shop.":"The shop has not configured a payment method yet. Please speak to the shop operator."}</p>}
      </div>}
      {legacyIndividualPayment&&payment&&payment.status!=="REJECTED"&&<div className="customer-pay-panel">
        <div className="customer-pay-heading"><span>{payment.method==="UPI"?"UPI PAYMENT":"CASH AT COUNTER"}</span><strong>{formatMoney(Math.max(0,payment.amountMinor-(payment.refundedAmountMinor??0)),payment.currency)}</strong></div>
        {payment.method==="UPI"&&payment.status==="AWAITING_CUSTOMER_PAYMENT"&&<>
          <CustomerUpiQr
            uri={buildCustomerUpiIntent({
              serverUri:payment.upiUri,upiId:payment.upiIdSnapshot||paymentOptions.upiId,
              payeeName:payment.payeeNameSnapshot||paymentOptions.payeeName,amountMinor:payment.amountMinor,
              currency:payment.currency,transactionReference:payment.id,
              description:"SmartPrint "+(uploaded?.job.jobNumber||"print payment")
            })}
            payeeName={payment.payeeNameSnapshot||paymentOptions.payeeName||"Shop"}
            upiId={payment.upiIdSnapshot||paymentOptions.upiId||""}
            amount={formatMoney(payment.amountMinor,payment.currency)}
            fileName={"smartprint-payment-"+payment.id.slice(0,12)+".png"}
          />
          <div className="customer-payment-reference">
            <label className="customer-reference-field">{nativeAppMode?"UTR":"UPI transaction reference / UTR"}<input value={paymentReference} maxLength={100} onChange={event=>setPaymentReference(event.target.value)} placeholder={nativeAppMode?"Enter UTR":"Enter UTR after paying"}/></label>
            <button className="customer-flow-button" disabled={busy||paymentReference.trim().length<4} onClick={()=>void submitPaymentReference()}>{busy?<span className="button-spinner"/>:nativeAppMode?"Submit UTR":"Submit payment reference"}</button>
            <small className="customer-payment-warning">UPI transfers can still be declined by the bank or UPI provider. A downloaded QR offers a scan-from-image route; it does not bypass payment security checks.</small>
          </div>
        </>}
        {payment.method==="CASH"&&payment.status==="CASH_AWAITING_PAYMENT"&&<p>{nativeAppMode?"Pay at counter · Await confirmation.":"Pay at the shop counter. The shop will confirm receipt before authorising the job."}</p>}
        {payment.status==="CUSTOMER_REPORTED"&&<p>{nativeAppMode?"Reference received · Verification pending.":"Your payment reference was received. The shop still needs to verify the transaction."}</p>}
        {payment.status==="VERIFIED"&&<p>{nativeAppMode?"Payment verified · Printing pending.":"Payment verified by the shop. The print job is authorised, but not yet physically printed."}</p>}
        {payment.status==="PARTIALLY_REFUNDED"&&<p>{nativeAppMode?"Partial refund · Accepted pages only.":"A partial return has been recorded. The remaining paid amount is for the pages the shop accepted."}</p>}
      </div>}
    </div>}
    {batchCheckoutVisible&&<section className="customer-pay-panel customer-batch-checkout-panel">
      <div className="customer-pay-heading">
        <span>{batchCheckoutHeading} · {batchCheckoutFileCount} FILE(S)</span>
        <strong>{formatMoney(batchCheckoutAmount,batchCheckoutCurrency)}</strong>
      </div>
      <div className="customer-batch-checkout-lines">
        {batchCheckoutLineItems.map(item=>{
          const allocation=customerPaymentForBatchItem(item);
          const amount=batchCheckoutUsesPayment
            ?allocation?.amountMinor??0
            :batchStatuses[item.job.id]?.quotedAmountMinor??quotesByJobId[item.job.id]?.quotedAmountMinor??item.job.quotedAmountMinor??0;
          const refunded=batchCheckoutUsesPayment?(allocation?.refundedAmountMinor??0):0;
          const lineStatus=batchCheckoutUsesPayment?allocation?.status:"Included in this checkout";
          return <div key={item.job.id}>
            <span>{item.document.originalFileName}<small>{lineStatus?String(lineStatus).replaceAll("_"," "):"Allocation loading"}{refunded>0? ` · ${formatMoney(refunded,item.job.currency)} refunded` : ""}</small></span>
            <strong>{formatMoney(amount,item.job.currency)}</strong>
          </div>;
        })}
      </div>
      {batchCheckoutUsesPayment&&!batchPaymentAllocationComplete&&<p className="customer-payment-warning">Refreshing the per-file payment breakdown. Check the complete file list before paying or submitting a reference.</p>}
      {batchCheckoutUsesPayment&&batchPaymentAllocationComplete&&batchPaymentRefundedTotal>0&&<p className="customer-payment-warning">Recorded file refunds: {formatMoney(batchPaymentRefundedTotal,batchCheckoutCurrency)}. Remaining amount after those returns: {formatMoney(Math.max(0,batchPayment!.amountMinor-batchPaymentRefundedTotal),batchCheckoutCurrency)}.</p>}
      {!batchCheckoutUsesPayment&&<>
        {batchPayment?.status==="REJECTED"&&<p className="customer-flow-error">The previous batch payment was rejected. {batchPayment.rejectionReason||"Check with the shop before trying again."}</p>}
        {!paymentOptions.cashEnabled&&paymentOptions.upiEnabled&&<small className="customer-payment-method-note">Cash at counter unavailable. <InfoTooltip label="Why cash is unavailable"><span>The shop must enable Cash at counter in Payments settings.</span></InfoTooltip></small>}
        {!paymentOptions.cashEnabled&&!paymentOptions.upiEnabled&&<small className="customer-payment-method-note">The shop has not enabled UPI or cash payment. Please contact the shop to arrange payment.</small>}
        {paymentOptions.upiEnabled&&<button className={paymentMethod==="UPI"?"customer-choice selected":"customer-choice"} onClick={()=>setPaymentMethod("UPI")}>UPI payment</button>}
        {paymentOptions.cashEnabled&&<button className={paymentMethod==="CASH"?"customer-choice selected":"customer-choice"} onClick={()=>setPaymentMethod("CASH")}>Cash at counter</button>}
        <button className="customer-flow-button" disabled={busy||(!paymentOptions.upiEnabled&&!paymentOptions.cashEnabled)||batchPayableItems.length===0} onClick={()=>void beginPayment(paymentMethod,true)}>
          {busy?<span className="button-spinner"/>:nativeAppMode?"Pay accepted files":"Pay accepted files · "+formatMoney(batchPayableTotal,batchCheckoutCurrency)}
        </button>
        {!paymentOptions.upiEnabled&&!paymentOptions.cashEnabled&&<p className="customer-flow-error">{nativeAppMode?"No payment method. Ask shop.":"The shop has not configured a payment method yet. Please speak to the shop operator."}</p>}
      </>}
      {batchCheckoutUsesPayment&&batchPaymentAllocationComplete&&batchPayment?.method==="UPI"&&batchPayment.status==="AWAITING_CUSTOMER_PAYMENT"&&<>
        <CustomerUpiQr
          uri={buildCustomerUpiIntent({
            serverUri:batchPayment.upiUri,upiId:batchPayment.upiIdSnapshot||paymentOptions.upiId,
            payeeName:batchPayment.payeeNameSnapshot||paymentOptions.payeeName,amountMinor:batchPayment.amountMinor,
            currency:batchPayment.currency,transactionReference:batchPayment.id,
            description:"SmartPrint batch "+batchPayment.batchId.slice(0,8).toUpperCase()
          })}
          payeeName={batchPayment.payeeNameSnapshot||paymentOptions.payeeName||"Shop"}
          upiId={batchPayment.upiIdSnapshot||paymentOptions.upiId||""}
          amount={formatMoney(batchPayment.amountMinor,batchPayment.currency)}
          fileName={"smartprint-batch-payment-"+batchPayment.id.slice(0,12)+".png"}
        />
        <div className="customer-payment-reference">
          <label className="customer-reference-field">{nativeAppMode?"UTR":"UPI transaction reference / UTR"}<input value={paymentReference} maxLength={100} onChange={event=>setPaymentReference(event.target.value)} placeholder={nativeAppMode?"Enter UTR":"Enter UTR after paying"}/></label>
          <button className="customer-flow-button" disabled={busy||paymentReference.trim().length<4} onClick={()=>void submitPaymentReference()}>{busy?<span className="button-spinner"/>:nativeAppMode?"Submit UTR":"Submit batch payment reference"}</button>
          <small className="customer-payment-warning">The shop verifies the actual UPI credit. A downloaded QR offers an alternative scan route; it cannot override a bank or UPI security decline.</small>
        </div>
      </>}
      {batchCheckoutUsesPayment&&batchPaymentAllocationComplete&&batchPayment?.method==="CASH"&&batchPayment.status==="CASH_AWAITING_PAYMENT"&&<p>Pay the displayed batch total at the shop counter. The shop will confirm receipt before authorising the accepted files.</p>}
      {batchCheckoutUsesPayment&&batchPayment?.status==="CUSTOMER_REPORTED"&&<p>Your batch payment reference was received. The shop must verify the full amount before any accepted file is authorised.</p>}
      {batchCheckoutUsesPayment&&batchPayment?.status==="VERIFIED"&&<div className="payment-verified-note"><Check size={14}/> Batch payment verified. Accepted files are authorised for the shop's print queue.</div>}
    </section>}
    <div className="customer-payment-boundary"><ShieldCheck size={15}/><span>{nativeAppMode ? smartPrintNativeBoundary(jobStatus?.status,Boolean(quote)) : jobStatus?.status==="PAID_AUTHORISED"?"The shop has verified your payment. It still needs to assign and start the local printer.":jobStatus?.status==="PRINT_AUTHORISED"?"The shop has authorised physical printing.":jobStatus?.status==="PRINT_SUBMITTING"?"Your document is being sent to the Windows printer queue.":jobStatus?.status==="SPOOLER_ACCEPTED"?"Windows accepted the job. This is not proof that paper has come out.":jobStatus?.status==="PRINTING"?"The Windows driver reports that printing is in progress.":jobStatus?.status==="OUTPUT_UNCONFIRMED"?"The shop is checking the printer and actual pages before any retry.":jobStatus?.status==="COMPLETED"?"The shop confirmed the requested pages are printed and ready for collection.":jobStatus?.status==="LOCAL_QUEUE_CLAIMED"?"The shop's paired Windows computer is receiving the encrypted document. It is not being printed yet.":jobStatus?.status==="LOCAL_QUEUE_READY"?"The encrypted local copy is verified on the shop computer. Physical printing still requires the print operator.":jobStatus?.status==="AWAITING_PAYMENT"?"Your quote is ready for payment. The shop collects payment directly and must verify it before the job is authorised.":jobStatus?.status==="PAYMENT_REPORTED"?"Your UPI reference is awaiting the shop's independent bank verification; submitting a reference does not confirm payment.":quote?"This quote is calculated by the shop's saved rate card. Accepting it sends a request to the shop; it does not collect payment or start printing.":"Your document is validated before pricing. You will see the shop's calculated price before any payment or print approval."}</span></div>
  </section></>;
}


function UploadIcon() {
  return <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M12 16V4m0 0L7 9m5-5 5 5"/><path d="M4 16v3a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-3"/></svg>;
}
