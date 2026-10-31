import { useEffect, useMemo, useState } from "react";
import { CalendarClock, CheckCircle2, ChevronLeft, ChevronRight, CreditCard, History, RefreshCw, Search, ShieldCheck, X } from "lucide-react";
import {
  assignPlatformSubscription,
  getPlatformPlans,
  getPlatformSubscriptionHistory,
  getPlatformSubscriptions,
  type AuthTokens,
  type PlatformPlan,
  type PlatformSubscriptionAudit,
  type PlatformSubscriptionItem,
  type PlatformSubscriptionsPage
} from "./api";

type AssignmentOption = {
  value: string;
  label: string;
};

function buildAssignmentOptions(plans: PlatformPlan[]): AssignmentOption[] {
  const options: AssignmentOption[] = [];
  for (const plan of plans) {
    if (!plan.isActive) continue;
    for (const version of plan.versions) {
      if (version.status !== "Published") continue;
      if (plan.isSystemPlan && plan.code === "TRIAL" && version.publishedUtc) {
        options.push({
          value: `${version.id}|Trial`,
          label: `${plan.name} · version ${version.version} · 14-day trial`,
        });
        continue;
      }
      if (plan.isSystemPlan) continue;
      if (version.monthlyPriceMinor === 0) {
        options.push({
          value: `${version.id}|Monthly`,
          label: `${plan.name} · version ${version.version} · free monthly`,
        });
      }
      if (version.annualPriceMinor === 0) {
        options.push({
          value: `${version.id}|Annual`,
          label: `${plan.name} · version ${version.version} · free annual`,
        });
      }
    }
  }
  return options;
}

function date(value: string | null | undefined) {
  return value
    ? new Date(value).toLocaleDateString("en-IN", { day: "numeric", month: "short", year: "numeric" })
    : "—";
}

