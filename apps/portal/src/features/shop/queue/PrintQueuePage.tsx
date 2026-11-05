import { Fragment, useEffect, useRef, useState } from "react";
import { Activity, ArrowRight, FileText, Printer, Search } from "lucide-react";
import type { AgentDevice, CustomerPrintRequestSettings, PrinterDevice, ShopPrintJob } from "../../../api";
import { formatIstDateTime } from "../../../date-time";
import { formatMoney, prettyStatus } from "../../../shared/lib/formatting";
import EmptyModule from "../../../shared/ui/EmptyModule";
import InfoTooltip from "../../../shared/ui/InfoTooltip";
import PrintKindTag from "../../../shared/ui/PrintKindTag";
import Status from "../../../shared/ui/Status";
import { isPrinterAvailable } from "../../../shared/lib/printers";
import { verifiedPdfExportPath } from "../../../print-output";

function spoolerStatusWarning(status?: string | null, printerStatus?: string | null) {
  const spooler = (status || "").toUpperCase();
  const printer = (printerStatus || "").toUpperCase();
  if (spooler === "OFFLINE" || printer === "OFFLINE")
    return "Printer is offline. Reconnect it; Windows may resume this same queued job. Do not start a duplicate.";
  if (spooler === "PAPEROUT")
    return "The printer reports no paper. Refill it and check the same Windows queue before continuing.";
  if (spooler === "PAUSED" || printer === "PAUSED")
    return "The printer or Windows print queue is paused. Resume the existing job in Windows; do not submit another copy.";
  if (spooler === "PRINTER_UNAVAILABLE")
    return "The Agent cannot currently open this printer queue. Reconnect or restore the queue, then refresh this job.";
  if (spooler === "ERROR")
    return "Windows reports a printer error. Resolve it in the printer queue and inspect any pages already produced.";
  if (spooler === "USERINTERVENTION")
    return "The printer requires attention. Resolve the prompt at the device before checking the same queued job.";
  return "";
}

function printBlockMessage(code:string):string {
  switch(code) {
    case "PAPER_SIZE_UNSUPPORTED":
    case "PAPER_SIZE_REJECTED":
      return "The selected printer did not confirm the requested paper size.";
    case "LANDSCAPE_UNSUPPORTED":
    case "ORIENTATION_UNCONFIRMED":
      return "The selected printer did not confirm the requested page orientation.";
    case "COLOUR_UNSUPPORTED":
    case "COLOUR_REJECTED":
      return "The selected printer did not confirm colour output.";
    case "DUPLEX_UNSUPPORTED":
    case "DUPLEX_REJECTED":
      return "The selected printer did not confirm double-sided output.";
    default:
      return "The Windows Agent could not confirm the requested settings with this output device.";
  }
}

export interface PrintQueuePageProps {
  jobs: ShopPrintJob[];
  printers: PrinterDevice[];
  agentDevices: AgentDevice[];
  searchQuery: string;
  loadingBatchId: string | null;
  onSearch: (query: string) => Promise<void>;
  onPreview: (job: ShopPrintJob) => void;
  onPreviewOutput: (job: ShopPrintJob) => void;
  onApprove: (job: ShopPrintJob) => void;
  onReject: (job: ShopPrintJob) => void;
  onRefund: (job: ShopPrintJob) => void;
  onReviewBatch: (batchId: string) => void;
  onHistory: (job: ShopPrintJob) => void;
  onAssignPrinter: (job: ShopPrintJob, printerDeviceId: string) => void;
  onUpdateSettings: (job: ShopPrintJob, settings: CustomerPrintRequestSettings) => Promise<void> | void;
  onDispatch: (job: ShopPrintJob) => void;
  onConfirmOutput: (job: ShopPrintJob) => void;
  onRetryPdfExport: (job: ShopPrintJob) => void;
  onRefresh: () => Promise<void> | void;
  onLoadOlder: () => Promise<void> | void;
  hasMoreJobs: boolean;
  loadingOlderJobs: boolean;
  busyJobId: string | null;
}

