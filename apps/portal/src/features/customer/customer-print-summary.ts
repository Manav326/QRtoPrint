import { shouldBlockBatchCheckoutForExpiredQuote } from "../../batch-checkout-state.ts";
import { customerFileFingerprint } from "./customer-print-utils.ts";
import type {
  CustomerBatchPayment, CustomerJobStatus, CustomerPayment, CustomerPhotoPricing,
  PrintQuoteResult, UploadedPrintJob
} from "../../api";

export type CustomerPrintSummaryInput = {
  files: File[];
  file: File | null;
  uploaded: UploadedPrintJob | null;
  uploadedBatch: UploadedPrintJob[];
  uploadedFileFingerprints: string[];
  batchId: string;
  batchPayment: CustomerBatchPayment | null;
  payment: CustomerPayment | null;
  jobStatus: CustomerJobStatus | null;
  batchStatuses: Record<string, CustomerJobStatus>;
  quotesByJobId: Record<string, PrintQuoteResult>;
  photoPricing: CustomerPhotoPricing | null;
  groupNameForFile: (file: File) => string;
};

const batchCheckoutStatuses = new Set([
  "REJECTED", "REFUNDED", "AWAITING_PAYMENT", "PAYMENT_REJECTED",
  "AWAITING_CUSTOMER_PAYMENT", "PAYMENT_REPORTED", "AWAITING_COUNTER_PAYMENT",
  "PAID_AUTHORISED", "LOCAL_QUEUE_CLAIMED", "LOCAL_QUEUE_READY", "PRINT_AUTHORISED",
  "PRINT_SUBMITTING", "SPOOLER_ACCEPTED", "PRINTING", "OUTPUT_UNCONFIRMED", "COMPLETED"
]);

/** Derives customer batch, quote and payment presentation from the latest known job snapshots. */
export function deriveCustomerPrintSummary({
  files, file, uploaded, uploadedBatch, uploadedFileFingerprints, batchId, batchPayment,
  payment, jobStatus, batchStatuses, quotesByJobId, photoPricing, groupNameForFile
}: CustomerPrintSummaryInput) {
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

  return {
    pendingFilesCount,
    uploadGroups,
    quoteItems,
    batchLockedForPayment,
    statusForBatchItem,
    quoteExpiredForBatchItem,
    batchPayableItems,
    batchPayableTotal,
    hasLegacyIndividualPayment,
    hasSelectedLegacyIndividualPayment,
    batchPaymentStarted,
    acceptedQuoteExpired,
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
    acceptedQuoteEntries,
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
    photoAlreadyUploaded
  };
}
