import { useEffect, useRef, useState } from "react";
import { Activity, ArrowRight, Search, Users } from "lucide-react";
import {
  getShopCustomerHistory,
  getShopCustomers,
  type ShopCustomer,
  type ShopCustomerHistory,
  type ShopCustomerPage
} from "../../../api";
import { formatIstDateTime } from "../../../date-time";
import { formatMoney, prettyStatus } from "../../../shared/lib/formatting";
import Modal from "../../../shared/ui/Modal";
import PrintKindTag from "../../../shared/ui/PrintKindTag";
import Status from "../../../shared/ui/Status";

export default function CustomersPage({ token }: { token: string }) {
  const [searchDraft, setSearchDraft] = useState("");
  const [search, setSearch] = useState("");
  const [page, setPage] = useState(1);
  const [refreshKey, setRefreshKey] = useState(0);
  const [result, setResult] = useState<ShopCustomerPage | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [selectedCustomer, setSelectedCustomer] = useState<ShopCustomer | null>(null);
  const [history, setHistory] = useState<ShopCustomerHistory | null>(null);
  const [historyLoading, setHistoryLoading] = useState(false);
  const [historyError, setHistoryError] = useState("");
  const historyRequest = useRef(0);
  const pageSize = 20;

  useEffect(() => {
    let current = true;
    setLoading(true);
    setError("");
    void getShopCustomers(token, { search, page, pageSize })
      .then(value => { if (current) setResult(value); })
      .catch(exception => {
        if (current) setError(exception instanceof Error ? exception.message : "Could not load shop customers.");
      })
      .finally(() => { if (current) setLoading(false); });
    return () => { current = false; };
  }, [token, search, page, pageSize, refreshKey]);

  async function openHistory(customer: ShopCustomer) {
    const request = ++historyRequest.current;
    setSelectedCustomer(customer);
    setHistory(null);
    setHistoryError("");
    setHistoryLoading(true);
    try {
      const value = await getShopCustomerHistory(token, customer.id);
      if (request === historyRequest.current) setHistory(value);
    } catch (exception) {
      if (request === historyRequest.current) {
        setHistoryError(exception instanceof Error ? exception.message : "Could not load this customer's print history.");
      }
    } finally {
      if (request === historyRequest.current) setHistoryLoading(false);
    }
  }

  function closeHistory() {
    historyRequest.current++;
    setSelectedCustomer(null);
    setHistory(null);
    setHistoryError("");
    setHistoryLoading(false);
  }

  const totalPages = Math.max(1, Math.ceil((result?.totalCount ?? 0) / pageSize));
  const firstRow = result?.totalCount ? (page - 1) * pageSize + 1 : 0;
  const lastRow = Math.min(page * pageSize, result?.totalCount ?? 0);

  return (
    <section className="customers-workspace">
      <div className="customers-intro">
        <div>
          <span className="reports-eyebrow">SHOP RELATIONSHIPS</span>
          <h2>Customer directory</h2>
          <p>Signed-in SmartPrint customers who have connected to this shop. Guest sessions stay anonymous.</p>
        </div>
        <div className="customers-total">
          <strong>{result?.totalCount ?? "—"}</strong>
          <span>linked customers</span>
        </div>
      </div>

      <form className="customers-toolbar" onSubmit={event => {
        event.preventDefault();
        setPage(1);
        setSearch(searchDraft.trim());
      }}>
        <label className="search-field customers-search">
          <Search size={14}/>
          <input value={searchDraft} onChange={event => setSearchDraft(event.target.value)}
            placeholder="Search customer name or email" aria-label="Search customer name or email"/>
        </label>
        <button className="primary-button" type="submit"><Search size={14}/> Search</button>
        <button className="secondary-button" type="button" onClick={() => setRefreshKey(value => value + 1)} disabled={loading}>
          <Activity size={14}/> Refresh
        </button>
      </form>

      {error && <div className="inline-error" role="alert">{error}</div>}
      {loading && !result ? <div className="panel reports-loading"><span className="button-spinner"/> Loading customers…</div> : null}
      {!loading && !error && result?.items.length === 0 && (
        <div className="panel customers-empty">
          <span className="customers-empty-icon"><Users size={24}/></span>
          <h3>{search ? "No matching customers" : "No signed-in customers yet"}</h3>
          <p>{search
            ? "Try another name or email search."
            : "When a customer signs in before scanning this shop's QR code, their shop-specific activity will appear here. Guest jobs are intentionally not identified or linked."}</p>
        </div>
      )}

      {result && result.items.length > 0 && (
        <div className="panel customers-table-panel">
          <div className="panel-heading customers-panel-heading">
            <div><h2>Customers connected to this shop</h2><p>Only activity created through this shop's QR sessions is included.</p></div>
            <span className="neutral-chip">{result.totalCount} total</span>
          </div>
          <div className="table-scroll">
            <table className="customers-table">
              <thead><tr><th>CUSTOMER</th><th>PRINT JOBS</th><th>COMPLETED</th><th>LAST QR SESSION</th><th>HISTORY</th></tr></thead>
              <tbody>
                {result.items.map(customer => (
                  <tr key={customer.id}>
                    <td><div className="customer-cell"><strong>{customer.displayName || "SmartPrint customer"}</strong><span>{customer.maskedEmail}</span></div></td>
                    <td><strong className="customers-number">{customer.printJobCount}</strong><small className="customers-subvalue">{customer.sessionCount} QR sessions</small></td>
                    <td><strong className="customers-number">{customer.completedJobCount}</strong><small className="customers-subvalue">output confirmed</small></td>
                    <td><span className="customers-date">{formatIstDateTime(customer.lastSeenUtc)}</span></td>
                    <td><button type="button" className="customers-history-button" onClick={() => void openHistory(customer)}>View history <ArrowRight size={13}/></button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="table-footer customers-pagination">
            <span>Showing <strong>{firstRow}–{lastRow}</strong> of <strong>{result.totalCount}</strong></span>
            <div><button type="button" disabled={page <= 1 || loading} onClick={() => setPage(value => Math.max(1, value - 1))}>← Previous</button>
              <span>{page} / {totalPages}</span>
              <button type="button" disabled={page >= totalPages || loading} onClick={() => setPage(value => Math.min(totalPages, value + 1))}>Next →</button></div>
          </div>
        </div>
      )}

      {selectedCustomer && (
        <Modal title={selectedCustomer.displayName || "SmartPrint customer"}
          subtitle={selectedCustomer.maskedEmail + " · Only this shop's print history"}
          onClose={closeHistory}>
          <div className="customer-history-modal">
            {historyLoading && <div className="reports-loading"><span className="button-spinner"/> Loading print history…</div>}
            {historyError && <div className="inline-error" role="alert">{historyError}</div>}
            {history && (
              <>
                <div className="customer-history-summary"><strong>{history.totalJobs}</strong><span>print jobs at this shop</span></div>
                {history.jobs.length === 0
                  ? <div className="empty-search">No print jobs are available for this customer yet.</div>
                  : <div className="customer-history-list">
                    {history.jobs.map(job => {
                      const netPaid = job.payment
                        ? Math.max(0, job.payment.amountMinor - job.payment.refundedAmountMinor)
                        : 0;
                      const tone = job.status === "COMPLETED" ? "green"
                        : job.status.includes("REJECT") || job.status === "FAILED" ? "red"
                        : job.status.includes("AWAITING") ? "amber" : "blue";
                      return (
                        <article className="customer-history-row" key={job.id}>
                          <div className="customer-history-heading">
                            <div><strong>{job.document.originalFileName}</strong><PrintKindTag printKind={job.printKind}/><span>{job.jobNumber} · {formatIstDateTime(job.createdUtc)}</span></div>
                            <Status label={prettyStatus(job.status)} tone={tone}/>
                          </div>
                          <div className="customer-history-facts">
                            <span>{job.printKind==="PHOTO"?`Photo sheet · ${job.photoCount??0} × ${job.photoSizeCode??"photo"} · ${job.pageCount} A4 pages`:`${job.pageCount} pages · ${job.copies} copies`}</span>
                            <strong>{formatMoney(job.quotedAmountMinor, job.currency)}</strong>
                          </div>
                          {job.payment && <div className="customer-history-payment">
                            {job.payment.status.replaceAll("_", " ")} · {job.payment.method} · Net recorded: {formatMoney(netPaid, job.payment.currency)}
                            {job.payment.refundedAmountMinor > 0 ? " · Refund recorded" : ""}
                          </div>}
                          <div className="customer-history-output">{job.outputType === "PDF_EXPORT"
                            ? (job.outputCompletedUtc ? "PDF export created and verified" : "PDF export not yet completed")
                            : (job.outputConfirmedUtc ? "Shop confirmed physical output" : "Physical output not yet confirmed")}</div>
                        </article>
                      );
                    })}
                  </div>}
              </>
            )}
          </div>
        </Modal>
      )}
    </section>
  );
}
