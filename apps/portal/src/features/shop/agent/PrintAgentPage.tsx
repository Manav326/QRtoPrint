import { useEffect, useState } from "react";
import {
  Check, Clock3, Copy, Monitor, Plus, RotateCcw, ShieldCheck, Wifi
} from "lucide-react";
import QRCode from "react-qr-code";
import type { AgentDevice } from "../../../api";
import {
  getLocalAgentStatus,
  getLocalNetworkSnapshot,
  selectLocalNetwork,
  startLocalPhoneTest,
  type DesktopAgentStatus,
  type DesktopLocalNetworkSnapshot,
  type DesktopLocalPhoneTest
} from "../../../desktop-bridge";
import { getPublicDownloadPath, usePublicRelease } from "../../../release-metadata";
import { formatIstDate, formatIstDateTime, formatIstTime } from "../../../date-time";
import Status from "../../../shared/ui/Status";

export default function PrintAgentPage({agents,enrollmentCode,busy,onGenerate,onRevoke,desktop,localAgentStatus,localAgentConflict,localAgentRevoked,localAgentNeedsRepair,localAgentEnrollmentIssue,localAgentOwnershipChecking,onConnect,onReconnectAfterRevocation,onCopyCode}:{
  agents:AgentDevice[];
  enrollmentCode:{code:string;expiresUtc:string}|null;
  busy:boolean;
  onGenerate:()=>void;
  onRevoke:(agent:AgentDevice)=>void;
  desktop:boolean;
  localAgentStatus:DesktopAgentStatus|null;
  localAgentConflict:boolean;
  localAgentRevoked:boolean;
  localAgentNeedsRepair:boolean;
  localAgentEnrollmentIssue:{code:string|null;message:string}|null;
  localAgentOwnershipChecking:boolean;
  onConnect:()=>void;
  onReconnectAfterRevocation:()=>void;
  onCopyCode:(value:string)=>void;
}) {
  const { release, state: releaseState } = usePublicRelease();
  const windowsInstallerUrl = release?.windowsAvailable ? getPublicDownloadPath("windows") : null;
  const connected = Boolean(localAgentStatus?.paired && agents.some(agent =>
    !agent.revokedUtc && agent.id === localAgentStatus.agentDeviceId
  ));
  return <div className="agent-page">
    <section className="panel agent-pairing-panel">
      {desktop ? <div className="desktop-agent-connect">
        <div className="agent-intro">
          <div className="agent-hero-icon"><Monitor size={22}/></div>
          <div><div className="eyebrow"><span className="eyebrow-line"/>THIS COMPUTER</div><h2>Your print computer</h2><p>SmartPrint connects this computer to your shop automatically. The background print service stays active even when you close this window.</p></div>
        </div>
        <div className="desktop-agent-status">
          <span className={connected ? "desktop-agent-indicator connected" : localAgentConflict ? "desktop-agent-indicator conflict" : "desktop-agent-indicator"} />
          <div><strong>{connected ? "Connected to this shop" : localAgentRevoked ? "Disconnected by the shop owner" : localAgentNeedsRepair ? "Connection needs repair" : localAgentEnrollmentIssue ? (localAgentConflict ? "Existing shop pairing needs revocation" : "Connection needs attention") : localAgentConflict ? "Connected to another shop" : localAgentOwnershipChecking ? "Checking shop registration" : localAgentStatus?.serviceReachable ? "Ready to connect" : "Print service not responding"}</strong>
            <small>{connected ? (localAgentStatus?.localApiReachable === false ? "Connected to your shop. Cloud printing remains available; local-network uploads are reconnecting automatically." : "Printer discovery and job hand-off are managed by the SmartPrint service.") : localAgentRevoked ? "This computer was revoked and will not reconnect automatically. Select Reconnect this computer when your shop is ready; no pairing code is needed." : localAgentNeedsRepair ? "Windows could not read this computer's saved pairing. Check the paired-computer list below; revoke its old entry if it is still active, then select Repair connection. No code entry is needed." : localAgentEnrollmentIssue ? localAgentEnrollmentIssue.message : localAgentConflict ? "This computer is still connected to another shop. Ask its previous owner to disconnect it before connecting it here." : localAgentOwnershipChecking ? "SmartPrint is verifying this computer's saved registration with your shop. It will not transfer or re-enroll it while ownership is uncertain." : localAgentStatus?.serviceReachable ? "Your owner session will authorize a one-time connection. No pairing code is shown." : "SmartPrint is starting or reconnecting its background service. It will retry automatically."}</small>
          </div>
        </div>
        <div className="desktop-agent-actions">
          {localAgentConflict ? (
            <button className="primary-button" disabled={busy} onClick={onReconnectAfterRevocation}>
              {busy ? <span className="button-spinner"/> : <><RotateCcw size={15}/> I’ve had the previous shop revoke this computer</>}
            </button>
          ) : localAgentOwnershipChecking ? (
            <span role="status" aria-live="polite">Checking shop registration…</span>
          ) : localAgentRevoked ? (
            <button className="primary-button" disabled={busy} onClick={onConnect}>
              {busy ? <span className="button-spinner"/> : <><RotateCcw size={15}/> Reconnect this computer</>}
            </button>
          ) : localAgentNeedsRepair ? (
            <button className="primary-button" disabled={busy} onClick={onConnect}>
              {busy ? <span className="button-spinner"/> : <><RotateCcw size={15}/> Repair connection</>}
            </button>
          ) : (
            <button className="primary-button" disabled={busy || connected || Boolean(localAgentStatus?.paired)} onClick={onConnect}>
              {busy ? <span className="button-spinner"/> : <><Monitor size={15}/>{localAgentStatus?.serviceReachable ? "Connect this computer" : "Retry connection"}</>}
            </button>
          )}
          <span>This computer will handle print orders for your shop while connected.</span>
        </div>
      </div> : <>
      <div className="agent-intro">
        <div className="agent-hero-icon"><Monitor size={22}/></div>
        <div><div className="eyebrow"><span className="eyebrow-line"/>LOCAL PRINT CONNECTIVITY</div><h2>Connect your shop computer</h2><p>Pair the Windows computer connected to your printers. SmartPrint will keep it ready to receive customer print orders.</p></div>
      </div>
      <div className="agent-steps">
        <div><span>01</span><strong>Generate a pairing code</strong><small>One-time code, valid for 10 minutes.</small></div>
        <div><span>02</span><strong>Run the guided Windows installer</strong><small>Enter the code in the setup wizard. No PowerShell commands are required.</small></div>
        <div><span>03</span><strong>Confirm the connection</strong><small>Return here and wait for the computer heartbeat and printer discovery.</small></div>
      </div>
      {enrollmentCode&&<div className="agent-code-panel">
        <div><span>ONE-TIME PAIRING CODE</span><strong>{enrollmentCode.code}</strong><small>Expires {formatIstTime(enrollmentCode.expiresUtc)}</small></div>
        <button className="secondary-button" onClick={()=>onCopyCode(enrollmentCode.code)}><Copy size={14}/> Copy code</button>
      </div>}
      <div className="agent-command-panel">
        <div><span>GUIDED WINDOWS INSTALLER</span><strong>Download and run SmartPrint Windows Setup on the shop computer.</strong><small>Run setup as administrator, enter the one-time pairing code in its wizard, and let it install and start the background service.</small></div>
        {windowsInstallerUrl
          ? <a className="secondary-button" href={windowsInstallerUrl}><Monitor size={14}/> Download Windows installer</a>
          : <span className="subtle-button" aria-disabled="true">{releaseState === "loading" ? "Checking installer availability…" : releaseState === "error" ? "Installer availability temporarily unavailable" : "Windows installer not published"}</span>}
      </div>
      <div className="agent-security-note"><ShieldCheck size={16}/><span>Your setup code works once and expires after 10 minutes. You can disconnect a computer from this page if it is replaced or lost.</span></div>
      <div className="agent-generate-row"><span>Pairing codes are single use. Generating a new one does not revoke already paired computers.</span><button className="primary-button" disabled={busy} onClick={onGenerate}>{busy?<span className="button-spinner"/>:<><Plus size={15}/> Generate pairing code</>}</button></div>
      </>}
    </section>
    {desktop && <ShopConnectionPanel connected={connected} localApiReady={localAgentStatus?.localApiReachable===true}/>}
    {!desktop && <section className="panel local-network-hint"><div className="local-network-mark"><Wifi size={20}/></div><div><h2>Shop connection</h2><p>Open SmartPrint on the Windows computer connected to your printers to choose and test the shop Wi-Fi.</p></div></section>}
    <section className="panel agent-device-panel">
      <div className="collection-header"><div><h2>Paired Windows computers</h2><p>Agent connection history and the number of discovered printer queues.</p></div><span className="setup-count">{agents.filter(agent=>!agent.revokedUtc).length} paired</span></div>
      {agents.length===0?<div className="agent-empty"><Monitor size={25}/><strong>No Windows computer paired yet</strong><span>Generate a pairing code above and run the agent on the computer connected to your printers.</span></div>:
        <div className="agent-device-list">{agents.map(agent=><article className="agent-device-card" key={agent.id}>
          <div className="agent-device-icon"><Monitor size={19}/></div>
          <div className="agent-device-copy"><strong>{agent.deviceName}</strong><span>{agent.printerCount} printer {agent.printerCount===1?"queue":"queues"} discovered · Paired {formatIstDate(agent.createdUtc)}</span><small>{agent.lastSeenUtc?("Last contact "+formatIstDateTime(agent.lastSeenUtc)):"No heartbeat received yet"}</small>{agent.localEndpoints?.length?<small>Shop connection found · run the phone check on the shop computer</small>:<small>Connect this computer to the shop Wi-Fi to enable phone uploads</small>}</div>
          <Status label={agent.revokedUtc?"Revoked":agent.status} tone={agent.revokedUtc?"red":agent.status==="Online"?"green":"slate"}/>
          {!agent.revokedUtc&&<button className="subtle-button reject-action" disabled={busy} onClick={()=>onRevoke(agent)}>Revoke</button>}
        </article>)}</div>}
    </section>
  </div>;
}

