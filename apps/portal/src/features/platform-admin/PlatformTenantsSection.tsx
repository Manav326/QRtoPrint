import type { Dispatch, SetStateAction } from "react";
import { Building2, ChevronLeft, ChevronRight, PauseCircle, PlayCircle, Search } from "lucide-react";
import type { PlatformTenant, PlatformTenantsPage } from "../../api";
import { formatIstDate, formatIstDateTime } from "../../date-time";
import { prettyStatus, toneForStatus } from "./platform-admin-formatting";
import type { PlatformTenantStatusHistoryItem } from "./usePlatformAdminData";

type PlatformTenantsSectionProps = {
  tenants:PlatformTenantsPage|null;
  tenantLoading:boolean;
  tenantError:string;
  tenantActionError:string;
  tenantActionId:string;
  statusHistoryTenant:PlatformTenant|null;
  statusHistory:PlatformTenantStatusHistoryItem[]|null;
  search:string;
  setSearch:Dispatch<SetStateAction<string>>;
  status:string;
  setStatus:Dispatch<SetStateAction<string>>;
  page:number;
  setPage:Dispatch<SetStateAction<number>>;
  changeTenantStatus:(tenant:PlatformTenant,nextStatus:"Active"|"Suspended"|"Inactive")=>Promise<void>;
  showTenantStatusHistory:(tenant:PlatformTenant)=>Promise<void>;
  closeTenantStatusHistory:()=>void;
};

