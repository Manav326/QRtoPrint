export type CustomerUpiIntentInput={
 serverUri?:string|null;upiId?:string|null;payeeName?:string|null;
 amountMinor:number;currency:string;transactionReference?:string|null;description:string;
};

/** Keep a complete API UPI intent authoritative; rebuild older incomplete intents safely. */
export function buildCustomerUpiIntent(input:CustomerUpiIntentInput):string|null{
 const server=input.serverUri?.trim();
 if(server){
  try{
   const parsed=new URL(server);
   if(parsed.protocol==="upi:"&&parsed.hostname==="pay"&&
      parsed.searchParams.get("pa")&&parsed.searchParams.get("am")&&
      parsed.searchParams.get("cu")==="INR"&&parsed.searchParams.get("tr"))return server;
  }catch{/* Rebuild only from verified payment snapshot values below. */}
 }
 const upiId=input.upiId?.trim();
 if(!upiId||!Number.isSafeInteger(input.amountMinor)||input.amountMinor<=0||
    input.currency.trim().toUpperCase()!=="INR")return null;
 const reference=(input.transactionReference??"").replace(/-/g,"").replace(/[^a-zA-Z0-9_]/g,"").slice(0,35);
 if(!reference)return null;
 const query=new URLSearchParams();
 query.set("pa",upiId);
 query.set("pn",input.payeeName?.trim()||"Shop");
 query.set("am",(input.amountMinor/100).toFixed(2));
 query.set("cu","INR");
 query.set("tn",input.description.trim().slice(0,80)||"SmartPrint payment");
 query.set("tr",reference);
 return "upi://pay?"+query.toString();
}