export default function PrintQueuePage({ jobs, printers, agentDevices, searchQuery, loadingBatchId, onSearch, onPreview, onPreviewOutput, onApprove, onReject, onRefund, onReviewBatch, onHistory, onAssignPrinter, onUpdateSettings, onDispatch, onConfirmOutput, onRetryPdfExport, onRefresh, onLoadOlder, hasMoreJobs, loadingOlderJobs, busyJobId }: PrintQueuePageProps) {
  const [autoRefresh,setAutoRefresh]=useState(true);
  const [refreshSeconds,setRefreshSeconds]=useState(10);
  const [searchInput,setSearchInput]=useState(searchQuery);
  const [expandedBatchKeys,setExpandedBatchKeys]=useState<Set<string>>(()=>new Set());
  const [editingSettingsJobId,setEditingSettingsJobId]=useState<string|null>(null);
  const [editingPrintSettings,setEditingPrintSettings]=useState<CustomerPrintRequestSettings|null>(null);
  useEffect(()=>setSearchInput(searchQuery),[searchQuery]);
  const refreshRunning=useRef(false);
  const refreshCallback=useRef(onRefresh);
  refreshCallback.current=onRefresh;

  async function refreshQueue() {
    if(refreshRunning.current)return;
    refreshRunning.current=true;
    try { await refreshCallback.current(); }
    finally { refreshRunning.current=false; }
  }

  useEffect(()=>{
    if(!autoRefresh)return;
    const timer=window.setInterval(()=>void refreshQueue(),Math.max(5,refreshSeconds)*1000);
    return ()=>window.clearInterval(timer);
  },[autoRefresh,refreshSeconds]);

  const reviewableBatchFileStatuses=new Set(["AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT","PAYMENT_REJECTED","REJECTED"]);
  const reviewBatches=Array.from(new Map(jobs.filter(job=>job.batchId&&reviewableBatchFileStatuses.has(job.status)).map(job=>[job.batchId!,job])).keys());
  const onlineAgentIds = new Set(agentDevices.filter(agent =>
    !agent.revokedUtc && agent.status === "Online" && !!agent.lastSeenUtc &&
    Date.parse(agent.lastSeenUtc) > Date.now() - 2 * 60 * 1000
  ).map(agent => agent.id));
  const dispatchablePrinters = printers.filter(printer =>
    isPrinterAvailable(printer, onlineAgentIds)
  );
  const batchMap=new Map<string,{key:string;batchId:string|null;jobs:ShopPrintJob[]}>();
  for(const job of jobs) {
    const key=job.batchId??`single:${job.id}`;
    const group=batchMap.get(key)??{key,batchId:job.batchId??null,jobs:[]};
    group.jobs.push(job);
    batchMap.set(key,group);
  }
  const queueBatches=Array.from(batchMap.values());

  return <section className="panel queue-panel live-queue-panel">
    <div className="collection-header"><div><h2>Customer print jobs</h2><p>Review files individually; one printer is used for the whole batch.</p></div><div className="queue-header-actions"><span className="setup-count">{queueBatches.length} batches shown</span><form className="queue-search-form" onSubmit={event=>{event.preventDefault();void onSearch(searchInput);}}><input aria-label="Search all print jobs" type="search" maxLength={120} placeholder="Job, file or printer" value={searchInput} onChange={event=>setSearchInput(event.target.value)}/><button className="secondary-button" type="submit"><Search size={13}/> Search</button></form><button className="secondary-button queue-refresh-button" onClick={()=>void refreshQueue()} disabled={refreshRunning.current}><Activity size={13}/> Refresh queue</button></div></div>
    <div className="queue-refresh-controls">
      <label className="queue-auto-toggle"><input type="checkbox" checked={autoRefresh} onChange={event=>setAutoRefresh(event.target.checked)}/><span className="queue-toggle-track"/><strong>Auto refresh</strong><small>{autoRefresh?"On":"Off"}</small></label>
      <label className="queue-refresh-interval">Refresh every <input type="number" min={5} max={3600} step={1} value={refreshSeconds} onChange={event=>setRefreshSeconds(Math.max(5,Math.min(3600,Number(event.target.value)||5)))}/> seconds</label>
      <span className="queue-refresh-note">{autoRefresh?"Queue refreshes automatically while this page is open.":"Automatic refresh is off. Use Refresh queue whenever needed."}</span>
    </div>
    {reviewBatches.length>0&&<div className="batch-review-strip"><div><strong>{reviewBatches.length} {reviewBatches.length===1?"batch":"batches"} need review</strong><span>Review includes every file in the batch. New files start accepted; rejected files stay excluded until restored.</span></div>{reviewBatches.map(batchId=><button key={batchId} className="secondary-button" disabled={loadingBatchId===batchId||!!loadingBatchId} onClick={()=>onReviewBatch(batchId)}>{loadingBatchId===batchId?<><span className="button-spinner"/>Loading full batch…</>:<>Review batch · {jobs.filter(job=>job.batchId===batchId&&reviewableBatchFileStatuses.has(job.status)).length} shown</>}</button>)}</div>}
    {jobs.length===0 ? <EmptyModule icon={<FileText size={24}/>} title="Your queue is clear" text="When a customer uploads a document through one of your QR stations, the job and its current state will appear here."/> :
      <div className="table-scroll queue-table-scroll"><table className="queue-jobs-table"><thead><tr><th>JOB</th><th>DOCUMENT</th><th>PRINT SETTINGS</th><th>QUOTE</th><th>PRINTER / LOCAL QUEUE</th><th>STATUS</th><th>CREATED</th><th>ACTIONS</th></tr></thead><tbody>
        {queueBatches.map(batch=>{
          const first=batch.jobs[0];
          const photoJobs=batch.jobs.filter(job=>job.printKind==="PHOTO");
          const documentJobs=batch.jobs.filter(job=>job.printKind!=="PHOTO");
          const waiting=batch.jobs.filter(job=>reviewableBatchFileStatuses.has(job.status));
          const folders=Array.from(new Set(batch.jobs.map(job=>job.batchGroupName??"Loose files")));
          const fileCountLabel=`${batch.jobs.length} ${batch.jobs.length===1?"file":"files"}`;
          const folderGroupLabel=`${folders.length} ${folders.length===1?"folder group":"folder groups"}`;
          const statusLabels=Array.from(new Set(batch.jobs.map(job=>job.status)));
          const payableJobs=batch.jobs.filter(job=>job.status!=="REJECTED"&&job.status!=="REFUNDED");
          const totalRequestedPages=payableJobs.reduce((sum,job)=>sum+job.pageCount*job.copies,0);
          const monoRequestedPages=payableJobs.reduce((sum,job)=>sum+(job.colourMode==="Mono"?job.pageCount*job.copies:0),0);
          const colourRequestedPages=payableJobs.reduce((sum,job)=>sum+(job.colourMode==="Colour"?job.pageCount*job.copies:0),0);
          const totalAmount=payableJobs.reduce((sum,job)=>sum+Math.max(0,job.quotedAmountMinor),0);
          const activeBatchJobs=batch.jobs.filter(job=>job.status!=="REJECTED"&&job.status!=="REFUNDED");
          const localStorageJobs=activeBatchJobs.filter(job=>job.storageMode==="LOCAL_AGENT");
          const localAgentIds=Array.from(new Set(localStorageJobs.map(job=>job.documentAgentDeviceId).filter((id):id is string=>Boolean(id))));
          const hasUnidentifiedLocalStorage=localStorageJobs.some(job=>!job.documentAgentDeviceId);
          const requiredLocalAgentId=localAgentIds.length===1&&!hasUnidentifiedLocalStorage?localAgentIds[0]:null;
          const localComputerName=localStorageJobs.find(job=>job.documentAgentDeviceName)?.documentAgentDeviceName;
          const assignedPrinterIds=Array.from(new Set(activeBatchJobs.map(job=>job.printerDeviceId).filter((id):id is string=>Boolean(id))));
          const batchHasUnassignedFiles=activeBatchJobs.some(job=>!job.printerDeviceId);
          const lockedBatchJobs=activeBatchJobs.filter(job=>
            ["PRINT_AUTHORISED","PRINT_SUBMITTING","SPOOLER_ACCEPTED","PRINTING","OUTPUT_UNCONFIRMED","COMPLETED"].includes(job.status) ||
            (job.localQueueReceivedUtc!=null && job.status!=="LOCAL_QUEUE_READY") ||
            (job.status==="LOCAL_QUEUE_CLAIMED"&&!!job.agentLeaseExpiresUtc&&Date.parse(job.agentLeaseExpiresUtc)>Date.now())
          );
          const lockedPrinterIds=Array.from(new Set(lockedBatchJobs.map(job=>job.printerDeviceId).filter((id):id is string=>Boolean(id))));
          const batchPrinterConflict=lockedPrinterIds.length>1;
          const lockedBatchPrinter=lockedPrinterIds.length===1?printers.find(printer=>printer.id===lockedPrinterIds[0]):undefined;
          const multipleOrUnknownLocalComputers=localAgentIds.length>1||hasUnidentifiedLocalStorage;
          const lockedPrinterOnWrongComputer=Boolean(requiredLocalAgentId&&lockedBatchPrinter&&lockedBatchPrinter.agentDeviceId!==requiredLocalAgentId);
          const localComputerIdentityConflict=multipleOrUnknownLocalComputers||lockedPrinterOnWrongComputer;
          const batchAssignmentIncomplete=assignedPrinterIds.length!==1||batchHasUnassignedFiles;
          const currentBatchPrinterId=!batchAssignmentIncomplete&&assignedPrinterIds.length===1?assignedPrinterIds[0]:"";
          const batchPrinter=printers.find(printer=>printer.id===currentBatchPrinterId);
          const batchPrinterDisplayName=batchPrinter?.outputMode==="PDF_EXPORT"?"Save as PDF":batchPrinter?.displayName||activeBatchJobs.find(job=>job.printerDeviceId===currentBatchPrinterId)?.printerName||"Assigned printer";
          const assignmentJob=batch.jobs.find(job=>
            job.status==="PAID_AUTHORISED" ||
            (job.status==="LOCAL_QUEUE_CLAIMED"&&!job.localQueueReceivedUtc&&
              (!job.agentLeaseExpiresUtc||Date.parse(job.agentLeaseExpiresUtc)<=Date.now())) ||
            (job.status==="LOCAL_QUEUE_READY"&&!!job.localQueueReceivedUtc&&!job.spoolerJobId&&!job.spoolerJobName)
          );
          const availableBatchPrinters=localComputerIdentityConflict
            ?[]
            :lockedPrinterIds.length===1
              ?dispatchablePrinters.filter(printer=>printer.id===lockedPrinterIds[0])
              :requiredLocalAgentId
                ?dispatchablePrinters.filter(printer=>printer.agentDeviceId===requiredLocalAgentId)
                :dispatchablePrinters;
          const expanded=expandedBatchKeys.has(batch.key);
          const customerBatchName=batch.jobs.find(job=>job.batchName?.trim())?.batchName?.trim()??null;
          const allPdfsSaved = batch.jobs.length > 0 && batch.jobs.every(job => !!verifiedPdfExportPath(job));
          const singleSavedPdfPath = batch.jobs.length === 1 ? verifiedPdfExportPath(first) : null;
          const label=statusLabels.length===1?(allPdfsSaved?"PDF saved":prettyStatus(statusLabels[0])):`${statusLabels.length} states`;
          const tone=statusLabels.every(status=>status==="COMPLETED"||status==="LOCAL_QUEUE_READY")?"green":statusLabels.some(status=>reviewableBatchFileStatuses.has(status)||status==="AWAITING_CUSTOMER_APPROVAL"||status==="OUTPUT_UNCONFIRMED")?"amber":"blue";
          return <Fragment key={batch.key}>
            <tr className="queue-batch-summary-row">
              <td><strong className="job-id">{customerBatchName??(batch.batchId?"Batch · "+first.jobNumber:first.jobNumber)}</strong><small className="batch-summary-sub">{batch.batchId?"First job · "+first.jobNumber+" · "+fileCountLabel:fileCountLabel}</small><div className="queue-print-kind-summary">{photoJobs.length>0&&<PrintKindTag printKind="PHOTO" count={photoJobs.length}/>}{documentJobs.length>0&&<PrintKindTag printKind="DOCUMENT" count={documentJobs.length}/>}</div></td>
              <td><div className="customer-cell"><strong>{folderGroupLabel}</strong><span>{folders.slice(0,3).join(" · ")}{folders.length>3?" · +"+(folders.length-3):""}</span></div></td>
              <td><div className="detail-cell"><strong>{totalRequestedPages} pages requested</strong><span>{monoRequestedPages} B&W · {colourRequestedPages} Colour</span></div></td>
              <td className="amount-cell">{totalAmount>0?formatMoney(totalAmount,first.currency):"Quote pending"}</td>
              <td><div className="queue-printer-cell">
                {singleSavedPdfPath ? <>
                  <strong>PDF saved</strong>
                  <small className="queue-pdf-path">{singleSavedPdfPath}</small>
                </> : <>
                  <strong>{batchPrinterConflict?"Multiple printer assignments":batchAssignmentIncomplete?(assignedPrinterIds.length?"Choose one printer":"Printer not assigned"):batchPrinterDisplayName}</strong>
                  <small>{lockedPrinterIds.length===1?"Locked to the printer already used for this batch":waiting.length===0?"No files awaiting review":`${waiting.length} ${waiting.length===1?"file":"files"} awaiting review`}</small>
                  {batchPrinter?.outputMode==="PDF_EXPORT"&&<small>Saves a PDF, not paper</small>}
                </>}
                {assignmentJob&&<select
                  aria-label="Assign one printer for all files in batch"
                  value={currentBatchPrinterId}
                  disabled={!!busyJobId||batchPrinterConflict||availableBatchPrinters.length===0}
                  onChange={event=>onAssignPrinter(assignmentJob,event.target.value)}>
                  <option value="">{availableBatchPrinters.length===0?"No available printer":"Choose one printer for this batch"}</option>
                  {availableBatchPrinters.map(printer=><option key={printer.id} value={printer.id}>{printer.outputMode==="PDF_EXPORT"?"Save as PDF · ":""}{printer.displayName} · {agentDevices.find(agent=>agent.id===printer.agentDeviceId)?.deviceName||"Shop computer"} · {printer.status}</option>)}
                </select>}
                {assignmentJob&&<small className="queue-batch-assignment-note">One printer is used for all active files.</small>}
                {requiredLocalAgentId&&!localComputerIdentityConflict&&<small className="queue-batch-assignment-note"><span>Stored on {localComputerName||"the original shop computer"}.</span> <InfoTooltip label="Shop computer storage details"><span>Files stay on this computer. Reconnect it to use its printers.</span><span>To switch computers, upload a new Cloud-assisted batch.</span></InfoTooltip></small>}
                {multipleOrUnknownLocalComputers&&<small className="queue-batch-printer-lock">Files are stored on different or unknown computers. Do not start output. Check for existing output, then upload all files in a new Cloud-assisted batch.</small>}
                {lockedPrinterOnWrongComputer&&<small className="queue-batch-printer-lock">Files are on {localComputerName||"the original shop computer"}, but this printer is on another computer. Stop; do not retry or switch printers. Check existing output before resubmitting.</small>}
                {batchPrinterConflict&&<small className="queue-batch-printer-lock">This batch uses multiple printers. New output is blocked. Review the affected files.</small>}
              </div></td>
              <td><Status label={label} tone={tone}/></td>
              <td className="time-cell">{formatIstDateTime(first.createdUtc)}</td>
              <td><div className="queue-actions">
                {waiting.length>0&&batch.batchId&&<button className="subtle-button approve-action" disabled={busyJobId==="batch-review"||!!loadingBatchId} onClick={()=>onReviewBatch(batch.batchId!)}>{loadingBatchId===batch.batchId?"Loading…":"Preview & review"}</button>}
                <button className="subtle-button" onClick={()=>setExpandedBatchKeys(current=>{const next=new Set(current);if(next.has(batch.key))next.delete(batch.key);else next.add(batch.key);return next;})}>{expanded?"Hide files":"Files"}</button>
              </div></td>
            </tr>
            {expanded&&<tr className="queue-batch-detail-row"><td colSpan={8}><div className="queue-batch-files">{batch.jobs.map(job=><article className="queue-batch-file-card" key={job.id}>
              <div className="queue-batch-file-main">
                <div><div className="queue-batch-file-title"><strong>{job.document.originalFileName}</strong><PrintKindTag printKind={job.printKind}/></div><small>{job.printKind==="PHOTO" ? `${job.photoCount??0} photos · ${job.photoSizeCode??""} · ${job.pageCount} A4 pages` : `${job.batchGroupName??"Loose files"} · ${job.pageCount} pages × ${job.copies} copies = ${job.pageCount*job.copies} pages requested · ${job.colourMode} · ${job.isDuplex?"Double-sided":"Single-sided"} · ${job.paperSize||"A4"} · ${job.orientation??"AUTO"}`}</small></div>
                <Status label={prettyStatus(job.status)} tone={job.status==="REJECTED"?"red":job.status==="COMPLETED"||job.status==="LOCAL_QUEUE_READY"?"green":job.status==="AWAITING_SHOP_APPROVAL"?"amber":"blue"}/>
                <strong className="amount-cell">{job.quotedAmountMinor>0?formatMoney(job.quotedAmountMinor,job.currency):"—"}</strong>
              </div>
              {job.printerName&&<div className="queue-batch-file-printer">
                <Printer size={13}/>
                <strong>{job.outputMode==="PDF_EXPORT"?"Save as PDF":job.outputConfirmedUtc||job.status==="COMPLETED"?"Printed on":job.spoolerJobId?"Sent to":"Assigned printer"}</strong>
                <span>{job.outputMode==="PDF_EXPORT"?job.printerAgentName||"Shop computer":job.printerName}</span>
                {job.outputMode==="PDF_EXPORT"&&<small>Virtual printer: {job.printerName}</small>}
                {job.queueName&&job.queueName!==job.printerName&&<small>Windows queue: {job.queueName}</small>}
                {job.printerAgentName&&job.outputMode!=="PDF_EXPORT"&&<small>Shop computer: {job.printerAgentName}</small>}
                {job.printerSnapshotUtc&&<small>Output destination recorded: {formatIstDateTime(job.printerSnapshotUtc)}</small>}
                {job.outputConfirmedUtc&&job.outputType!=="PDF_EXPORT"&&<small>Paper output manually confirmed by {job.outputConfirmedByName||"shop operator"} at {formatIstDateTime(job.outputConfirmedUtc)}</small>}
              </div>}
              {!job.printerName&&job.storageMode==="LOCAL_AGENT"&&job.documentAgentDeviceName&&<div className="queue-batch-file-printer"><Printer size={13}/><strong>Stored on</strong><span>{job.documentAgentDeviceName}</span></div>}
              {batch.jobs.length > 1 && verifiedPdfExportPath(job) && <div className="queue-batch-file-output">
                <span>Saved PDF location</span>
                <code>{verifiedPdfExportPath(job)}</code>
              </div>}
              {job.status==="LOCAL_QUEUE_READY"&&job.failureCode&&<div className="printer-duplicate-warning" role="status">
                 <strong>Printing paused before submission</strong>
                 <span>{printBlockMessage(job.failureCode)} The original document and requested settings remain unchanged. Reassign this batch to a compatible printer, or resolve the issue with the customer before changing any setting. A settings change must be sent as a revised quote and explicitly approved before output.</span>
               </div>}
               <div className="queue-batch-file-actions">
                {job.printKind!=="PHOTO"&&["AWAITING_CONFIGURATION","AWAITING_CUSTOMER_APPROVAL","AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT","QUOTE_EXPIRED","PAYMENT_REJECTED"].includes(job.status)&&<button className="subtle-button" disabled={busyJobId===job.id} onClick={()=>{
                  setEditingSettingsJobId(job.id);
                  setEditingPrintSettings({
                    copies:job.copies,
                    colourMode:job.colourMode==="Colour"?"Colour":"Mono",
                    paperSize:(["A4","A3","A5","LETTER","LEGAL"].includes(job.paperSize)?job.paperSize:"A4") as CustomerPrintRequestSettings["paperSize"],
                    isDuplex:job.isDuplex,
                    orientation:(["AUTO","PORTRAIT","LANDSCAPE"].includes(job.orientation??"AUTO")?job.orientation??"AUTO":"AUTO") as CustomerPrintRequestSettings["orientation"]
                  });
                }}>Adjust options</button>}
                <button className="subtle-button" disabled={busyJobId===job.id} onClick={()=>onPreview(job)}>Preview original</button>
                {job.status==="COMPLETED"&&job.outputType==="PDF_EXPORT"&&verifiedPdfExportPath(job)&&job.outputFileName&&job.outputSha256?.length===64&&(job.outputSizeBytes??0)>0&&(job.outputSizeBytes??0)<=100*1024*1024&&<button className="subtle-button approve-action" disabled={busyJobId===job.id} onClick={()=>onPreviewOutput(job)}>Preview saved PDF</button>}
                <button className="subtle-button" disabled={busyJobId===job.id} onClick={()=>onHistory(job)}>History</button>
                {job.status==="AWAITING_SHOP_APPROVAL"&&<button className="subtle-button approve-action" disabled={busyJobId===job.id} onClick={()=>onApprove(job)}>Accept</button>}
                {["AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT"].includes(job.status)&&<button className="subtle-button reject-action" disabled={busyJobId===job.id} onClick={()=>onReject(job)}>Reject file</button>}
                {(job.status==="PAID_AUTHORISED"||job.status==="REJECTED")&&(job.payment?.status==="VERIFIED"||job.payment?.status==="PARTIALLY_REFUNDED")&&!job.localQueueReceivedUtc&&!job.claimedByAgentDeviceId&&<button className="subtle-button reject-action" disabled={busyJobId===job.id} onClick={()=>onRefund(job)}>{job.status==="REJECTED"?"Refund rejected file":"Review pages / refund"}</button>}
                {job.status==="LOCAL_QUEUE_READY"&&<button className="subtle-button approve-action" disabled={busyJobId===job.id} onClick={()=>onDispatch(job)}>{job.outputMode==="PDF_EXPORT"?"Save PDF":"Start printing"}</button>}
                {job.status==="OUTPUT_UNCONFIRMED"&&job.outputMode==="PDF_EXPORT"&&<button className="subtle-button approve-action" disabled={busyJobId===job.id} onClick={()=>onRetryPdfExport(job)}>Save PDF again</button>}
                {(job.status==="SPOOLER_ACCEPTED"||job.status==="PRINTING"||(job.status==="OUTPUT_UNCONFIRMED"&&job.outputMode!=="PDF_EXPORT"))&&<button className="subtle-button approve-action" disabled={busyJobId===job.id||(job.status!=="OUTPUT_UNCONFIRMED"&&!!spoolerStatusWarning(job.spoolerStatus,job.printerStatus))} onClick={()=>onConfirmOutput(job)}>Confirm output</button>}
              </div>
              {editingSettingsJobId===job.id&&editingPrintSettings&&<div className="queue-settings-editor">
                <div><strong>Review print options</strong><small>The revised quote must be approved by the customer before payment or printing.</small></div>
                <label>Colour <select value={editingPrintSettings.colourMode} onChange={event=>{
                  const colourMode=event.currentTarget.value as CustomerPrintRequestSettings["colourMode"];
                  setEditingPrintSettings(current=>current?{...current,colourMode}:current);
                }}><option value="Mono">B&W</option><option value="Colour">Colour</option></select></label>
                <label>Sides <select value={editingPrintSettings.isDuplex?"DOUBLE":"SINGLE"} onChange={event=>{
                  const isDuplex=event.currentTarget.value==="DOUBLE";
                  setEditingPrintSettings(current=>current?{...current,isDuplex}:current);
                }}><option value="SINGLE">Single-sided</option><option value="DOUBLE">Double-sided</option></select></label>
                <label>Copies <input type="number" min={1} max={100} value={editingPrintSettings.copies} onChange={event=>{
                  const copies=Math.max(1,Math.min(100,Number(event.currentTarget.value)||1));
                  setEditingPrintSettings(current=>current?{...current,copies}:current);
                }}/></label>
                <label>Paper size <select value={editingPrintSettings.paperSize} onChange={event=>{
                  const paperSize=event.currentTarget.value as CustomerPrintRequestSettings["paperSize"];
                  setEditingPrintSettings(current=>current?{...current,paperSize}:current);
                }}><option value="A4">A4</option><option value="A3">A3</option><option value="A5">A5</option><option value="LETTER">Letter</option><option value="LEGAL">Legal</option></select></label>
                <label>Orientation <select value={editingPrintSettings.orientation} onChange={event=>{
                  const orientation=event.currentTarget.value as CustomerPrintRequestSettings["orientation"];
                  setEditingPrintSettings(current=>current?{...current,orientation}:current);
                }}><option value="AUTO">Auto · follow document</option><option value="PORTRAIT">Portrait</option><option value="LANDSCAPE">Landscape</option></select></label>
                <div className="queue-settings-actions">
                  <button className="secondary-button" type="button" onClick={()=>{setEditingSettingsJobId(null);setEditingPrintSettings(null);}}>Cancel</button>
                  <button className="primary-button" type="button" disabled={busyJobId===job.id||!editingPrintSettings} onClick={()=>{if(editingPrintSettings)void onUpdateSettings(job,editingPrintSettings);setEditingSettingsJobId(null);setEditingPrintSettings(null);}}>Send revised quote</button>
                </div>
              </div>}
            </article>)}</div></td></tr>}
          </Fragment>;
        })}
      </tbody></table></div>}
    {hasMoreJobs && <div className="queue-load-older"><button className="secondary-button" type="button" onClick={() => void onLoadOlder()} disabled={loadingOlderJobs}>{loadingOlderJobs ? <span className="button-spinner"/> : <ArrowRight size={14}/>} Load older jobs</button></div>}
  </section>;
}