export default function PlatformSubscriptionsSection({ tokens }: { tokens: AuthTokens }) {
  const [page, setPage] = useState<PlatformSubscriptionsPage | null>(null);
  const [plans, setPlans] = useState<PlatformPlan[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [success, setSuccess] = useState("");
  const [search, setSearch] = useState("");
  const [pageNumber, setPageNumber] = useState(1);
  const [refreshKey, setRefreshKey] = useState(0);
  const [assignTenant, setAssignTenant] = useState<PlatformSubscriptionItem | null>(null);
  const [selectedOption, setSelectedOption] = useState("");
  const [reason, setReason] = useState("");
  const [requestKey, setRequestKey] = useState("");
  const [historyTenant, setHistoryTenant] = useState<string | null>(null);
  const [history, setHistory] = useState<PlatformSubscriptionAudit[]>([]);
  const [historyLoading, setHistoryLoading] = useState(false);

  const options = useMemo(() => buildAssignmentOptions(plans), [plans]);

  useEffect(() => {
    let current = true;
    setLoading(true);
    setError("");
    void Promise.all([
      getPlatformSubscriptions(tokens.accessToken, { search, page: pageNumber, pageSize: 20 }),
      getPlatformPlans(tokens.accessToken)
    ]).then(([subscriptions, catalogue]) => {
      if (!current) return;
      setPage(subscriptions);
      setPlans(catalogue);
    }).catch(exception => {
      if (current) setError(exception instanceof Error ? exception.message : "Could not load subscription administration.");
    }).finally(() => {
      if (current) setLoading(false);
    });
    return () => { current = false; };
  }, [tokens.accessToken, search, pageNumber, refreshKey]);

  function openAssignment(tenant: PlatformSubscriptionItem) {
    setAssignTenant(tenant);
    setSelectedOption(options[0]?.value ?? "");
    setReason("");
    setRequestKey(crypto.randomUUID());
    setError("");
    setSuccess("");
  }

  async function submitAssignment() {
    if (!assignTenant) return;
    const [planVersionId, billingInterval] = selectedOption.split("|");
    if (!planVersionId || !billingInterval || reason.trim().length < 10) {
      setError("Choose an assignable plan and provide a reason of at least 10 characters.");
      return;
    }
    setBusy(true);
    setError("");
    setSuccess("");
    try {
      const result = await assignPlatformSubscription(tokens.accessToken, assignTenant.id, {
        planVersionId,
        billingInterval,
        reason: reason.trim(),
        idempotencyKey: requestKey
      });
      setSuccess(`${result.planCode} assigned to ${assignTenant.businessName}. ${result.status} until ${date(result.periodEndUtc)}.`);
      setAssignTenant(null);
      setHistoryTenant(null);
      setRefreshKey(value => value + 1);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "The subscription could not be assigned.");
    } finally {
      setBusy(false);
    }
  }

  async function toggleHistory(tenant: PlatformSubscriptionItem) {
    if (historyTenant === tenant.id) {
      setHistoryTenant(null);
      setHistory([]);
      return;
    }
    setHistoryTenant(tenant.id);
    setHistory([]);
    setHistoryLoading(true);
    setError("");
    try {
      setHistory(await getPlatformSubscriptionHistory(tokens.accessToken, tenant.id));
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not load subscription history.");
    } finally {
      setHistoryLoading(false);
    }
  }

  const canAssign = options.length > 0;

  return (
    <section className="platform-plans platform-subscriptions">
      <div className="platform-plans-intro">
        <div>
          <div className="platform-plan-kicker"><CreditCard size={14}/> SUBSCRIPTION OPERATIONS</div>
          <h2>Shop subscriptions</h2>
          <p>Inspect each shop’s assigned plan and billing period. Trial and explicitly free plans can be assigned with an immutable audit record. Paid plans require verified payment before activation.</p>
        </div>
        <div className="platform-plans-actions">
          <button className="secondary-button" onClick={() => setRefreshKey(value => value + 1)} disabled={loading}><RefreshCw size={14}/> Refresh</button>
        </div>
      </div>

      {error && <div className="inline-error" role="alert">{error}</div>}
      {success && <div className="inline-success" role="status"><CheckCircle2 size={15}/><span>{success}</span><button className="icon-button" onClick={() => setSuccess("")} aria-label="Dismiss"><X size={14}/></button></div>}

      <section className="panel platform-tenants-panel">
        <div className="platform-tenants-heading">
          <div><h2>Subscription directory</h2><p>Current subscription per tenant; older assignments remain available in history.</p></div>
          <span className="neutral-chip">{page?.totalCount ?? 0} shops</span>
        </div>
        <div className="platform-tenant-filters">
          <label className="platform-search"><Search size={15}/><input value={search} onChange={event => { setSearch(event.target.value); setPageNumber(1); }} placeholder="Search shop name or tenant slug"/></label>
          <span className="platform-status-filter"><ShieldCheck size={14}/> Platform-only controls</span>
        </div>

        {loading && !page ? <div className="platform-loading-inline"><span className="button-spinner"/> Loading subscriptions…</div> : null}
        {!loading && page && page.items.length === 0 ? <div className="platform-empty">No shops match this search.</div> : null}
        {page && page.items.length > 0 && (
          <div className="platform-table-scroll">
            <table className="platform-tenant-table platform-subscription-table">
              <thead><tr><th>SHOP</th><th>ASSIGNED PLAN</th><th>STATUS</th><th>PERIOD END</th><th>ACTIONS</th></tr></thead>
              <tbody>
                {page.items.map(tenant => (
                  <tr key={tenant.id}>
                    <td><div className="platform-shop-cell"><div className="platform-shop-icon"><CreditCard size={15}/></div><div><strong>{tenant.businessName}</strong><small>{tenant.slug}</small></div></div></td>
                    <td>
                      <div className="platform-subs-copy">
                        <strong>{tenant.plan?.name ?? tenant.subscription?.planCode ?? "No plan assigned"}</strong>
                        <small>{tenant.subscription ? `${tenant.subscription.billingInterval} · ${tenant.plan ? `v${tenant.plan.version}` : "legacy"}` : "No subscription record"}</small>
                      </div>
                    </td>
                    <td><span className={`platform-subscription-state ${(tenant.subscription?.status ?? "unassigned").toLowerCase()}`}>{tenant.subscription?.status ?? "Unassigned"}</span></td>
                    <td><div className="platform-subs-copy"><strong>{date(tenant.subscription?.periodEndUtc)}</strong><small>{tenant.subscription ? `Started ${date(tenant.subscription.periodStartUtc)}` : "—"}</small></div></td>
                    <td><div className="platform-subscription-actions">
                      <button className="secondary-button small" type="button" onClick={() => void toggleHistory(tenant)}><History size={13}/> History</button>
                      <button className="primary-button small" type="button" disabled={!canAssign} onClick={() => openAssignment(tenant)}>Assign trial/free plan</button>
                    </div></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {page && <div className="platform-pagination">
          <span>Page {page.page} · {page.totalCount.toLocaleString("en-IN")} shops</span>
          <div>
            <button className="icon-button" onClick={() => setPageNumber(value => Math.max(1, value - 1))} disabled={page.page <= 1 || loading} aria-label="Previous page"><ChevronLeft size={17}/></button>
            <button className="icon-button" onClick={() => setPageNumber(value => value + 1)} disabled={page.page * page.pageSize >= page.totalCount || loading} aria-label="Next page"><ChevronRight size={17}/></button>
          </div>
        </div>}
      </section>

      {historyTenant && (
        <section className="panel platform-tenants-panel platform-subscription-history">
          <div className="platform-tenants-heading"><div><h2>Assignment history</h2><p>Append-only platform audit events for {page?.items.find(item => item.id === historyTenant)?.businessName ?? "this shop"}.</p></div><button className="icon-button" onClick={() => setHistoryTenant(null)} aria-label="Close history"><X size={15}/></button></div>
          {historyLoading ? <div className="platform-loading-inline"><span className="button-spinner"/> Loading history…</div> :
            history.length === 0 ? <div className="platform-empty">No platform assignment events have been recorded for this shop yet.</div> :
            <div className="platform-table-scroll"><table className="platform-tenant-table platform-history-table">
              <thead><tr><th>EVENT</th><th>PLAN</th><th>STATUS</th><th>PREVIOUS STATUS</th><th>PERIOD</th><th>REASON</th><th>RECORDED</th></tr></thead>
              <tbody>{history.map(event => <tr key={event.id}>
                <td><strong>{event.eventType.replace(/_/g, " ")}</strong></td>
                <td>{event.planCode} · {event.billingInterval}</td>
                <td>{event.status}</td>
                <td>{event.previousStatus ?? "First assignment"}</td>
                <td>{date(event.periodStartUtc)} – {date(event.periodEndUtc)}</td>
                <td className="platform-history-reason">{event.reason}</td>
                <td>{date(event.createdUtc)}</td>
              </tr>)}</tbody>
            </table></div>
          }
        </section>
      )}

      {assignTenant && (
        <div className="platform-assignment-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget && !busy) setAssignTenant(null); }}>
          <section className="panel platform-assignment-modal" role="dialog" aria-modal="true" aria-labelledby="subscription-assignment-title">
            <div className="platform-assignment-heading">
              <div><div className="platform-plan-kicker"><CalendarClock size={14}/> MANUAL ASSIGNMENT</div><h2 id="subscription-assignment-title">Assign trial or free plan</h2><p>{assignTenant.businessName} · {assignTenant.slug}</p></div>
              <button className="icon-button" onClick={() => setAssignTenant(null)} disabled={busy} aria-label="Close assignment"><X size={16}/></button>
            </div>
            <label className="platform-assignment-field">Published trial or free plan
              <select value={selectedOption} onChange={event => setSelectedOption(event.target.value)} disabled={busy}>
                {options.map(option => <option key={option.value} value={option.value}>{option.label}</option>)}
              </select>
            </label>
            <label className="platform-assignment-field">Reason for assignment
              <textarea rows={3} maxLength={500} value={reason} onChange={event => setReason(event.target.value)} placeholder="Record the approval, trial extension or complimentary access reason." disabled={busy}/>
              <small>{reason.trim().length}/500 characters · minimum 10</small>
            </label>
            <div className="platform-assignment-guard"><ShieldCheck size={15}/><span>Paid versions cannot be manually assigned. This action does not initiate a charge, create an invoice or handle customer print payments.</span></div>
            <div className="platform-assignment-actions">
              <button className="secondary-button" onClick={() => setAssignTenant(null)} disabled={busy}>Cancel</button>
              <button className="primary-button" onClick={() => void submitAssignment()} disabled={busy || !selectedOption || reason.trim().length < 10}>{busy ? "Assigning…" : "Assign subscription"}</button>
            </div>
          </section>
        </div>
      )}
    </section>
  );
}
