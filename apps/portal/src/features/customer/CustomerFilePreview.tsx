import { FileText, Image as ImageIcon, ShieldCheck } from "lucide-react";
import {
  customerFileFingerprint, isCustomerImage, isCustomerPdf, smartPrintNativePdfPreviewTokens,
  type CustomerPhotoSettings
} from "./customer-print-utils";

type CustomerFilePreviewProps = {
  previewUrl:string|null;
  file:File|null;
  photoSettingsByFileFingerprint:Record<string,CustomerPhotoSettings>;
  photoPreviewPagesByFingerprint:Record<string,string[]>;
  photoRatesAvailable:boolean;
  photoAlreadyUploaded:boolean;
  busy:boolean;
  batchLockedForPayment:boolean;
  nativeAppMode:boolean;
  selectedPreparedPhotoPage:number;
  onSelectPreparedPhotoPage:(index:number)=>void;
  onOpenPhotoStudio:(file:File)=>void;
  onOpenCustomerPdfPreview:(file:File)=>void;
};

export default function CustomerFilePreview({
  previewUrl,file,photoSettingsByFileFingerprint,photoPreviewPagesByFingerprint,
  photoRatesAvailable,photoAlreadyUploaded,busy,batchLockedForPayment,nativeAppMode,
  selectedPreparedPhotoPage,onSelectPreparedPhotoPage,onOpenPhotoStudio,onOpenCustomerPdfPreview
}:CustomerFilePreviewProps) {
  if(!previewUrl||!file)return null;
  const fingerprint=customerFileFingerprint(file);
  const preparedPhoto=photoSettingsByFileFingerprint[fingerprint];
  const preparedPages=photoPreviewPagesByFingerprint[fingerprint]??[];
  return <div className="customer-file-preview">
    <div className="customer-file-label">
      <FileText size={15}/>
      <span>{preparedPhoto?"Prepared photo sheet":"Selected document"} · {file.name}</span>
      {isCustomerImage(file)&&photoRatesAvailable&&<button type="button" className="customer-file-studio-button" disabled={busy||batchLockedForPayment||photoAlreadyUploaded} title={photoAlreadyUploaded?"This photo has already been submitted to the shop.":"Open Photo Studio"} onClick={()=>onOpenPhotoStudio(file)}><ImageIcon size={14}/> {photoAlreadyUploaded?"Already sent to shop":"Open Photo Studio"}</button>}
    </div>
    {isCustomerImage(file)&&!photoRatesAvailable&&<small className="customer-upload-local-note">Photo printing is not enabled at this shop yet. Ask the shop owner to configure photo sizes and rates.</small>}
    {isCustomerImage(file)&&photoAlreadyUploaded&&photoRatesAvailable&&<small className="customer-upload-local-note">This photo is already with the shop. The shop owner can prepare it from the review queue without creating another print job.</small>}
    {isCustomerPdf(file)
      ? preparedPhoto
        ? preparedPages.length
          ? <div className="customer-edited-photo-preview">
              <div className="customer-edited-photo-frame">
                <img src={preparedPages[Math.min(selectedPreparedPhotoPage,preparedPages.length-1)]} alt={"Prepared photo sheet page "+(Math.min(selectedPreparedPhotoPage,preparedPages.length-1)+1)}/>
                <span>SmartPrint by QuantaEdge</span>
              </div>
              {preparedPages.length>1&&<div className="customer-edited-photo-pages" aria-label="Prepared A4 pages">
                {preparedPages.map((url,index)=><button type="button" key={url} aria-pressed={selectedPreparedPhotoPage===index} onClick={()=>onSelectPreparedPhotoPage(index)}>
                  <img src={url} alt={"Page "+(index+1)+" thumbnail"}/><span>Page {index+1}</span>
                </button>)}
              </div>}
              <small>Preview only · The final prepared file stays in the print workflow.</small>
            </div>
          : <div className="customer-private-photo-preview"><ShieldCheck size={20}/><strong>Photo sheet prepared for this shop</strong><span>It will be uploaded with your print request. The finished sheet is not available as a separate download.</span></div>
        : nativeAppMode
          ? smartPrintNativePdfPreviewTokens.has(file)
            ? <button type="button" className="customer-flow-button" onClick={()=>onOpenCustomerPdfPreview(file)}><FileText size={16}/> Preview PDF</button>
            : <small className="customer-upload-local-note">PDF selected. Preview is unavailable for this file, but you can still upload it.</small>
          : <iframe src={previewUrl} title={file.name+" preview"}/>
      : <img className="customer-original-image-preview" src={previewUrl} alt={file.name}/>}
  </div>;
}
