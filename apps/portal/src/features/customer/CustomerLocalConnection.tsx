import { useEffect, useRef, useState } from "react";
import Status from "../../shared/ui/Status";
import type { CustomerLocalAgent } from "./customer-print-utils";

type LocalAgentCheckStatus = "idle" | "checking" | "connected" | "unverified" | "recovering" | "unavailable" | "denied" | "insecure";

function isPrivateIpv4Hostname(hostname:string):boolean {
  const octets=hostname.split(".").map(Number);
  return octets.length===4 &&
    octets.every(part=>Number.isInteger(part)&&part>=0&&part<=255) &&
    (octets[0]===10 ||
     (octets[0]===172&&octets[1]>=16&&octets[1]<=31) ||
     (octets[0]===192&&octets[1]===168));
}

function isLocalOrLoopbackPortalOrigin(hostname:string):boolean {
  const value=hostname.toLowerCase().replace(/^\[|\]$/g,"");
  return value==="localhost" ||
    value.endsWith(".localhost") ||
    value==="127.0.0.1" ||
    value==="::1" ||
    isPrivateIpv4Hostname(value) ||
    value.endsWith(".local");
}

function buildSecureLocalPortalUrl():string|null {
  try {
    const url=new URL(window.location.href);
    if(url.protocol!=="http:"||url.port!=="3000"||!isPrivateIpv4Hostname(url.hostname))return null;
    url.protocol="https:";
    url.port="3443";
    return url.toString();
  } catch {
    return null;
  }
}

async function requestSmartPrintNativeLocalNetworkAccess():Promise<"granted"|"denied"|"not_required"|"unavailable"> {
  const bridge=(window as Window & {
    SmartPrintNative?: {
      postMessage:(message:string)=>void;
      addEventListener:(type:string,listener:(event:MessageEvent)=>void)=>void;
      removeEventListener:(type:string,listener:(event:MessageEvent)=>void)=>void;
    };
  }).SmartPrintNative;
  if(!bridge?.postMessage||!bridge.addEventListener||!bridge.removeEventListener)return "unavailable";

  return new Promise(resolve=>{
    let settled=false;
    let timeout:number|undefined;
    const finish=(result:"granted"|"denied"|"not_required"|"unavailable")=>{
      if(settled)return;
      settled=true;
      if(timeout!==undefined)window.clearTimeout(timeout);
      bridge.removeEventListener("message",handleReply);
      resolve(result);
    };
    const handleReply=(event:MessageEvent)=>{
      const value=typeof event.data==="string"?event.data:"";
      if(value==="localNetworkAccess:granted")finish("granted");
      else if(value==="localNetworkAccess:denied")finish("denied");
      else if(value==="localNetworkAccess:not_required")finish("not_required");
    };
    timeout=window.setTimeout(()=>finish("unavailable"),30000);
    bridge.addEventListener("message",handleReply);
    try{bridge.postMessage("requestLocalNetworkAccess");}
    catch{finish("unavailable");}
  });
}

