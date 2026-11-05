import { useEffect, useState } from "react";
import { Check, Printer, RotateCcw, ShieldCheck, Wallet, Zap } from "lucide-react";
import type { ShopPayment, ShopPaymentSettings, ShopRefund } from "../../../api";
import { formatIstDateTime } from "../../../date-time";
import { formatMoney } from "../../../shared/lib/formatting";
import EmptyModule from "../../../shared/ui/EmptyModule";
import Status from "../../../shared/ui/Status";

export interface PaymentSettingsUpdate {
  upiEnabled: boolean;
  upiId: string | null;
  payeeName: string | null;
  cashEnabled: boolean;
  instantPaymentEnabled: boolean;
}

interface PaymentsPageProps {
  settings: ShopPaymentSettings;
  payments: ShopPayment[];
  refunds: ShopRefund[];
  onSave: (values: PaymentSettingsUpdate) => void;
  onVerify: (payment: ShopPayment) => void;
  onReject: (payment: ShopPayment) => void;
  busy: boolean;
  busyJobId: string | null;
}

export default function PaymentsPage({settings,payments,refunds,onSave,onVerify,onReject,busy,busyJobId}: PaymentsPageProps) {
  const [upiEnabled,setUpiEnabled]=useState(settings.upiEnabled);
  const [upiId,setUpiId]=useState(settings.upiId||"");
  const [payeeName,setPayeeName]=useState(settings.payeeName||"");
  const [cashEnabled,setCashEnabled]=useState(settings.cashEnabled);
  const [instantPaymentEnabled,setInstantPaymentEnabled]=useState(settings.instantPaymentEnabled);
  useEffect(()=>{
    setUpiEnabled(settings.upiEnabled);
    setUpiId(settings.upiId||"");
    setPayeeName(settings.payeeName||"");
    setCashEnabled(settings.cashEnabled);
    setInstantPaymentEnabled(settings.instantPaymentEnabled);
  },[settings.upiEnabled,settings.upiId,settings.payeeName,settings.cashEnabled,settings.instantPaymentEnabled]);

  const pending=payments.filter(payment=>payment.status==="AWAITING_CUSTOMER_PAYMENT"||payment.status==="CUSTOMER_REPORTED"||payment.status==="CASH_AWAITING_PAYMENT");
  return <div className="payments-workspace">
    <section className="panel payment-settings-panel">
      <div className="collection-header"><div><h2>Shop payment methods</h2><p>Customer print payments go directly to your own UPI account or are received at your counter.</p></div><span className="setup-count">{(upiEnabled?1:0)+(cashEnabled?1:0)} enabled</span></div>
      <div className="payment-method-card">
        <div className="payment-method-heading"><div className="payment-method-icon"><Wallet size={19}/></div><div><strong>UPI · QR and payment app</strong><small>Configure your shop's own UPI ID. Payments are not auto-verified.</small></div><label className="switch"><input type="checkbox" checked={upiEnabled} onChange={event=>setUpiEnabled(event.target.checked)}/><span/></label></div>
        {upiEnabled&&<div className="payment-settings-fields">
          <label>Shop UPI ID<input value={upiId} onChange={event=>setUpiId(event.target.value)} placeholder="yourshop@bank"/></label>
          <label>Payee / business name<input value={payeeName} onChange={event=>setPayeeName(event.target.value)} maxLength={120} placeholder="Name shown in customer's UPI app"/></label>
        </div>}
      </div>
      <div className="payment-method-card">
        <div className="payment-method-heading"><div className="payment-method-icon"><Printer size={19}/></div><div><strong>Cash at counter</strong><small>{cashEnabled?"Enabled · customers can pay at the counter.":"Disabled · customers will not see cash at the counter."}</small></div><label className="switch"><input type="checkbox" checked={cashEnabled} onChange={event=>setCashEnabled(event.target.checked)}/><span/></label></div>
      </div>
      <div className="payment-method-card instant-payment-setting">
        <div className="payment-method-heading"><div className="payment-method-icon"><Zap size={19}/></div><div><strong>Instant payment after upload</strong><small>Customers can pay as soon as the rate-card quote is calculated, without a separate shop pre-approval. Once payment starts, file decisions are locked; after payment is verified, refund rejected files before printer handoff.</small></div><label className="switch"><input type="checkbox" checked={instantPaymentEnabled} onChange={event=>setInstantPaymentEnabled(event.target.checked)}/><span/></label></div>
        <p className="instant-payment-setting-note">Requires your print rate card and at least one payment method. UPI references and cash are still verified by the shop before printing. Leave this off when you want to review and approve every quote before customers can pay.</p>
      </div>
      <div className="payment-security-note"><ShieldCheck size={16}/><span>SmartPrint by QuantaEdge does not receive, hold or route your customer print revenue. Verify every UPI reference against your own bank/UPI app; cash must be physically received. SmartPrint platform subscription charges are billed separately.</span></div>
      <div className="payment-settings-footer"><span>Last updated {formatIstDateTime(settings.updatedUtc)}</span><button className="primary-button" disabled={busy||(!upiEnabled&&!cashEnabled)||(upiEnabled&&(!upiId.trim()||!payeeName.trim()))} onClick={()=>onSave({upiEnabled,upiId:upiEnabled?upiId.trim():null,payeeName:upiEnabled?payeeName.trim():null,cashEnabled,instantPaymentEnabled})}>{busy?<span className="button-spinner"/>:"Save payment methods"}</button></div>
    </section>
    <section className="panel payment-review-panel">
      <div className="collection-header"><div><h2>Payment verification</h2><p>Review UPI references and confirm cash collected before a job becomes payment-authorised.</p></div><span className="setup-count">{pending.length} need review</span></div>
      {payments.length===0?<EmptyModule icon={<Wallet size={24}/>} title="No print payments yet" text="When customers choose UPI or cash for a shop-approved job, the payment attempt will appear here."/>:
      <div className="payment-record-list">{payments.map(payment=><article className="payment-record" key={payment.id}>
        <div className="payment-record-head"><div><strong>{payment.batchPaymentId?(`Batch checkout · ${payment.batchFileCount??1} files`):payment.jobNumber}</strong><small>{payment.documentName}</small></div><div className="payment-record-amount-wrap"><div className="payment-record-amount">{formatMoney(payment.amountMinor,payment.currency)}</div>{(payment.refundedAmountMinor??0)>0&&<small className="payment-record-refund-summary">Refund recorded: {formatMoney(payment.refundedAmountMinor??0,payment.currency)} · Net retained: {formatMoney(Math.max(0,payment.amountMinor-(payment.refundedAmountMinor??0)),payment.currency)}</small>}</div></div>
        <div className="payment-record-meta"><span>{payment.method}</span><span>{formatIstDateTime(payment.createdUtc)}</span><Status label={payment.status.replaceAll("_"," ")} tone={payment.status==="VERIFIED"?"green":payment.status==="REJECTED"?"red":payment.status==="CUSTOMER_REPORTED"||payment.status==="CASH_AWAITING_PAYMENT"?"amber":"slate"}/></div>
        {payment.method==="UPI"&&<div className="payment-reference-details"><span>Transaction reference</span><strong>{payment.paymentReference||"Customer has not submitted a reference"}</strong><small>Payee snapshot: {payment.payeeNameSnapshot||"Not recorded"} · {payment.upiIdSnapshot||"No UPI ID snapshot"}</small></div>}
        {payment.rejectionReason&&<div className="payment-rejection-note">Reason: {payment.rejectionReason}</div>}
        {(payment.status==="AWAITING_CUSTOMER_PAYMENT"||payment.status==="CUSTOMER_REPORTED"||payment.status==="CASH_AWAITING_PAYMENT")&&<div className="payment-review-actions">{payment.status==="AWAITING_CUSTOMER_PAYMENT"&&<button className="secondary-button reject-action" disabled={busy||busyJobId===payment.printJobId} onClick={()=>onReject(payment)}>Cancel attempt</button>}{payment.status==="CASH_AWAITING_PAYMENT"&&<button className="secondary-button reject-action" disabled={busy||busyJobId===payment.printJobId} onClick={()=>onReject(payment)}>{payment.batchPaymentId?"Cancel batch cash attempt":"Cancel cash attempt"}</button>}{payment.status==="CUSTOMER_REPORTED"&&<button className="secondary-button reject-action" disabled={busy||busyJobId===payment.printJobId} onClick={()=>onReject(payment)}>{payment.batchPaymentId?"Reject batch report":"Reject report"}</button>}<button className="primary-button" disabled={busy||busyJobId===payment.printJobId} onClick={()=>onVerify(payment)}>{busyJobId===payment.printJobId?<span className="button-spinner"/>:payment.method==="UPI"?(payment.batchPaymentId?"Verify full batch in bank app":"Verify in bank app"):(payment.batchPaymentId?"Confirm full batch cash":"Confirm cash received")}</button></div>}
        {payment.status==="VERIFIED"&&<div className="payment-verified-note"><Check size={14}/> Verified {payment.verifiedUtc?formatIstDateTime(payment.verifiedUtc):"by shop"}</div>}
      </article>)}</div>}
    </section>
    <section className="panel refund-audit-panel">
      <div className="collection-header"><div><h2>Refund audit log</h2><p>Every shopkeeper-recorded file refund, with its exact amount, return method, reason and operator.</p></div><span className="setup-count">{refunds.length} recorded</span></div>
      {refunds.length===0?<EmptyModule icon={<RotateCcw size={24}/>} title="No refunds recorded" text="When a shopkeeper rejects a paid file and records its return, the audit entry will appear here."/>:
      <div className="payment-record-list">{refunds.map(refund=><article className="payment-record refund-record" key={refund.id}>
        <div className="payment-record-head"><div><strong>{refund.jobNumber}</strong><small>{refund.documentName}</small></div><div className="payment-record-amount">{formatMoney(refund.amountMinor,refund.currency)}</div></div>
        <div className="payment-record-meta"><span>{refund.method} refund</span><span>{formatIstDateTime(refund.refundedUtc)}</span><Status label="Recorded as sent" tone="green"/></div>
        <div className="refund-record-details"><span>Shopkeeper</span><strong>{refund.refundedByName}</strong><span>Reason</span><strong>{refund.reason}</strong>{refund.reference&&<><span>Refund reference</span><strong>{refund.reference}</strong></>}</div>
      </article>)}</div>}
    </section>
  </div>;
}
