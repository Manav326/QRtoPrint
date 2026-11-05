import { useEffect, useState } from "react";
import { Activity, Check, FileText, Printer, Wallet } from "lucide-react";
import { getShopReportSummary, type ShopReportSummary } from "../../../api";
import { formatIstDateTime } from "../../../date-time";
import { formatMoney, prettyStatus } from "../../../shared/lib/formatting";
import MetricCard from "../../../shared/ui/MetricCard";

export default function ReportsPage({ token }: { token: string }) {
  const [days, setDays] = useState<7 | 30 | 90>(30);
  const [summary, setSummary] = useState<ShopReportSummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [refreshKey, setRefreshKey] = useState(0);

  useEffect(() => {
    let current = true;
    setLoading(true);
    setError("");
    setSummary(null);
    void getShopReportSummary(token, days)
      .then(result => { if (current) setSummary(result); })
      .catch(exception => {
        if (current) {
          setError(exception instanceof Error ? exception.message : "Could not load shop reports.");
        }
      })
      .finally(() => { if (current) setLoading(false); });
    return () => { current = false; };
  }, [token, days, refreshKey]);

  const statusMaximum = Math.max(1, ...(summary?.jobsByStatus.map(item => item.count) ?? [0]));

  return (
    <section className="reports-workspace">
      <div className="reports-toolbar">
        <div><span className="reports-eyebrow">SHOP PERFORMANCE</span><h2>Operations report</h2><p>Live figures from this shop\u2019s recorded print jobs and verified collections.</p></div>
        <div className="reports-controls">
          <label className="reports-period">Period
            <select aria-label="Reporting period" value={days} onChange={event => setDays(Number(event.target.value) as 7 | 30 | 90)}>
              <option value={7}>Last 7 days</option><option value={30}>Last 30 days</option><option value={90}>Last 90 days</option>
            </select>
          </label>
          <button className="secondary-button reports-refresh" type="button" onClick={() => setRefreshKey(value => value + 1)} disabled={loading}><Activity size={14}/> Refresh</button>
        </div>
      </div>
      {error && <div className="inline-error" role="alert">{error}</div>}
      {loading && !summary ? <div className="panel reports-loading"><span className="button-spinner"/> Loading shop activity…</div> : null}
      {!loading && !summary && !error ? <div className="panel reports-loading">No report data is available for this period yet.</div> : null}
      {summary && <>
        <div className="stat-grid reports-stat-grid">
          <MetricCard icon={<FileText size={18}/>} label="Print jobs" value={summary.totalJobs.toLocaleString("en-IN")} note={`Created in the last ${summary.periodDays} days`} accent="blue"/>
          <MetricCard icon={<Check size={18}/>} label="Output confirmed" value={summary.completedJobs.toLocaleString("en-IN")} note="Physically confirmed by shop operators" accent="mint"/>
          <MetricCard icon={<Printer size={18}/>} label="Pages requested" value={summary.pagesRequested.toLocaleString("en-IN")} note="Page count × requested copies" accent="violet"/>
          <MetricCard icon={<Wallet size={18}/>} label="Payments to review" value={summary.pendingPaymentCount.toLocaleString("en-IN")} note="Open UPI/cash attempts across all dates" accent="amber"/>
        </div>
        <div className="reports-grid">
          <section className="panel reports-panel">
            <div className="panel-heading"><div><h2>Job status breakdown</h2><p>Current status of jobs created during this period</p></div><span className="neutral-chip">{summary.totalJobs} jobs</span></div>
            {summary.jobsByStatus.length === 0 ? <div className="reports-empty">No print jobs were created in this period.</div> :
              <div className="report-status-list">{summary.jobsByStatus.map(item => <div className="report-status-row" key={item.status}>
                <div className="report-status-label"><span>{prettyStatus(item.status)}</span><strong>{item.count.toLocaleString("en-IN")}</strong></div>
                <div className="report-status-track"><span style={{ width: `${Math.max(2, item.count / statusMaximum * 100)}%` }}/></div>
              </div>)}</div>}
          </section>
          <section className="panel reports-panel">
            <div className="panel-heading"><div><h2>Verified collections</h2><p>Shop-confirmed payments, grouped separately by currency</p></div><span className="neutral-chip">Verified only</span></div>
            {summary.verifiedCollectionsByMethod.length === 0 ? <div className="reports-empty">No payments have been verified in this period. Unverified UPI references are not included.</div> :
              <div className="report-collection-list">{summary.verifiedCollectionsByMethod.map(item => <div className="report-collection-row" key={`${item.currency}-${item.method}`}>
                <div><strong>{item.method === "UPI" ? "UPI" : "Cash at counter"}</strong><small>{item.paymentCount} verified {item.paymentCount === 1 ? "payment" : "payments"}</small></div>
                <strong className="report-collection-amount">{formatMoney(item.amountMinor, item.currency)}</strong>
              </div>)}</div>}
          </section>
          <section className="panel reports-panel">
            <div className="panel-heading"><div><h2>Refunds recorded</h2><p>Refunds the shop confirmed sending during this period</p></div><span className="neutral-chip">Recorded as sent</span></div>
            {summary.refundsRecordedByMethod.length === 0 ? <div className="reports-empty">No refunds were recorded as sent during this period.</div> :
              <div className="report-collection-list">{summary.refundsRecordedByMethod.map(item => <div className="report-collection-row" key={`refund-${item.currency}-${item.method}`}>
                <div><strong>{item.method === "UPI" ? "UPI refund" : "Cash refund"}</strong><small>{item.refundCount} recorded {item.refundCount === 1 ? "refund" : "refunds"}</small></div>
                <strong className="report-collection-amount">{formatMoney(item.amountMinor, item.currency)}</strong>
              </div>)}</div>}
          </section>
        </div>
        {summary.outputUnconfirmedJobs > 0 && <div className="report-caution"><ShieldCheck size={16}/><span><strong>{summary.outputUnconfirmedJobs} output-unconfirmed {summary.outputUnconfirmedJobs === 1 ? "job" : "jobs"}.</strong> Check the actual pages before any manual retry. These jobs are not counted as completed.</span></div>}
        <div className="reports-footnote">Report window: {formatIstDateTime(summary.fromUtc)} – {formatIstDateTime(summary.toUtc)}. Job counts use creation time, gross collections use payment verification time, and refunds use the date the shop recorded sending them. Pages requested are not a claim of pages physically printed. A UPI reference is excluded until the shop verifies the receipt.</div>
      </>}
    </section>
  );
}