export default function CustomerLocalConnection({localAgent,onReady,onUnavailable,cloudAssistedPrintingEnabled}:{localAgent:CustomerLocalAgent|null;onReady:(ready:boolean)=>void;onUnavailable:()=>void;cloudAssistedPrintingEnabled:boolean}) {
  const nativeAppMode = navigator.userAgent.includes("SmartPrintAndroid/1");
  const [permissionState,setPermissionState]=useState<PermissionState|"unknown">("unknown");
  const [checkStatus,setCheckStatus]=useState<LocalAgentCheckStatus>("idle");
  const [message,setMessage]=useState("");
  const checkGeneration=useRef(0);
  const securePortalUrl=buildSecureLocalPortalUrl();
  const localOriginDoesNotNeedLnaPermission=isLocalOrLoopbackPortalOrigin(window.location.hostname);

  function requireSecureContext() {
    setCheckStatus("insecure");
    onReady(false);
    if(cloudAssistedPrintingEnabled)onUnavailable();
    setMessage(nativeAppMode ? "Secure HTTPS required." : securePortalUrl
      ? "This shop page is open over HTTP. Chrome blocks local-network requests from insecure pages without showing a permission prompt. Open the secure shop link below; the certificate must be trusted on this phone."
      : "This shop page is not a secure HTTPS context. Open the shop's HTTPS customer portal before checking local printing.");
  }

  async function checkConnection() {
    const generation=++checkGeneration.current;
    if(!localAgent)return;
    if(!window.isSecureContext) {
      requireSecureContext();
      return;
    }
    setCheckStatus("checking");
    onReady(false);
    setMessage(nativeAppMode ? "Checking shop Wi-Fi." : "Checking the shop computer. No document is sent by this check.");
    let nativeLocalNetworkPermissionDenied=false;
    if(nativeAppMode) {
      const access=await requestSmartPrintNativeLocalNetworkAccess();
      if(generation!==checkGeneration.current)return;
      // Android 16 only enforces local-network permission when its protection is
      // active. A denied Nearby devices prompt must not block a connection on
      // devices where the OS still grants local access implicitly.
      nativeLocalNetworkPermissionDenied=access==="denied";
    }

    let validEndpointCount=0;
    for(const value of localAgent.endpoints.slice(0,8)) {
      let endpoint:URL;
      try {
        endpoint=new URL(value);
        if(endpoint.protocol!=="http:" ||
           endpoint.port!=="17842" ||
           endpoint.username!=="" ||
           endpoint.password!=="" ||
           endpoint.pathname!=="/" ||
           endpoint.search!=="" ||
           endpoint.hash!=="" ||
           !isPrivateIpv4Hostname(endpoint.hostname))continue;
      } catch {
        continue;
      }

      validEndpointCount++;
      const controller=new AbortController();
      const timeout=window.setTimeout(()=>controller.abort(),3500);
      try {
        // Start from the user's click when a browser permission prompt is required.
        const response=await fetch(`${endpoint.origin}/api/v1/local/status`,{
          method:"GET",
          mode:"cors",
          cache:"no-store",
          credentials:"omit",
          signal:controller.signal,
          targetAddressSpace:"local"
        } as RequestInit & {targetAddressSpace:"local"});
        if(generation!==checkGeneration.current)return;
        if(!response.ok)continue;

        const status=await response.json() as {
          agentDeviceId?:string;
          protocolVersion?:number;
          status?:string;
          localUploadReady?:boolean;
          readiness?: {
            queueInitialized?:boolean;
            printWorkerRunning?:boolean;
            heartbeatFresh?:boolean;
            printerInventoryFresh?:boolean;
            discoveredPrinterCount?:number;
            localUploadReady?:boolean;
          };
        };
        if(generation!==checkGeneration.current)return;
        if(status.agentDeviceId===localAgent.agentDeviceId &&
           status.protocolVersion===1 &&
           status.status==="LOCAL_ENDPOINT_READY") {
          if(!status.readiness) {
            // Existing installed Agents do not yet publish the detailed readiness
            // payload. Keep their working local route available; the ticket API
            // and Agent must still validate the assigned printer and queue.
            setCheckStatus("unverified");
            onReady(true);
            setMessage("This shop computer is reachable, but its Agent has not reported detailed queue health yet. SmartPrint will verify the assigned printer and upload with the shop before confirming the file.");
            return;
          }
          if(status.localUploadReady!==true || status.readiness?.localUploadReady!==true) {
            setCheckStatus("recovering");
            onReady(false);
            const readiness=status.readiness;
            const agentHeartbeatOffline=readiness?.heartbeatFresh!==true;
            if(cloudAssistedPrintingEnabled||agentHeartbeatOffline)onUnavailable();
            const blockers=[
              readiness?.queueInitialized!==true?"protected document queue is not ready":null,
              readiness?.printWorkerRunning!==true?"print worker is not running":null,
              readiness?.heartbeatFresh!==true?"cloud heartbeat is not fresh":null,
              readiness?.printerInventoryFresh!==true?"printer inventory is not fresh":null
            ].filter((value):value is string=>value!==null);
            const detail=blockers.length?blockers.join(", "):"the Agent has not reported full pipeline readiness";
            setMessage("The shop computer is reachable, but local printing is recovering ("+detail+"). "+
              (cloudAssistedPrintingEnabled
                ? "Choose Cloud-assisted upload while the shop operator restores the local print pipeline."
                : agentHeartbeatOffline
                  ? "The Agent cloud heartbeat is offline. Choose Secure queue · HTTPS to save this request while the shop reconnects."
                  : "Ask the shop operator to restore the protected local queue, then check again."));
            return;
          }
          setCheckStatus("connected");
          onReady(true);
          setMessage(nativeAppMode ? "Shop Wi-Fi ready · Local HTTP is not encrypted." : "This phone can reach the shop computer. Direct uploads use the shop's private Wi-Fi, but this local HTTP connection is not encrypted in transit. Choose cloud-assisted upload for HTTPS transfer.");
          return;
        }
      } catch {
        // Continue across the Agent's advertised private addresses.
      } finally {
        window.clearTimeout(timeout);
      }
    }

    if(generation!==checkGeneration.current)return;
    setCheckStatus("unavailable");
    onReady(false);
    // A customer session can outlive the Agent heartbeat. Surface the existing
    // HTTPS queue path after the local Agent endpoint is confirmed unreachable;
    // the API still verifies the live Agent/plan boundary before accepting bytes.
    onUnavailable();
    setMessage(nativeAppMode
      ? nativeLocalNetworkPermissionDenied
        ? "Could not reach the shop. Check that this phone is on the same Wi-Fi as the shop computer. If Android 16 local-network access is restricted, allow SmartPrint under Android Settings → Permissions → Nearby devices. Cloud-assisted upload remains available."
        : "Shop offline · Secure HTTPS queue available while the shop Agent is offline."
      : validEndpointCount===0
        ? "The shop Agent has not announced a usable local address. Secure HTTPS queue fallback is available only while the shop Agent is offline."
        : "Could not reach the shop computer. Make sure this phone is connected to the same shop Wi-Fi, then ask the shop operator to check the connection. Secure HTTPS queue fallback is available only while the shop Agent is offline.");
  }

  useEffect(()=>{
    // Invalidate an older request immediately when the shop endpoint list changes.
    checkGeneration.current++;
    if(!localAgent?.endpoints.length) {
      onReady(false);
      onUnavailable();
      setPermissionState("unknown");
      setCheckStatus("unavailable");
      setMessage("The shop's paired print computer has not reported a reachable local connection. Direct Wi-Fi transfer is temporarily unavailable. Use Secure queue · HTTPS below to submit your request when available.");
      return ()=>{checkGeneration.current++;};
    }
    onReady(false);
    setCheckStatus("idle");
    setMessage("");
    if(!window.isSecureContext) {
      requireSecureContext();
      return ()=>{checkGeneration.current++;};
    }

    // Android WebView does not expose Chrome's site-level Local Network Access
    // prompt. Keep this check user-initiated and let the native app request the
    // Android 16 transitional permission only when the customer chooses Shop Wi-Fi.
    if(nativeAppMode) {
      setPermissionState("unknown");
      setCheckStatus("idle");
      setMessage("Tap Check Wi-Fi to connect to this shop.");
      return ()=>{checkGeneration.current++;};
    }

    if(localOriginDoesNotNeedLnaPermission) {
      setPermissionState("unknown");
      return ()=>{checkGeneration.current++;};
    }
    if(!navigator.permissions?.query)return ()=>{checkGeneration.current++;};

    let active=true;
    let permissionStatus:PermissionStatus|null=null;
    const updatePermission=()=>{
      if(!active||!permissionStatus)return;
      setPermissionState(permissionStatus.state);
      if(permissionStatus.state==="denied") {
        setCheckStatus("denied");
        onReady(false);
        if(cloudAssistedPrintingEnabled)onUnavailable();
        setMessage(nativeAppMode ? "Allow local network access." : "Chrome has blocked Local network access for this public HTTPS site. On Android, open the site information icon beside the address bar → Permissions → Local network access → Allow, then reload this QR page.");
      } else if(permissionStatus.state==="prompt") {
        setCheckStatus("idle");
        setMessage(nativeAppMode ? "Tap Check Wi-Fi." : "Tap Check shop Wi-Fi to request local-network access from Chrome.");
      }
    };

    void navigator.permissions.query({name:"local-network" as PermissionName})
      .then(status=>{
        if(!active)return;
        permissionStatus=status;
        status.addEventListener("change",updatePermission);
        updatePermission();
        if(status.state==="granted")void checkConnection();
      })
      .catch(()=>{if(active)setPermissionState("unknown");});

    return ()=>{
      active=false;
      checkGeneration.current++;
      permissionStatus?.removeEventListener("change",updatePermission);
    };
  },[localAgent?.agentDeviceId,localAgent?.endpoints.join("|"),onReady,onUnavailable]);

  const label=!localAgent?.endpoints.length?"Temporarily unavailable":
    checkStatus==="checking"?"Checking…":
    checkStatus==="connected"?(nativeAppMode?"Connected":"Shop computer reachable"):
    checkStatus==="unverified"?"Connected · health details unavailable":
    checkStatus==="recovering"?"Print pipeline recovering":
    checkStatus==="denied"?(nativeAppMode?"Access blocked":"Browser permission blocked"):
    checkStatus==="insecure"?"HTTPS required":
    checkStatus==="unavailable"?"Not reachable":"Not checked";
  const tone=checkStatus==="connected"?"green":
    checkStatus==="unverified"?"amber":
    checkStatus==="recovering"?"amber":
    checkStatus==="unavailable"||checkStatus==="denied"?"red":"blue";

  return <section className="entry-local-agent">
    <div className="entry-local-agent-head">
      <div><strong>{nativeAppMode?"Shop Wi-Fi":"Shop Wi-Fi connection"}</strong><span>Optional local connection check</span></div>
      <Status label={label} tone={tone}/>
    </div>
    {!nativeAppMode&&<p>Check whether this phone can reach the shop's local print computer. This check does not upload your document.</p>}
    {!localAgent?.endpoints.length
      ? <div className="entry-local-agent-message" role="status"><strong>Direct transfer is temporarily unavailable.</strong> {message}</div>
      : checkStatus==="insecure"
      ? <div className="entry-local-agent-message">
          <small>{message}</small>
          {securePortalUrl&&<a className="customer-flow-button" href={securePortalUrl}>{nativeAppMode?"Open HTTPS":"Open secure HTTPS shop page"}</a>}
        </div>
      : permissionState==="denied"&&!localOriginDoesNotNeedLnaPermission
        ? <small className="entry-local-agent-message">{message}</small>
        : <button type="button" className="customer-flow-button" disabled={checkStatus==="checking"} onClick={()=>void checkConnection()}>
            {checkStatus==="checking"?"Checking…":checkStatus==="connected"?"Check again":nativeAppMode?"Check Wi-Fi":"Check shop Wi-Fi"}
          </button>}
    {message&&checkStatus!=="insecure"&&!(permissionState==="denied"&&!localOriginDoesNotNeedLnaPermission)&&<small className="entry-local-agent-message">{message}</small>}
  </section>;
}
