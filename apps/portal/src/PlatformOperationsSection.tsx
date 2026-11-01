import { useEffect, useState } from "react";
import { AlertTriangle, Clock3, LoaderCircle, RefreshCw, Save, ShieldCheck } from "lucide-react";
import {
  getPlatformOperationalSettings,
  getPlatformOperationalSettingsHistory,
  updatePlatformOperationalSettings,
  type AuthTokens,
  type PlatformOperationalSettings,
  type PlatformOperationalSettingsAudit,
  type PlatformOperationalSettingsInput
} from "./api";

type NumericKey =
  | "trialMaxMonthlyPrintJobs"
  | "cloudCompletedDocumentRetentionDays"
  | "agentCompletedDocumentRetentionDays"
  | "abandonedDocumentRetentionDays"
  | "pendingLocalUploadRetentionDays"
  | "expiredUploadTicketRetentionDays"
  | "emptyCustomerSessionRetentionDays"
  | "staleRefreshTokenRetentionDays"
  | "apiRetentionSweepHours"
  | "agentRetentionSweepHours";

const fields: Array<{ key: NumericKey; label: string; hint: string; min: number; max: number }> = [
  { key: "trialMaxMonthlyPrintJobs", label: "Trial monthly print quota", hint: "Print jobs per calendar month. Default is 3,000. Paid plan quotas remain versioned under Plans & entitlements.", min: 1, max: 10_000_000 },
  { key: "cloudCompletedDocumentRetentionDays", label: "Cloud completed documents", hint: "Days after confirmed physical output or verified PDF export before source files are purged.", min: 1, max: 3650 },
  { key: "agentCompletedDocumentRetentionDays", label: "Windows Agent completed documents", hint: "Days local source documents and managed PDF exports remain after output confirmation.", min: 1, max: 3650 },
  { key: "abandonedDocumentRetentionDays", label: "Abandoned unpaid documents", hint: "Days terminal rejected or quote-expired files are retained when no payment or refund hold exists.", min: 1, max: 3650 },
  { key: "pendingLocalUploadRetentionDays", label: "Pending local uploads", hint: "Days after a local upload becomes terminal unpaid before the API authorises the Agent to delete it.", min: 1, max: 3650 },
  { key: "expiredUploadTicketRetentionDays", label: "Expired upload tickets", hint: "Extra days after an unfinished local upload expires before its ticket record is removed.", min: 1, max: 3650 },
  { key: "emptyCustomerSessionRetentionDays", label: "Empty customer sessions", hint: "Days expired sessions without jobs or upload tickets are kept.", min: 1, max: 3650 },
  { key: "staleRefreshTokenRetentionDays", label: "Expired/revoked refresh tokens", hint: "Additional days before expired or revoked authentication-token records are removed.", min: 1, max: 3650 },
  { key: "apiRetentionSweepHours", label: "API retention sweep interval", hint: "How often the API cleans cloud documents and database records.", min: 1, max: 168 },
  { key: "agentRetentionSweepHours", label: "Windows Agent retention sweep interval", hint: "How often paired Windows Agents clean confirmed local documents.", min: 1, max: 24 }
];

type Draft = Omit<PlatformOperationalSettings, "updatedByUserId" | "updatedUtc">;
function toDraft(value: PlatformOperationalSettings): Draft {
  const { updatedByUserId: _actor, updatedUtc: _updated, ...draft } = value;
  return draft;
}

