import type { CustomerPhotoPricing, CustomerPrintRequestSettings, PrintQuoteResult, UploadedPrintJob } from "../../api";

export type CustomerPrintFlowProps = {
  dark:boolean;
  publicCode:string;
  sessionToken:string;
  paymentOptions:{upiEnabled:boolean;upiId?:string|null;payeeName?:string|null;cashEnabled:boolean;instantPaymentEnabled:boolean};
  localAgent:CustomerLocalAgent|null;
  localReady:boolean;
  localFallbackSuggested:boolean;
  cloudAssistedPrintingEnabled:boolean;
  photoPricing:CustomerPhotoPricing|null;
  hasAssignedPrinter:boolean;
  printerCapabilitiesJson?:string|null;
};

export type CustomerLocalAgent = NonNullable<Awaited<ReturnType<typeof import("../../api").createCustomerSession>>["localAgent"]>;

export function rejectedPageList(json?:string) {
  try {
    const pages=JSON.parse(json||"[]");
    return Array.isArray(pages)&&pages.length>0?pages.join(", "):"selected pages";
  } catch {
    return "selected pages";
  }
}

export function formatBytes(bytes:number) {
  return bytes<1024*1024?`${(bytes/1024).toFixed(0)} KB`:`${(bytes/(1024*1024)).toFixed(2)} MB`;
}

export type CustomerGroupSettings = {
  copies: string;
  colourMode: "Mono" | "Colour";
  isDuplex: boolean;
  advancedEnabled: boolean;
  paperSize: CustomerPrintRequestSettings["paperSize"];
  orientation: CustomerPrintRequestSettings["orientation"];
};
export type CustomerFileSettings = CustomerGroupSettings & { custom: boolean };

export function consumeSmartPrintNativePickerGroups():string[]|undefined {
  const target=window as Window & {__smartPrintNativePickerGroups?:string[]};
  const groups=target.__smartPrintNativePickerGroups;
  delete target.__smartPrintNativePickerGroups;
  return Array.isArray(groups)?groups:undefined;
}
export type CustomerSavedPrintSettings = CustomerPrintRequestSettings & { advancedEnabled?: boolean };
export type CustomerPhotoSettings = { sizeCode: string; photoCount: number; photosPerPage?: number };
export function customerPhotoSizeLabel(sizeCode?:string|null):string {
  switch(sizeCode){
    case "35x45": return "35 × 45 mm";
    case "25x35": return "25 × 35 mm";
    case "2x2": return "2 × 2 in";
    case "4x6": return "4 × 6 in";
    default: return "photo";
  }
}

export type StoredCustomerDraft = {
  sessionMarker: string;
  uploaded?: UploadedPrintJob;
  batchName?: string;
  uploadedBatch?: UploadedPrintJob[];
  uploadedFileFingerprints?: string[];
  batchId?: string;
  quote: PrintQuoteResult | null;
  fileGroupNames?: Record<string,string>;
  groupSettings?: Record<string,CustomerGroupSettings>;
  fileSettings?: Record<string,CustomerFileSettings>;
  printSettingsByJobId?: Record<string,CustomerSavedPrintSettings>;
  quotesByJobId?: Record<string,PrintQuoteResult>;
  photoSettingsByJobId?: Record<string,CustomerPhotoSettings>;
};

export function isCustomerImage(file:File):boolean {
  return file.type.toLowerCase().startsWith("image/") || /\.(jpe?g|png)$/i.test(file.name);
}

export function customerFileFingerprint(file:File):string {
  const relativePath=(file as File & {webkitRelativePath?:string}).webkitRelativePath??"";
  const nativeGroup=(file as File & {__smartPrintBatchGroup?:string}).__smartPrintBatchGroup??"";
  return `${relativePath||nativeGroup}\u0000${file.name}\u0000${file.size}\u0000${file.lastModified}\u0000${file.type}`;
}

export function isCustomerPdf(file:File):boolean {
  return file.type.toLowerCase()==="application/pdf"||/\.pdf$/i.test(file.name);
}

export const smartPrintNativePdfPreviewTokens=new WeakMap<File,string>();

export function postSmartPrintNativeMessage(message:string):boolean {
  const bridge=(window as Window & {SmartPrintNative?:{postMessage:(value:string)=>void}}).SmartPrintNative;
  if(!bridge?.postMessage)return false;
  try {bridge.postMessage(message);return true;}
  catch {return false;}
}

export function consumeSmartPrintNativePdfPreviews():Array<string|null>|undefined {
  const target=window as Window & {__smartPrintNativePdfPreviews?:Array<string|null>};
  const previews=target.__smartPrintNativePdfPreviews;
  delete target.__smartPrintNativePdfPreviews;
  return Array.isArray(previews)?previews:undefined;
}

export function customerBatchGroupName(file:File):string {
  const relativePath=(file as File & {webkitRelativePath?:string}).webkitRelativePath??"";
  const pathParts=relativePath.split("/").filter(Boolean);
  return pathParts.length>1?pathParts[0]:"Loose files";
}

