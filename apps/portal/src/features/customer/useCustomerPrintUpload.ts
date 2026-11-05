import { useState } from "react";
import type { Dispatch, SetStateAction } from "react";
import {
  ApiError, LocalUploadOutcomeUnknownError, LocalUploadPreflightError,
  uploadCustomerDocument, uploadCustomerDocumentLocally,
  type UploadedPrintJob, type CustomerPrintRequestSettings
} from "../../api";
import { canFallbackToCloudFromLocalFailure } from "../../local-upload-routing";
import {
  customerFileFingerprint, isCustomerPdf, smartPrintNativePdfPreviewTokens, postSmartPrintNativeMessage,
  customerBatchGroupName, type CustomerLocalAgent, type CustomerGroupSettings,
  type CustomerFileSettings, type CustomerSavedPrintSettings, type CustomerPhotoSettings
} from "./customer-print-utils";
import type { PreparedPhotoSheet } from "../../PhotoStudio";

type StateSetter<T> = Dispatch<SetStateAction<T>>;
type CustomerPrintUploadOptions = {
  sessionToken: string;
  nativeAppMode: boolean;
  files: File[];
  setFiles: StateSetter<File[]>;
  selectedFileIndex: number;
  setSelectedFileIndex: StateSetter<number>;
  uploaded: UploadedPrintJob | null;
  setUploaded: StateSetter<UploadedPrintJob | null>;
  uploadedBatch: UploadedPrintJob[];
  setUploadedBatch: StateSetter<UploadedPrintJob[]>;
  uploadedFileFingerprints: string[];
  setUploadedFileFingerprints: StateSetter<string[]>;
  batchId: string;
  setBatchId: StateSetter<string>;
  batchName: string;
  setBatchName: StateSetter<string>;
  printSettingsByJobId: Record<string, CustomerSavedPrintSettings>;
  setPrintSettingsByJobId: StateSetter<Record<string, CustomerSavedPrintSettings>>;
  photoSettingsByJobId: Record<string, CustomerPhotoSettings>;
  setPhotoSettingsByJobId: StateSetter<Record<string, CustomerPhotoSettings>>;
  photoSettingsByFileFingerprint: Record<string, CustomerPhotoSettings>;
  setPhotoSettingsByFileFingerprint: StateSetter<Record<string, CustomerPhotoSettings>>;
  fileGroupNames: Record<string, string>;
  setFileGroupNames: StateSetter<Record<string, string>>;
  groupSettings: Record<string, CustomerGroupSettings>;
  setGroupSettings: StateSetter<Record<string, CustomerGroupSettings>>;
  fileSettings: Record<string, CustomerFileSettings>;
  setFileSettings: StateSetter<Record<string, CustomerFileSettings>>;
  photoStudioSourceFingerprint: string | null;
  setPhotoStudioSource: StateSetter<File | null>;
  setPhotoStudioSourceFingerprint: StateSetter<string | null>;
  uploadMode: "LOCAL" | "CLOUD";
  setUploadMode: StateSetter<"LOCAL" | "CLOUD">;
  setUploadModeManuallySelected: StateSetter<boolean>;
  secureQueueFallbackEnabled: boolean;
  cloudAssistedPrintingEnabled: boolean;
  localAgent: CustomerLocalAgent | null;
  localReady: boolean;
  paymentOptions: { instantPaymentEnabled: boolean };
  photoRatesAvailable: boolean;
  batchLockedForPayment: boolean;
  busy: boolean;
  setBusy: StateSetter<boolean>;
  setError: StateSetter<string>;
  setQuote: StateSetter<import("../../api").PrintQuoteResult | null>;
  setQuotesByJobId: StateSetter<Record<string, import("../../api").PrintQuoteResult>>;
  setJobStatus: StateSetter<import("../../api").CustomerJobStatus | null>;
  setPayment: StateSetter<import("../../api").CustomerPayment | null>;
  setPaymentReference: StateSetter<string>;
  groupNameForFile: (file: File) => string;
  defaultsForGroup: (name: string) => CustomerGroupSettings;
  settingsForFile: (file: File) => CustomerSavedPrintSettings;
  getQuote: (itemsOverride?: UploadedPrintJob[], settingsOverride?: Record<string, CustomerSavedPrintSettings>, photoSettingsOverride?: Record<string, CustomerPhotoSettings>) => Promise<void>;
};

export function useCustomerPrintUpload(options: CustomerPrintUploadOptions) {
  const {
    sessionToken, nativeAppMode, files, setFiles, selectedFileIndex, setSelectedFileIndex,
    uploaded, setUploaded, uploadedBatch, setUploadedBatch, uploadedFileFingerprints, setUploadedFileFingerprints,
    batchId, setBatchId, batchName, setBatchName, printSettingsByJobId, setPrintSettingsByJobId,
    photoSettingsByJobId, setPhotoSettingsByJobId, photoSettingsByFileFingerprint, setPhotoSettingsByFileFingerprint,
    fileGroupNames, setFileGroupNames, groupSettings, setGroupSettings, fileSettings, setFileSettings,
    photoStudioSourceFingerprint, setPhotoStudioSource, setPhotoStudioSourceFingerprint, uploadMode, setUploadMode,
    setUploadModeManuallySelected, secureQueueFallbackEnabled, cloudAssistedPrintingEnabled, localAgent, localReady,
    paymentOptions, photoRatesAvailable, batchLockedForPayment, busy, setBusy, setError, setQuote,
    setQuotesByJobId, setJobStatus, setPayment, setPaymentReference, groupNameForFile, defaultsForGroup,
    settingsForFile, getQuote
  } = options;
  const [uploadProgress, setUploadProgress] = useState<{completed:number;total:number;currentFile:string|null}|null>(null);

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

  return {
    upload, uploadProgress, applyPhotoSheets, openPhotoStudio, openCustomerPdfPreview,
    chooseFiles, removeSelectedFile
  };
}
