import { useCustomerPrintFlow } from "./useCustomerPrintFlow";
import {
  Check, FileText, FileUp, FolderOpen, Image as ImageIcon, ShieldCheck, Wallet, X
} from "lucide-react";
import PhotoStudio from "../../PhotoStudio";
import CustomerUpiQr from "../../CustomerUpiQr";
import { buildCustomerUpiIntent } from "../../upi-payment-intent";
import Status from "../../shared/ui/Status";
import PrintKindTag from "../../shared/ui/PrintKindTag";
import { formatMoney, prettyStatus } from "../../shared/lib/formatting";
import InfoTooltip from "../../shared/ui/InfoTooltip";
import { formatIstDateTime } from "../../date-time";
import {
  rejectedPageList, formatBytes, consumeSmartPrintNativePickerGroups, customerPhotoSizeLabel,
  isCustomerImage, customerFileFingerprint, isCustomerPdf, smartPrintNativePdfPreviewTokens,
  consumeSmartPrintNativePdfPreviews, normalizeCustomerCopies, smartPrintNativeHeadline,
  smartPrintNativeStatus, smartPrintNativeBoundary, type CustomerGroupSettings,
  type CustomerFileSettings, type CustomerPrintFlowProps
} from "./customer-print-utils";

export default function CustomerPrintFlow(props:CustomerPrintFlowProps) {
  const {dark,localAgent,cloudAssistedPrintingEnabled,localReady}=props;
  const {
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
  } = useCustomerPrintFlow(props);
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