export function normalizeCustomerCopies(value:string):string {
  const trimmed=value.trim();
  if(!/^\d+$/.test(trimmed))return "1";
  const copies=Number(trimmed);
  if(!Number.isFinite(copies))return "100";
  return String(Math.min(100,Math.max(1,Math.trunc(copies))));
}

export function readStoredCustomerDraft(publicCode:string,sessionToken:string):StoredCustomerDraft|null {
  try {
    const raw=localStorage.getItem(`qrtoprint.customer-active-job:${publicCode}`);
    if(!raw)return null;
    const draft=JSON.parse(raw) as StoredCustomerDraft;
    return draft.sessionMarker===sessionToken.slice(-16)&&
      (draft.uploaded?.job?.id||draft.uploadedBatch?.length||draft.batchName?.trim())?draft:null;
  } catch {
    return null;
  }
}

export function smartPrintNativeHeadline(status:string):string {
  switch(status) {
    case "AWAITING_SHOP_APPROVAL": return "Awaiting shop review";
    case "AWAITING_PAYMENT": return "Payment required";
    case "AWAITING_CUSTOMER_PAYMENT": return "Complete UPI payment";
    case "AWAITING_COUNTER_PAYMENT": return "Pay at counter";
    case "PAYMENT_REPORTED": return "Verification pending";
    case "PAYMENT_REJECTED": return "Payment not verified";
    case "PAID_AUTHORISED": return "Payment verified";
    case "LOCAL_QUEUE_CLAIMED": return "Transferring to shop";
    case "LOCAL_QUEUE_READY": return "Queued at shop";
    case "PRINT_AUTHORISED": return "Printing authorised";
    case "PRINT_SUBMITTING": return "Sending to printer";
    case "SPOOLER_ACCEPTED": return "Printer queue accepted";
    case "PRINTING": return "Printing";
    case "OUTPUT_UNCONFIRMED": return "Output check pending";
    case "COMPLETED": return "Ready to collect";
    case "REJECTED": return "Request declined";
    case "QUOTE_EXPIRED": return "Quote expired";
    default: return "Request in progress";
  }
}

export function smartPrintNativeStatus(status:string):string {
  switch(status) {
    case "AWAITING_SHOP_APPROVAL": return "Awaiting shop approval.";
    case "AWAITING_PAYMENT": return "Quote ready · Payment required.";
    case "AWAITING_CUSTOMER_PAYMENT": return "Pay shop · Submit UTR.";
    case "AWAITING_COUNTER_PAYMENT": return "Pay at the counter.";
    case "PAYMENT_REPORTED": return "UTR sent · Verify pending.";
    case "PAYMENT_REJECTED": return "Payment not verified.";
    case "PAID_AUTHORISED": return "Payment verified · Print pending.";
    case "LOCAL_QUEUE_CLAIMED": return "Transferring to shop.";
    case "LOCAL_QUEUE_READY": return "Queued · Operator pending.";
    case "PRINT_AUTHORISED": return "Printing authorised.";
    case "PRINT_SUBMITTING": return "Sending to printer.";
    case "SPOOLER_ACCEPTED": return "Queue accepted · Paper unconfirmed.";
    case "PRINTING": return "Printing in progress.";
    case "OUTPUT_UNCONFIRMED": return "Shop confirming print output.";
    case "COMPLETED": return "Ready for collection.";
    case "REJECTED": return "Shop rejected request.";
    case "QUOTE_EXPIRED": return "Quote expired.";
    default: return "Status updated.";
  }
}

export function smartPrintNativeBoundary(status:string|undefined,hasQuote:boolean):string {
  switch(status) {
    case "AWAITING_SHOP_APPROVAL": return "Shop review · No payment.";
    case "AWAITING_PAYMENT": return "Pay shop · Verification required.";
    case "AWAITING_COUNTER_PAYMENT": return "Counter payment · Await confirmation.";
    case "AWAITING_CUSTOMER_PAYMENT": return "Submit UTR · Shop verifies.";
    case "PAYMENT_REPORTED": return "UTR pending · Shop verifies.";
    case "PAID_AUTHORISED": return "Payment verified · Printing not started.";
    case "LOCAL_QUEUE_CLAIMED": return "Secure transfer · Not printing.";
    case "LOCAL_QUEUE_READY": return "Queued · Operator approval needed.";
    case "PRINT_AUTHORISED": return "Shop approved physical printing.";
    case "PRINT_SUBMITTING": return "Sending to printer.";
    case "SPOOLER_ACCEPTED": return "Queue accepted · Paper unconfirmed.";
    case "PRINTING": return "Printing · Output unconfirmed.";
    case "OUTPUT_UNCONFIRMED": return "Output unconfirmed · Shop checks pages.";
    case "COMPLETED": return "Output confirmed · Collect print.";
    case "QUOTE_EXPIRED": return "Quote expired · Request another.";
    case "REJECTED": return "Request rejected · Ask shop.";
    default: return hasQuote ? "Quote only · No payment." : "No payment yet.";
  }
}