export default function PlatformTenantsSection({
  tenants,tenantLoading,tenantError,tenantActionError,tenantActionId,
  statusHistoryTenant,statusHistory,search,setSearch,status,setStatus,page,setPage,
  changeTenantStatus,showTenantStatusHistory,closeTenantStatusHistory
}:PlatformTenantsSectionProps) {
  return (
    <section className="panel platform-tenants-panel">
                  <div className="platform-tenants-heading">
                    <div><h2>Shop directory</h2><p>Tenant metadata and operational footprint. Customer-level information is intentionally excluded.</p></div>
                    <span className="setup-count">{tenants ? `${tenants.totalCount.toLocaleString("en-IN")} shops` : "Loading"}</span>
                  </div>
                  <div className="platform-tenant-filters">
                    <label className="platform-search"><Search size={15}/><input value={search} onChange={event => { setSearch(event.target.value); setPage(1); }} placeholder="Search shop name or slug" aria-label="Search shop name or slug"/></label>
                    <label className="platform-status-filter">
                      <span>Status</span>
                      <select value={status} onChange={event => { setStatus(event.target.value); setPage(1); }} aria-label="Filter by tenant status">
                        <option value="">All statuses</option>
                        <option value="Active">Active</option>
                        <option value="Suspended">Suspended</option>
                        <option value="Inactive">Inactive</option>
                      </select>
                    </label>
                  </div>
                  {tenantError && <div className="inline-error" role="alert">{tenantError}</div>}
                  {tenantActionError && <div className="inline-error" role="alert">{tenantActionError}</div>}
                  {tenantLoading && !tenants ? <div className="platform-loading-inline"><span className="button-spinner"/> Loading shops…</div> : null}
                  {statusHistoryTenant && <section className="panel platform-status-history">
                    <div className="panel-heading"><div><h2>Status history · {statusHistoryTenant.businessName}</h2><p>Audited platform tenant lifecycle changes</p></div><button className="text-action" onClick={closeTenantStatusHistory}>Close</button></div>
                    {statusHistory === null ? <div className="platform-loading-inline">Loading status history…</div> :
                      statusHistory.length === 0 ? <div className="platform-loading-inline">No status changes have been recorded.</div> :
                      <div className="platform-status-history-list">{statusHistory.map(item => <div className="platform-status-history-row" key={item.id}>
                        <strong>{item.previousStatus} → {item.newStatus}</strong><span>{item.reason}</span><small>{formatIstDateTime(item.createdUtc)} · {item.actorUserId || "Platform administrator"}</small>
                      </div>)}</div>}
                  </section>}
                  {tenants && (
                    <>
                      <div className="platform-table-scroll">
                        <table className="platform-tenant-table">
                          <thead><tr><th>SHOP</th><th>TENANT STATUS</th><th>SUBSCRIPTION</th><th>DEVICES</th><th>JOBS · 30 DAYS</th><th>CREATED</th><th>ACTIONS</th></tr></thead>
                          <tbody>
                            {tenants.items.map(tenant => (
                              <tr key={tenant.id}>
                                <td><div className="platform-shop-cell"><div className="platform-shop-icon"><Building2 size={15}/></div><div><strong>{tenant.businessName}</strong><small>{tenant.slug}</small></div></div></td>
                                <td><span className={`platform-tenant-badge ${toneForStatus(tenant.status)}`}>{prettyStatus(tenant.status)}</span></td>
                                <td><div className="platform-subscription-cell"><strong>{tenant.planCode || "Unassigned"}</strong><small>{tenant.subscriptionStatus ? prettyStatus(tenant.subscriptionStatus) : "No subscription"}</small>{tenant.subscriptionPeriodEndUtc && <small>Until {formatIstDate(tenant.subscriptionPeriodEndUtc)}</small>}</div></td>
                                <td><div className="platform-device-cell"><strong>{tenant.activePrinterCount} / {tenant.printerCount} printers</strong><small>{tenant.agentCount} paired · {tenant.onlineAgentCount} online · {tenant.offlineAgentCount} offline</small><small>{tenant.latestAgentHeartbeatUtc ? "Last contact " + formatIstDateTime(tenant.latestAgentHeartbeatUtc) : "No Agent heartbeat received"}</small></div></td>
                                <td><strong className="platform-numeric">{tenant.jobsLast30Days.toLocaleString("en-IN")}</strong></td>
                                <td><span className="platform-date">{formatIstDate(tenant.createdUtc)}</span></td>
                                <td><div className="platform-tenant-actions">
                                  {tenant.status === "Active" && <>
                                    <button className="secondary-button" onClick={() => void changeTenantStatus(tenant, "Suspended")} disabled={tenantActionId === tenant.id || tenantLoading} title="Block new print activity while preserving recovery"><PauseCircle size={13}/> Suspend</button>
                                  </>}
                                  {tenant.status === "Suspended" && <>
                                    <button className="secondary-button" onClick={() => void changeTenantStatus(tenant, "Active")} disabled={tenantActionId === tenant.id || tenantLoading}><PlayCircle size={13}/> Reactivate</button>
                                    <button className="secondary-button" onClick={() => void changeTenantStatus(tenant, "Inactive")} disabled={tenantActionId === tenant.id || tenantLoading}>Deactivate</button>
                                  </>}
                                  {tenant.status === "Inactive" && <span className="platform-date">Permanently inactive</span>}
                                  <button className="text-action" onClick={() => void showTenantStatusHistory(tenant)}>History</button>
                                </div></td>
                              </tr>
                            ))}
                            {tenants.items.length === 0 && <tr><td className="platform-no-results" colSpan={7}>No shops match this filter.</td></tr>}
                          </tbody>
                        </table>
                      </div>
                      <div className="platform-pagination">
                        <span>{tenants.totalCount === 0 ? "No shops" : `Showing ${((tenants.page - 1) * tenants.pageSize) + 1}–${Math.min(tenants.page * tenants.pageSize, tenants.totalCount)} of ${tenants.totalCount.toLocaleString("en-IN")}`}</span>
                        <div>
                          <button className="icon-button" onClick={() => setPage(value => Math.max(1, value - 1))} disabled={tenants.page <= 1 || tenantLoading} aria-label="Previous page"><ChevronLeft size={17}/></button>
                          <span>Page {tenants.page}</span>
                          <button className="icon-button" onClick={() => setPage(value => value + 1)} disabled={tenants.page * tenants.pageSize >= tenants.totalCount || tenantLoading} aria-label="Next page"><ChevronRight size={17}/></button>
                        </div>
                      </div>
                    </>
                  )}
                </section>
  );
}
