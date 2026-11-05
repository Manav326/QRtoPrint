import { Check, CreditCard, ShieldCheck, X } from "lucide-react";
import { formatIstDate, formatIstTime } from "../../date-time";
import type { ShopSubscriptionSummary } from "../../api";

export default function SubscriptionPage({ summary }: { summary: ShopSubscriptionSummary }) {
  const { subscription, plan, entitlements, usage } = summary;
  const status = subscription.status.toLowerCase();
  const statusTone = status === "active" ? "tone-success" : status === "trial" ? "tone-warning" : "tone-neutral";
  const planName = plan?.name ?? (subscription.planCode === "TRIAL" ? "Trial plan" : subscription.planCode);
  const periodStart = formatIstDate(subscription.periodStartUtc);
  const periodEnd = formatIstDate(subscription.periodEndUtc);
  const price = (amount: number | null | undefined, currency: string) =>
    amount == null ? "Not configured" : `${currency} ${(amount / 100).toLocaleString("en-IN", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;

  return (
    <section className="subscription-workspace">
      <div className="subscription-hero-grid">
        <section className="panel subscription-current-panel">
          <div className="subscription-panel-top">
            <span className="subscription-eyebrow">CURRENT SUBSCRIPTION</span>
            <span className={`subscription-status ${statusTone}`}>{subscription.status}</span>
          </div>
          <h2>{planName}</h2>
          <p className="subscription-plan-meta">
            {plan ? `Plan ${plan.code} · version ${plan.version} · ${plan.status}` : "A versioned plan has not yet been assigned to this shop."}
          </p>
          <div className="subscription-period">
            <div><span>Billing interval</span><strong>{subscription.billingInterval}</strong></div>
            <div><span>Current period</span><strong>{periodStart} – {periodEnd}</strong></div>
          </div>
          <div className="subscription-prices">
            <div><span>Monthly plan price</span><strong>{price(plan?.monthlyPriceMinor, plan?.currency ?? "INR")}</strong></div>
            <div><span>Annual plan price</span><strong>{price(plan?.annualPriceMinor, plan?.currency ?? "INR")}</strong></div>
          </div>
          {!entitlements.isAccessActive && (
            <div className="subscription-legacy-note subscription-expired-note"><ShieldCheck size={15}/><span>This trial or subscription is no longer active. New print requests are paused. Contact SmartPrint by QuantaEdge support to restore access.</span></div>
          )}
          {entitlements.isLegacyUnconfigured && entitlements.isAccessActive && (
            <div className="subscription-legacy-note"><ShieldCheck size={15}/><span>This subscription predates plan assignment. Existing shop access is being preserved until its terms are explicitly assigned.</span></div>
          )}
        </section>

        <section className="panel subscription-feature-panel">
          <div className="panel-heading"><div><h2>Included capabilities</h2><p>Resolved from the version assigned to your subscription</p></div></div>
          <div className="subscription-feature-list">
            <FeatureLine label="Cloud-assisted printing" enabled={entitlements.cloudAssistedPrintingEnabled}/>
            <FeatureLine label="White-label experience" enabled={entitlements.whiteLabelEnabled}/>
            <FeatureLine label="Advanced reports" enabled={entitlements.advancedReportsEnabled}/>
          </div>
          <div className="subscription-upgrade-note">
            <CreditCard size={15}/>
            <span>Plan changes and subscription billing are managed by the SmartPrint by QuantaEdge platform team. Customer print payments remain separate and continue to be handled by your shop.</span>
          </div>
        </section>
      </div>

      <section className="panel subscription-usage-panel">
        <div className="subscription-usage-heading">
          <div><h2>Usage and limits</h2><p>Live counts for this shop. Monthly print jobs use the UTC calendar month.</p></div>
          <span className="subscription-updated">Updated {formatIstTime(summary.generatedUtc)}</span>
        </div>
        <div className="subscription-usage-grid">
          <UsageMeter label="Registered printers" used={usage.printerCount} maximum={entitlements.maxPrinters}/>
          <UsageMeter label="Paired computers" used={usage.agentDeviceCount} maximum={entitlements.maxAgentDevices}/>
          <UsageMeter label="QR stations" used={usage.qrStationCount} maximum={entitlements.maxQrStations}/>
          <UsageMeter label="Print jobs this month" used={usage.monthlyPrintJobCount} maximum={entitlements.maxMonthlyPrintJobs}/>
        </div>
        <div className="subscription-document-limit">
          <span>Maximum cloud document size</span>
          <strong>{entitlements.maxDocumentBytes == null ? "No configured cap" : `${(entitlements.maxDocumentBytes / (1024 * 1024)).toLocaleString("en-IN", { maximumFractionDigits: 2 })} MB`}</strong>
        </div>
        <div className="subscription-usage-footnote">Disabled printers, QR stations and offline agents still count as registered resources unless removed or revoked. Revoked agent computers are excluded.</div>
      </section>
    </section>
  );
}

function UsageMeter({ label, used, maximum }: { label: string; used: number; maximum: number | null }) {
  const boundedMaximum = maximum == null ? null : Math.max(0, maximum);
  const percentage = boundedMaximum == null ? 0 : boundedMaximum > 0 ? Math.min(100, used / boundedMaximum * 100) : 100;
  return (
    <div className="subscription-usage-item">
      <div className="subscription-usage-item-top"><span>{label}</span><strong>{used.toLocaleString("en-IN")} <small>{boundedMaximum == null ? "/ unlimited" : `/ ${boundedMaximum.toLocaleString("en-IN")}`}</small></strong></div>
      <div className="subscription-progress-track"><span style={{ width: boundedMaximum == null ? "0%" : `${percentage}%` }}/></div>
      <small className="subscription-usage-caption">{boundedMaximum == null ? "No configured plan limit" : boundedMaximum === 0 ? "Not available on this plan" : `${Math.max(0, boundedMaximum - used).toLocaleString("en-IN")} remaining`}</small>
    </div>
  );
}

function FeatureLine({ label, enabled }: { label: string; enabled: boolean }) {
  return (
    <div className="subscription-feature-line">
      <span className={enabled ? "subscription-feature-indicator enabled" : "subscription-feature-indicator"}>{enabled ? <Check size={13}/> : <X size={13}/>}</span>
      <span>{label}</span>
      <strong>{enabled ? "Included" : "Not included"}</strong>
    </div>
  );
}