function ShopConnectionPanel({connected,localApiReady}:{connected:boolean;localApiReady:boolean}) {
  const [snapshot,setSnapshot]=useState<DesktopLocalNetworkSnapshot|null>(null);
  const [agentStatus,setAgentStatus]=useState<DesktopAgentStatus|null>(null);
  const [loading,setLoading]=useState(true);
  const [saving,setSaving]=useState(false);
  const [error,setError]=useState("");
  const [test,setTest]=useState<DesktopLocalPhoneTest|null>(null);
  const [verified,setVerified]=useState(false);
  const [expired,setExpired]=useState(false);

  async function refresh() {
    try {
      const [next,status]=await Promise.all([getLocalNetworkSnapshot(),getLocalAgentStatus()]);
      setSnapshot(next);
      setAgentStatus(status);
      setVerified(Boolean(next.phoneVerifiedUtc));
      setError("");
      return next;
    } catch(exception) {
      setError(exception instanceof Error?exception.message:"Could not check the shop connection.");
      return null;
    } finally { setLoading(false); }
  }

  useEffect(()=>{void refresh();},[]);

  useEffect(()=>{
    if(!connected||!localApiReady) {
      setTest(null);
      setVerified(false);
      setExpired(false);
      return;
    }
    void refresh();
  },[connected,localApiReady]);

  useEffect(()=>{
    if(!test) {
      const timer=window.setInterval(()=>void refresh(),30000);
      return ()=>window.clearInterval(timer);
    }
    let active=true;
    let polling=false;
    const started=Date.parse(test.startedUtc);
    const poll=async()=>{
      if(polling||!active)return;
      if(Date.now()>=Date.parse(test.expiresUtc)){
        setExpired(true);
        setTest(null);
        return;
      }
      polling=true;
      try {
        const next=await getLocalNetworkSnapshot();
        if(!active)return;
        setSnapshot(next);
        if(next.phoneVerifiedUtc&&Date.parse(next.phoneVerifiedUtc)>=started){
          setVerified(true);setTest(null);
        }
      }catch{
        // The QR remains visible while the Agent briefly restarts or reconnects.
        // Expiry is checked before the pipe call, so a stopped service cannot
        // leave a disabled test button on screen indefinitely.
      }finally{polling=false;}
    };
    const timer=window.setInterval(()=>void poll(),2000);
    return ()=>{active=false;window.clearInterval(timer);};
  },[test?.startedUtc]);

  async function changeNetwork(interfaceId:string) {
    if(!interfaceId)return;
    setSaving(true);setError("");setTest(null);setVerified(false);setExpired(false);
    try {setSnapshot(await selectLocalNetwork(interfaceId));}
    catch(exception){setError(exception instanceof Error?exception.message:"Could not select this connection.");}
    finally{setSaving(false);}
  }

  async function beginPhoneTest() {
    setSaving(true);setError("");setExpired(false);setVerified(false);
    try {setTest(await startLocalPhoneTest());}
    catch(exception){setError(exception instanceof Error?exception.message:"Could not start the phone check.");}
    finally{setSaving(false);}
  }

  const selected=snapshot?.networks.find(network=>network.id===snapshot.selectedNetworkId)??null;
  const hasSelectedNetwork=Boolean(snapshot?.selectedNetworkId && selected);
  const phoneTestPassed=verified&&localApiReady;
  const readiness=agentStatus?.readiness??null;
  const pipelineReady=readiness?.localUploadReady===true;
  const statusTone=phoneTestPassed&&pipelineReady?"green":hasSelectedNetwork||snapshot?.networks.length?"amber":"slate";
  const statusLabel=loading?"Checking…":error&&!snapshot?"Connection unavailable":connected&&!localApiReady?"Print service starting":localApiReady&&!readiness?"Agent update needed":localApiReady&&!pipelineReady?"Print pipeline recovering":phoneTestPassed?"Phone test passed":hasSelectedNetwork?"Phone check needed":snapshot?.networks.length?"Choose connection":"No connection found";

  return <section className="panel local-network-panel">
    <div className="local-network-heading">
      <div><span className="eyebrow"><span className="eyebrow-line"/>SHOP CONNECTION</span><h2>Customer phone connection</h2><p>Choose the connection customers use at your shop, then check it with a phone.</p></div>
      <Status label={loading?"Checking…":statusLabel} tone={loading?"slate":statusTone}/>
    </div>
    {loading&&!snapshot?<div className="local-network-loading"><span className="button-spinner"/> Checking available connections…</div>:error&&!snapshot
      ? <div className="local-network-empty"><Wifi size={20}/><div><strong>Could not check this computer</strong><p>SmartPrint is not responding yet. Make sure the desktop app is open, then try again.</p></div><button className="secondary-button" type="button" onClick={()=>void refresh()}>Try again</button></div>
      : <>
      {snapshot?.networks.length?<>
        <label className="local-network-choice">Shop Wi-Fi or network
          <select value={snapshot.selectedNetworkId??""} onChange={event=>void changeNetwork(event.target.value)} disabled={saving||!connected}>
            <option value="">Choose the connection customers use</option>
            {snapshot.networks.map(network=><option key={network.id} value={network.id}>{network.displayName}{network.kind&&network.displayName.toLowerCase()!==network.kind.toLowerCase()?" · "+network.kind:""}</option>)}
          </select>
        </label>
        {selected
          ? <div className="local-network-selected"><Wifi size={17}/><span><strong>{selected.displayName}</strong><small>{selected.kind} · Used for customer document uploads</small></span><Check size={17}/></div>
          : <p className="local-network-copy">Your previous connection is not available. Choose the Wi-Fi used by your customers before testing phone access.</p>}
        {!connected?<p className="local-network-copy">Connect this computer to your shop first. The phone check will be available when setup is complete.</p>:!localApiReady?<p className="local-network-copy">SmartPrint is preparing the shop connection. The phone check will be available when the print service is ready.</p>:<>
          <div className="local-network-actions">
            <button className="primary-button" type="button" onClick={()=>void beginPhoneTest()} disabled={saving||!!test||!hasSelectedNetwork||!localApiReady}>
              {saving?<span className="button-spinner"/>:<><QrCode size={16}/> Test with a phone</>}
            </button>
            <button className="secondary-button" type="button" onClick={()=>void refresh()} disabled={saving}><RotateCcw size={15}/> Refresh</button>
          </div>
          {test&&<div className="local-phone-test">
            <div className="local-phone-qr"><QRCode value={test.phoneTestUrl} size={190} level="M" bgColor="#ffffff" fgColor="#111827"/></div>
            <div><strong>Scan with SmartPrint on your phone</strong><p>On the phone, open SmartPrint → Your space → connected shop → Check shop Wi-Fi, then scan this code. This screen will confirm automatically when the connection works.</p><small>Using a phone without SmartPrint? Scan with its camera and open the link. This check expires in five minutes.</small></div>
          </div>}
          {phoneTestPassed&&<div className="local-network-result success"><Check size={17}/><span><strong>Phone test passed</strong><small>A phone reached this shop connection recently. The check expires after ten minutes.</small></span></div>}
          {localApiReady&&readiness?.localUploadReady===true&&<div className="local-network-result success"><Check size={17}/><span><strong>Local print pipeline ready</strong><small>Protected queue, print worker, cloud heartbeat and printer inventory are healthy. The assigned printer is checked separately for each QR station upload.</small></span></div>}
          {localApiReady&&!readiness&&<div className="local-network-result warning"><Clock3 size={17}/><span><strong>Agent update required</strong><small>This Agent version does not report queue and print-worker readiness. Update the Windows Print Agent to verify local upload readiness.</small></span></div>}
          {localApiReady&&readiness&&!readiness.localUploadReady&&<div className="local-network-result warning"><Clock3 size={17}/><span><strong>Print pipeline recovering</strong><small>{[
            readiness.queueInitialized?"":"Protected document queue is not ready",
            readiness.printWorkerRunning?"":"Print worker is not running",
            readiness.heartbeatFresh?"":"Cloud heartbeat is stale",
            readiness.printerInventoryFresh?"":"Printer inventory is stale"
          ].filter(Boolean).join(" · ")} {readiness.lastFailureCode?"(Last issue: "+readiness.lastFailureCode+") ":""}Retry after the shop computer/Agent has recovered.</small></span></div>}
          {expired&&<div className="local-network-result warning"><Clock3 size={17}/><span><strong>Phone not connected yet</strong><small>Make sure the phone is on this same shop Wi-Fi, then start a new check. You can also try another connection above.</small></span></div>}
        </>}
      </>:<div className="local-network-empty"><Wifi size={20}/><div><strong>No shop connection found</strong><p>Connect this computer to the Wi-Fi or network you use at the shop, then refresh.</p></div><button className="secondary-button" type="button" onClick={()=>void refresh()}>Refresh</button></div>}
    </>}
    {error&&<div className="local-network-error" role="alert">{error}</div>}
  </section>;
}
