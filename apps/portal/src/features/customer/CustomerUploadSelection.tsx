import type { Dispatch, SetStateAction } from "react";
import { FileText, FileUp, FolderOpen, X } from "lucide-react";
import PrintKindTag from "../../shared/ui/PrintKindTag";
import {
  consumeSmartPrintNativePickerGroups, consumeSmartPrintNativePdfPreviews,
  customerFileFingerprint, formatBytes, normalizeCustomerCopies,
  type CustomerGroupSettings, type CustomerFileSettings, type CustomerPhotoSettings,
  type CustomerLocalAgent
} from "./customer-print-utils";

type CustomerUploadSelectionProps = {
  localAgent: CustomerLocalAgent | null;
  secureQueueFallbackEnabled: boolean;
  cloudAssistedPrintingEnabled: boolean;
  customerUploadUnavailable: boolean;
  uploadMode: "LOCAL" | "CLOUD";
  setUploadMode: Dispatch<SetStateAction<"LOCAL" | "CLOUD">>;
  setUploadModeManuallySelected: (selected: boolean) => void;
  batchLockedForPayment: boolean;
  chooseFiles: (files: File[], nativeGroups?: string[], nativePreviewTokens?: Array<string | null>) => void;
  nativeAppMode: boolean;
  files: File[];
  uploadGroups: Array<{ name: string; files: File[] }>;
  defaultsForGroup: (name: string) => CustomerGroupSettings;
  setGroupSettings: Dispatch<SetStateAction<Record<string, CustomerGroupSettings>>>;
  fileSettings: Record<string, CustomerFileSettings>;
  setFileSettings: Dispatch<SetStateAction<Record<string, CustomerFileSettings>>>;
  photoSettingsByFileFingerprint: Record<string, CustomerPhotoSettings>;
  selectedFileIndex: number;
  setSelectedFileIndex: Dispatch<SetStateAction<number>>;
  busy: boolean;
  removeSelectedFile: (fingerprint: string) => void;
};

export default function CustomerUploadSelection({
  localAgent,secureQueueFallbackEnabled,cloudAssistedPrintingEnabled,customerUploadUnavailable,
  uploadMode,setUploadMode,setUploadModeManuallySelected,batchLockedForPayment,chooseFiles,
  nativeAppMode,files,uploadGroups,defaultsForGroup,setGroupSettings,fileSettings,setFileSettings,
  photoSettingsByFileFingerprint,selectedFileIndex,setSelectedFileIndex,busy,removeSelectedFile
}:CustomerUploadSelectionProps) {
  return <>
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

  </>;
}