export default function PlatformOperationsSection({ tokens }: { tokens: AuthTokens }) {
  const [draft, setDraft] = useState<Draft | null>(null);
  const [history, setHistory] = useState<PlatformOperationalSettingsAudit[]>([]);
  const [reason, setReason] = useState("");
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [success, setSuccess] = useState("");

  async function reload() {
    setLoading(true);
    setError("");
    try {
      const [current, events] = await Promise.all([
        getPlatformOperationalSettings(tokens.accessToken),
        getPlatformOperationalSettingsHistory(tokens.accessToken)
      ]);
      setDraft(toDraft(current));
      setHistory(events);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not load platform operational settings.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { void reload(); }, [tokens.accessToken]);

  function updateNumber(key: NumericKey, value: string) {
    const parsed = value === "" ? 0 : Number(value);
    if (!Number.isSafeInteger(parsed)) return;
    setDraft(current => current ? { ...current, [key]: parsed } : current);
  }

  async function save() {
    if (!draft) return;
    if (reason.trim().length < 5) {
      setError("Enter an audit reason of at least five characters.");
      return;
    }
    setSaving(true);
    setError("");
    setSuccess("");
    try {
      const payload: PlatformOperationalSettingsInput = { ...draft, reason: reason.trim() };
      const saved = await updatePlatformOperationalSettings(tokens.accessToken, payload);
      setDraft(toDraft(saved));
      setReason("");
      setSuccess("Platform settings saved. The API and connected Windows Agents will apply the updated values.");
      setHistory(await getPlatformOperationalSettingsHistory(tokens.accessToken));
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not save platform settings.");
    } finally {
      setSaving(false);
    }
  }

  return (
    <section className="platform-ops-page">
      <div className="platform-ops-intro">
        <div>
          <div className="platform-plan-kicker"><ShieldCheck size={14}/> PLATFORM POLICY</div>
          <h2>Operations, quotas and data retention</h2>
          <p>Central controls enforced by the API and paired Windows Agents. Every change is persisted with an audit reason.</p>
        </div>
        <button className="secondary-button" onClick={() => void reload()} disabled={loading || saving}>
          <RefreshCw size={14}/> Refresh
        </button>
      </div>

      {error && <div className="inline-error" role="alert">{error}</div>}
      {success && <div className="platform-plan-success" role="status"><ShieldCheck size={15}/>{success}</div>}
      {loading && !draft ? (
        <div className="panel platform-loading"><LoaderCircle size={17} className="platform-spin"/> Loading platform settings…</div>
      ) : draft ? (
        <>
          <section className="panel platform-ops-panel">
            <div className="platform-ops-heading">
              <div><h3>Monthly print quota and retention</h3><p>Quota units are print jobs, not pages or copies. Paid plan limits remain versioned per plan.</p></div>
              <span className="neutral-chip">Platform-managed</span>
            </div>
            <div className="platform-ops-grid">
              {fields.map(field => (
                <label className="platform-ops-field" key={field.key}>
                  <span><strong>{field.label}</strong><small>{field.hint}</small></span>
                  <input type="number" min={field.min} max={field.max} step={1} value={draft[field.key]}
                    onChange={event => updateNumber(field.key, event.target.value)} aria-label={field.label}/>
                </label>
              ))}
            </div>
            <label className="platform-ops-toggle">
              <input type="checkbox" checked={draft.allowPaidJobClaimsWhenSuspended}
                onChange={event => setDraft(current => current ? { ...current, allowPaidJobClaimsWhenSuspended: event.target.checked } : current)}/>
              <span><strong>Allow already-paid jobs to continue during suspension</strong><small>Only jobs created and payment-verified before the latest suspension can be claimed. New orders and post-suspension payments remain blocked.</small></span>
            </label>
            <div className="platform-ops-save-row">
              <label className="platform-ops-reason"><span>Audit reason</span><input value={reason} onChange={event => setReason(event.target.value)} maxLength={500} placeholder="Why are these platform settings changing?"/></label>
              <button className="primary-button" onClick={() => void save()} disabled={saving || loading}>
                {saving ? <LoaderCircle size={14} className="platform-spin"/> : <Save size={14}/>} Save settings
              </button>
            </div>
          </section>

          <section className="panel platform-ops-policy-note">
            <AlertTriangle size={17}/>
            <div><strong>Safety behaviour</strong><p>Output-unconfirmed documents are never automatically deleted. Paid files and files with outstanding refund obligations are retained. Permanent tenant deactivation is blocked until unsettled print and financial work is resolved.</p></div>
          </section>

          <section className="panel platform-ops-history">
            <div className="platform-ops-heading"><div><h3>Settings audit history</h3><p>Most recent platform-wide changes</p></div><span>{history.length} events</span></div>
            {history.length === 0 ? <div className="platform-plan-empty">No operational settings changes have been recorded yet.</div> : (
              <div className="platform-ops-history-list">
                {history.map(item => (
                  <details className="platform-ops-history-item" key={item.id}>
                    <summary><span><strong>{item.reason}</strong><small>{new Date(item.createdUtc).toLocaleString("en-IN")} · {item.actorUserId ?? "Platform administrator"}</small></span><Clock3 size={15}/></summary>
                    <pre>{item.changesJson}</pre>
                  </details>
                ))}
              </div>
            )}
          </section>
        </>
      ) : null}
    </section>
  );
}
