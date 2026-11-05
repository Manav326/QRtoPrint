import { useCallback, useEffect, useState } from "react";
import { Check, Printer, QrCode, ShieldCheck, X } from "lucide-react";
import {
  ApiError, clearNativeCustomerPrintSession, createCustomerSession, getNativeCustomerPrintSession,
  renewCustomerPrintSession, resumeCustomerSession, saveNativeCustomerPrintSession, saveNativeCustomerShopProfile
} from "../../api";
import CustomerPrintFlow from "./CustomerPrintFlow";
import CustomerLocalConnection from "./CustomerLocalConnection";
import Status from "../../shared/ui/Status";
import ThemeToggleButton from "../../ThemeToggleButton";
import { formatIstTime } from "../../date-time";

export default function CustomerEntry({publicCode,dark,onToggleTheme}:{publicCode:string;dark:boolean;onToggleTheme:()=>void}) {
  const [result,setResult]=useState<Awaited<ReturnType<typeof createCustomerSession>>|null>(null);
  const [error,setError]=useState("");
  const [loading,setLoading]=useState(true);
  const [localReady,setLocalReady]=useState(false);
  const [localFallbackSuggested,setLocalFallbackSuggested]=useState(false);
  const handleLocalReady=useCallback((ready:boolean)=>{
    setLocalReady(ready);
    if(ready)setLocalFallbackSuggested(false);
  },[]);
  const handleLocalUnavailable=useCallback(()=>setLocalFallbackSuggested(true),[]);
  useEffect(()=>{
    setLocalReady(false);
    setLocalFallbackSuggested(false);
  },[result?.localAgent?.agentDeviceId]);
  useEffect(()=>{
    let mounted=true;
    const stationKey=`qrtoprint.customer-session:${publicCode}`;
    const restore=async()=>{
      const storedToken=sessionStorage.getItem(stationKey) ||
        await getNativeCustomerPrintSession(publicCode);
      let existing:Awaited<ReturnType<typeof createCustomerSession>>|null=null;
      if(storedToken){
        try {
          existing=await resumeCustomerSession(storedToken,publicCode);
        } catch(exception) {
          if(exception instanceof ApiError&&exception.status===401){
            sessionStorage.removeItem(stationKey);
            sessionStorage.removeItem("qrtoprint.customer-session");
            await clearNativeCustomerPrintSession(publicCode);
          } else throw exception;
        }
      }
      const value=existing??await createCustomerSession(publicCode);
      // Wait for native encrypted persistence before presenting the connected session.
      // In a regular browser the bridge is absent and this safely becomes a no-op.
      const storedNatively=await saveNativeCustomerPrintSession(publicCode,value.sessionToken);
      await saveNativeCustomerShopProfile(publicCode,value.shop);
      if(!mounted)return;
      setResult(value);
      if(storedNatively)sessionStorage.removeItem(stationKey);
      else sessionStorage.setItem(stationKey,value.sessionToken);
      sessionStorage.removeItem("qrtoprint.customer-session");
    };
    void restore().catch(exception=>{
      if(mounted)setError(exception instanceof Error?exception.message:"This QR station is unavailable.");
    }).finally(()=>{if(mounted)setLoading(false);});
    return ()=>{mounted=false;};
  },[publicCode]);

  useEffect(()=>{
    if(!result)return;
    let active=true;
    let renewing=false;
    const renew=()=>{
      if(renewing)return;
      renewing=true;
      void renewCustomerPrintSession(result.sessionToken)
        .then(session=>{
          if(active){
            setResult(current=>current?.sessionToken===result.sessionToken
              ? {...current,expiresUtc:session.expiresUtc,localAgent:session.localAgent,paymentOptions:session.paymentOptions}
              : current);
          }
        })
        .catch(()=>{/* A temporary offline period is retried on the next foreground check. */})
        .finally(()=>{renewing=false;});
    };
    const checkExpiry=()=>{
      const expiry=Date.parse(result.expiresUtc);
      if(!Number.isFinite(expiry)||expiry<=Date.now()+10*60*1000)renew();
    };
    const timer=window.setInterval(renew,5*60*1000);
    const onVisibility=()=>{if(document.visibilityState==="visible")checkExpiry();};
    window.addEventListener("focus",checkExpiry);
    document.addEventListener("visibilitychange",onVisibility);
    return ()=>{
      active=false;
      window.clearInterval(timer);
      window.removeEventListener("focus",checkExpiry);
      document.removeEventListener("visibilitychange",onVisibility);
    };
  },[result?.sessionToken,result?.expiresUtc]);

  const nativeAppMode = navigator.userAgent.includes("SmartPrintAndroid/1");
  return <div className={nativeAppMode ? "customer-entry smartprint-native" : "customer-entry"}><div className="customer-entry-glow"/>
    <header className="customer-entry-brand"><div className="customer-entry-brand-lockup"><div className="brand-mark"><QrCode size={21}/></div><div><div className="brand-name">smart<span>print</span></div><div className="brand-caption">BY QUANTAEDGE</div></div></div><ThemeToggleButton dark={dark} onToggle={onToggleTheme}/></header>
    <main className={result ? "customer-entry-card customer-entry-card--upload" : "customer-entry-card"}>
      {loading?<><div className="customer-entry-spinner"><span className="button-spinner"/></div><h1>{nativeAppMode?"Connecting…":"Connecting to your print shop"}</h1>{!nativeAppMode&&<p>Creating a secure, time-limited session for this QR station.</p>}</>:
      error?<><div className="entry-error-icon"><X size={23}/></div><div className="entry-eyebrow">STATION UNAVAILABLE</div><h1>{nativeAppMode?"Shop unavailable":"This QR station can't be reached"}</h1>{!nativeAppMode&&<p>{error}</p>}<button className="primary-button" onClick={()=>window.location.reload()}>Try again</button></>:
      result?<><div className="entry-success-icon"><Check size={23}/></div><div className="entry-eyebrow">SECURE SESSION CREATED</div><h1>{nativeAppMode?result.shop.displayName:"Welcome to "+result.shop.displayName}</h1><p>{nativeAppMode?"Shop connected.":"Your phone is connected to this shop's print station."}</p>
        <div className="entry-printer-card"><div className="entry-printer-icon"><Printer size={20}/></div><div><strong>{result.printer?.displayName||"Shop print counter"}</strong><span>{nativeAppMode?(result.printer?.status||"Printer not selected"):result.printer?result.printer.manufacturer+" · "+result.printer.model:"The shop will select a printer for your job."}</span></div><Status label={result.printer?.status||"Session ready"} tone="blue"/></div>
        <div className="entry-session-note"><ShieldCheck size={16}/><span>{nativeAppMode?"Secure session · Expires "+formatIstTime(result.expiresUtc):"This session expires at "+formatIstTime(result.expiresUtc)+". Your document access stays limited to this shop session."}</span></div>
        <CustomerLocalConnection localAgent={result.localAgent} onReady={handleLocalReady} onUnavailable={handleLocalUnavailable} cloudAssistedPrintingEnabled={result.cloudAssistedPrintingEnabled}/>
        <CustomerPrintFlow dark={dark} publicCode={publicCode} sessionToken={result.sessionToken} paymentOptions={result.paymentOptions} localAgent={result.localAgent} localReady={localReady} localFallbackSuggested={localFallbackSuggested} cloudAssistedPrintingEnabled={result.cloudAssistedPrintingEnabled} photoPricing={result.photoPricing} hasAssignedPrinter={Boolean(result.printer)} printerCapabilitiesJson={result.printer?.capabilitiesJson}/>
      </>:null}
    </main>
    <footer className="customer-entry-footer">© 2026 SmartPrint by QuantaEdge · Secure customer printing</footer>
  </div>;
}
