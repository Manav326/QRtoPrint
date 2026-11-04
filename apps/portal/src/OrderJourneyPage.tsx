
import { useMemo } from "react";
import { Activity, AlertTriangle, ArrowRight, Check, Clock3, CreditCard, FileText, Inbox, PackageCheck, Printer, RefreshCw, ShieldCheck, Wallet, Wifi } from "lucide-react";
import type { AgentDevice, PrinterDevice, ShopPayment, ShopPrintJob } from "./api";
import { formatIstDateTime } from "./date-time";
import { buildShopOrderJourneys, orderJourneyStageLabel, orderJourneyStatusLabel, type OrderJourneyStage } from "./order-journey";
import styles from "./OrderJourney.module.css";

type ShopDestination = "Print Queue" | "Payments" | "Print Agent";
interface Props {
  jobs: ShopPrintJob[]; payments: ShopPayment[]; printers: PrinterDevice[]; agents: AgentDevice[];
  refreshing: boolean; onRefresh: () => void; onNavigate: (section: ShopDestination) => void;
  onReviewBatch: (batchId: string) => void; onVerifyPayment: (payment: ShopPayment) => void;
}
const readyStatuses = new Set(["COMPLETED","PDF_EXPORTED"]);
const flowSteps: Array<{id:OrderJourneyStage;title:string;detail:string;icon:typeof Inbox}> = [
  {id:"received",title:"Received",detail:"Files arrive safely",icon:Inbox},
  {id:"review",title:"Review",detail:"Accept or exclude files",icon:FileText},
  {id:"payment",title:"Payment",detail:"Confirm the real payment",icon:Wallet},
  {id:"printing",title:"Printing",detail:"Agent sends to printer",icon:Printer},
  {id:"ready",title:"Ready",detail:"Output checked and complete",icon:PackageCheck}
];
function money(value:number,currency:string){return new Intl.NumberFormat("en-IN",{style:"currency",currency:currency||"INR",maximumFractionDigits:2}).format(Math.max(0,value)/100);}
function paymentLabel(payment:ShopPayment|null,stage:OrderJourneyStage){
  if(!payment)return stage==="payment"?"Checkout not started":"Not due yet";
  switch(payment.status){
    case "AWAITING_CUSTOMER_PAYMENT":return "Waiting for customer payment";
    case "CUSTOMER_REPORTED":return "Customer reported payment · verify";
    case "CASH_AWAITING_PAYMENT":return "Cash expected at counter";
    case "VERIFIED":return "Payment verified";
    case "PARTIALLY_REFUNDED":return "Verified · partial refund";
    case "REJECTED":return "Payment rejected · customer can retry";
    default:return payment.status.replaceAll("_"," ").toLowerCase();
  }
}
function isAgentOnline(agent:AgentDevice){return !agent.revokedUtc&&agent.status==="Online"&&!!agent.lastSeenUtc&&Date.parse(agent.lastSeenUtc)>Date.now()-120000;}
function printerSummary(jobs:ShopPrintJob[],printers:PrinterDevice[],agents:AgentDevice[]){
  const acceptedJobs=jobs.filter(job=>!["REJECTED","REFUNDED","CANCELLED"].includes(job.status));
  const ids=new Set(acceptedJobs.map(job=>job.printerDeviceId).filter((id):id is string=>!!id));
  if(!ids.size)return {name:"Printer not assigned",detail:"Assign one in the print queue after payment verification",ready:false};
  if(ids.size>1)return {name:"Multiple printers assigned",detail:"Accepted files in this batch must use one printer before dispatch",ready:false};
  const assigned=printers.filter(printer=>ids.has(printer.id));
  const online=new Set(agents.filter(isAgentOnline).map(agent=>agent.id));
  const connected=assigned.filter(printer=>printer.isActive&&!!printer.agentDeviceId&&online.has(printer.agentDeviceId));
  const names=Array.from(new Set(assigned.map(printer=>printer.displayName).filter(Boolean)));
  return connected.length
    ? {name:names.join(", ")||"Assigned printer",detail:connected.length+" connected · Windows Agent recently online",ready:true}
    : {name:names.join(", ")||"Assigned printer",detail:"Printer assigned · waiting for its shop computer",ready:false};
}
function PrinterVisual({stage,attention}:{stage:OrderJourneyStage;attention:boolean}){
  const printing=stage==="printing"&&!attention,ready=stage==="ready";
  return <div role="img" className={styles.machineVisual+(printing?" "+styles.machinePrinting:"")+(ready?" "+styles.machineReady:"")} aria-label={attention?"Printing needs attention":ready?"Print output complete":printing?"Print job in progress":"Print workflow illustration"}>
    <div className={styles.paper}><span/><span/><span/><span/><i>{ready?<Check size={13}/>:<FileText size={13}/>}</i></div>
    <div className={styles.printerTop}><div className={styles.printerTopLine}/><div className={styles.printerStatusDot}/></div>
    <div className={styles.printerBody}><div className={styles.printerDisplay}><span/>{attention?"!":ready?"OK":printing?"RUN":"READY"}</div><div className={styles.printerSlot}/><div className={styles.printerTray}><span/><span/><span/></div></div><div className={styles.printerBase}/>
  </div>;
}
function OrderCard({journey,printers,agents,onNavigate,onReviewBatch,onVerifyPayment}:{
  journey:ReturnType<typeof buildShopOrderJourneys>[number];printers:PrinterDevice[];agents:AgentDevice[];
  onNavigate:(section:ShopDestination)=>void;onReviewBatch:(batchId:string)=>void;onVerifyPayment:(payment:ShopPayment)=>void;
}){
  const printer=printerSummary(journey.jobs,printers,agents),canReview=journey.canReview&&!!journey.batchId;
  const headline=journey.isClosed?"No files moving forward":journey.attentionJobs.length?"Needs a quick check":
    journey.stage==="received"?"New order received":journey.stage==="review"?"Your review is next":
    journey.stage==="payment"?(journey.payment?.status==="CUSTOMER_REPORTED"?"Payment reported · verification needed":"Waiting for payment"):
    journey.stage==="printing"?"Order is being prepared":
    journey.jobs.every(job=>job.status==="PDF_EXPORTED")?"PDF output saved":"Ready for collection";
  const primary=journey.isClosed?"Open print queue":canReview?"Review files":journey.canVerifyPayment?"Verify payment":
    journey.stage==="payment"?"View order in queue":journey.stage==="ready"?"View completed order":"Open print queue";
  const activate=()=>{if(canReview&&journey.batchId){onReviewBatch(journey.batchId);return;}if(journey.canVerifyPayment&&journey.payment){onVerifyPayment(journey.payment);return;}onNavigate("Print Queue");};
  const stageIndex=journey.isClosed?1:journey.stageIndex,files=journey.jobs.slice(0,3),hidden=Math.max(0,journey.jobs.length-files.length);
  return <article className={styles.orderCard}>
    <div className={styles.orderCardTop}><div className={styles.orderIdentity}>
      <div className={styles.orderEyebrow}><span>ORDER</span><span className={styles.orderReference}>{journey.reference}</span>{journey.batchId&&<span className={styles.batchBadge}>{journey.jobs.length} files</span>}</div>
      <h3>{journey.name}</h3><p><Clock3 size={13}/> Received {formatIstDateTime(journey.createdUtc)}</p>
    </div><div className={styles.currentStatus+" "+styles["tone"+journey.stage[0].toUpperCase()+journey.stage.slice(1)]}><span/><strong>{headline}</strong></div></div>
    <div className={styles.miniProgress} aria-label={"Order progress: "+orderJourneyStageLabel(journey.stage)}>
      {flowSteps.map((step,index)=><span key={step.id} className={styles.progressSegment+(index<=stageIndex?" "+styles.progressComplete:"")+(index===stageIndex?" "+styles.progressCurrent:"")}/>)}
    </div>
    <div className={styles.orderFacts}>
      <div><strong>{journey.acceptedCount}</strong><span>files included</span></div><div><strong>{journey.rejectedCount}</strong><span>excluded</span></div>
      <div><strong>{journey.pageCount}</strong><span>pages to produce</span></div>
      <div><strong>{money(journey.payment?.amountMinor??journey.quotedAmountMinor,journey.currency)}</strong><span>{journey.payment?"payment total":"quoted value"}</span></div>
    </div>
    <div className={styles.orderCardContent}>
      <div className={styles.fileList}><div className={styles.sectionMicroHeading}>FILES IN THIS ORDER</div>
        {files.map(job=><div className={styles.fileRow} key={job.id}>
          <span className={styles.fileGlyph}><FileText size={15}/></span>
          <span className={styles.fileName} title={job.document.originalFileName}><strong>{job.document.originalFileName}</strong><small>{job.pageCount} pages · {job.copies} {job.copies===1?"copy":"copies"}</small></span>
          <span className={styles.fileState+(job.status==="REJECTED"||job.status==="REFUNDED"?" "+styles.fileExcluded:"")+(readyStatuses.has(job.status)?" "+styles.fileReady:"")}>{orderJourneyStatusLabel(job.status)}</span>
        </div>)}
        {hidden>0&&<div className={styles.moreFiles}>+ {hidden} more files in this batch</div>}
        {journey.refundedCount>0&&<div className={styles.fileFootnote}>{journey.refundedCount} file(s) refunded</div>}
      </div>
      <div className={styles.orderSignals}>
        <div className={styles.signalRow}><span className={styles.signalIcon+(journey.payment?.status==="VERIFIED"||journey.payment?.status==="PARTIALLY_REFUNDED"?" "+styles.signalGood:"")}><CreditCard size={15}/></span><div><strong>Payment</strong><small>{paymentLabel(journey.payment,journey.stage)}</small></div></div>
        <div className={styles.signalRow}><span className={styles.signalIcon+(printer.ready?" "+styles.signalGood:"")}><Printer size={15}/></span><div><strong>{printer.name}</strong><small>{printer.detail}</small></div></div>
        {journey.attentionJobs.length>0&&<div className={styles.attentionLine}><AlertTriangle size={14}/><span>{journey.attentionJobs.length} file(s) need attention. Do not mark these as ready.</span></div>}
      </div>
    </div>
    <div className={styles.orderCardBottom}><div className={styles.nextStep}><span>NEXT STEP</span><strong>{journey.isClosed?"Review the recorded decisions":journey.attentionJobs.length?"Check the output and resolve the issue":
      journey.stage==="received"?"The upload is safely in the shop workflow. Review starts when file checks finish.":
      journey.stage==="review"?"Review each file. Every file starts included; rejected files stay out of printing.":
      journey.stage==="payment"?"Verify payment in your bank or at the cash counter before printing.":
      journey.stage==="printing"?"The shop computer receives secured files and submits each authorised job once.":
      "Physical orders are ready only after the actual paper has been checked."}</strong></div>
      <button className={styles.actionButton} type="button" onClick={activate}>{journey.canVerifyPayment?<ShieldCheck size={15}/>:journey.stage==="ready"?<PackageCheck size={15}/>:<ArrowRight size={15}/>} {primary}</button>
    </div>
  </article>;
}
export default function OrderJourneyPage({jobs,payments,printers,agents,refreshing,onRefresh,onNavigate,onReviewBatch,onVerifyPayment}:Props){
  const journeys=useMemo(()=>buildShopOrderJourneys(jobs,payments),[jobs,payments]);
  const counts=useMemo(()=>({
    received:journeys.filter(j=>j.stage==="received"&&!j.isClosed).length,
    review:journeys.filter(j=>j.stage==="review"&&!j.isClosed).length,
    payment:journeys.filter(j=>j.stage==="payment"&&!j.isClosed).length,
    printing:journeys.filter(j=>j.stage==="printing"&&!j.isClosed).length,
    ready:journeys.filter(j=>j.stage==="ready").length
  }),[journeys]);
  const agentsOnline=agents.filter(isAgentOnline).length;
  const reachable=printers.filter(p=>p.isActive&&!!p.agentDeviceId&&agents.some(a=>a.id===p.agentDeviceId&&isAgentOnline(a))).length;
  const inMotion=journeys.filter(j=>!j.isClosed).length;
  const visualJourney=journeys.find(j=>j.attentionJobs.length>0)??journeys.find(j=>j.stage==="printing")??journeys[0];
  return <div className={styles.journeyPage}>
    <header className={styles.pageHero}><div><div className={styles.eyebrow}><span className={styles.livePulse}/> LIVE ORDER CONTROL</div>
      <h2>From customer upload to print handover.</h2><p>One clear view of every recent order, what is happening now, and what needs your attention next.</p></div>
      <div className={styles.heroActions}><span className={styles.liveLabel}><span/> Live shop view</span><button type="button" className={styles.refreshButton} onClick={onRefresh} disabled={refreshing}><RefreshCw size={15} className={refreshing?styles.spinning:""}/>{refreshing?"Refreshing":"Refresh"}</button></div>
    </header>
    <section className={styles.flowPanel} aria-label="The order process">
      <div className={styles.flowPanelHeader}><div><span className={styles.panelEyebrow}>THE PRINT JOURNEY</span><h3>Every order follows the same clear path</h3></div><span className={styles.flowPanelHint}>Stage counts update with the shop queue</span></div>
      <div className={styles.flowSteps}>{flowSteps.map((step,index)=>{const Icon=step.icon,count=counts[step.id];return <div className={styles.flowStepWrap} key={step.id}>
        <div className={styles.flowStep+(count>0?" "+styles.flowStepActive:"")}><div className={styles.flowIcon}><Icon size={19}/></div>
          <div className={styles.flowStepCopy}><strong>{step.title}</strong><small>{count} {count===1?"order":"orders"}</small></div><span className={styles.flowStepDetail}>{step.detail}</span>
        </div>{index<flowSteps.length-1&&<span className={styles.flowConnector} aria-hidden="true"><ArrowRight size={15}/></span>}</div>;})}</div>
      <div className={styles.flowLegend}><div className={styles.legendCopy}><ShieldCheck size={16}/><span><strong>Safe by design</strong> — only accepted files move forward, and printing is never triggered just by receiving payment.</span></div>
        <PrinterVisual stage={visualJourney?.stage??"received"} attention={(visualJourney?.attentionJobs.length??0)>0}/></div>
    </section>
    <section className={styles.summaryStrip} aria-label="Shop order summary">
      <div className={styles.summaryItem}><span className={styles.summaryIcon}><Activity size={17}/></span><div><strong>{inMotion}</strong><small>Orders in motion</small></div></div>
      <div className={styles.summaryItem}><span className={styles.summaryIcon+" "+styles.summaryViolet}><FileText size={17}/></span><div><strong>{counts.review}</strong><small>Need file review</small></div></div>
      <div className={styles.summaryItem}><span className={styles.summaryIcon+" "+styles.summaryAmber}><CreditCard size={17}/></span><div><strong>{journeys.filter(j=>j.canVerifyPayment).length}</strong><small>Payments to verify</small></div></div>
      <div className={styles.summaryItem}><span className={styles.summaryIcon+" "+styles.summaryCyan}><Printer size={17}/></span><div><strong>{counts.printing}</strong><small>Orders printing</small></div></div>
      <div className={styles.summaryItem}><span className={styles.summaryIcon+" "+styles.summaryGreen}><PackageCheck size={17}/></span><div><strong>{counts.ready}</strong><small>Ready / completed</small></div></div>
    </section>
    <section className={styles.operationsStrip} aria-label="Printer and Agent readiness">
      <div className={styles.operationsLabel}><span className={styles.operationsGlyph}><Wifi size={16}/></span><div><strong>Shop floor readiness</strong><small>Live connection to the local Windows computer</small></div></div>
      <div className={styles.operationMetric}><strong>{agentsOnline}</strong><span>Agents online</span></div>
      <div className={styles.operationMetric}><strong>{reachable}</strong><span>Printers reachable</span></div>
      <button type="button" className={styles.subtleLink} onClick={()=>onNavigate(agentsOnline>0?"Print Queue":"Print Agent")}>{agentsOnline>0?"Check print queue":"Connect shop computer"} <ArrowRight size={14}/></button>
    </section>
    <section className={styles.ordersSection}><div className={styles.ordersHeading}><div><span className={styles.panelEyebrow}>YOUR CUSTOMERS</span><h3>Recent order journeys</h3><p>Files stay grouped by their original batch, so one customer's order never gets mixed with another.</p></div><span className={styles.ordersCount}>{journeys.length} recent {journeys.length===1?"order":"orders"}</span></div>
      {journeys.length===0?<div className={styles.emptyState}><div className={styles.emptyIllustration}><div className={styles.emptySheet}><span/><span/><span/></div><div className={styles.emptyPrinter}><Printer size={27}/></div><i><Check size={13}/></i></div>
        <h3>Your first customer order will appear here</h3><p>Share a QR station with your customers. Their uploaded files will appear here as one easy-to-follow journey.</p>
        <button type="button" className={styles.actionButton} onClick={()=>onNavigate("Print Queue")}>Open print queue <ArrowRight size={15}/></button></div>
      :<div className={styles.orderList}>{journeys.map(j=><OrderCard key={j.key} journey={j} printers={printers} agents={agents} onNavigate={onNavigate} onReviewBatch={onReviewBatch} onVerifyPayment={onVerifyPayment}/>)}</div>}
    </section>
    <footer className={styles.journeyFooter}><span>Output accuracy matters.</span> A printer accepting a job does not prove paper was produced. Physical orders become complete only after the shop confirms the actual pages.</footer>
  </div>;
}
