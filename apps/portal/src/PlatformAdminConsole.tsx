import { useEffect, useState, type ReactNode } from "react";
import {
  Activity, Building2, CheckCircle2, ChevronLeft, ChevronRight,
  Clock3, Layers3, LogOut, Menu, Moon, Monitor, Printer, RefreshCw, Search, ShieldCheck,
  Sun
} from "lucide-react";
import PlatformPlansSection from "./PlatformPlansSection";
import {
  getPlatformOverview, getPlatformTenants, logout,
  type AuthTokens, type PlatformOverview, type PlatformTenantsPage
} from "./api";

type Section = "Overview" | "Tenants" | "Plans";

export default function PlatformAdminConsole({
  tokens,
  onSignOut
}: {
  tokens: AuthTokens;
  onSignOut: () => void;
}) {
  const [section, setSection] = useState<Section>("Overview");
  const [dark, setDark] = useState(true);
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [overview, setOverview] = useState<PlatformOverview | null>(null);
  const [overviewLoading, setOverviewLoading] = useState(true);
  const [overviewError, setOverviewError] = useState("");
  const [tenants, setTenants] = useState<PlatformTenantsPage | null>(null);
  const [tenantLoading, setTenantLoading] = useState(false);
  const [tenantError, setTenantError] = useState("");
  const [search, setSearch] = useState("");
  const [status, setStatus] = useState("");
  const [page, setPage] = useState(1);
  const [refreshKey, setRefreshKey] = useState(0);
  const [signingOut, setSigningOut] = useState(false);

  useEffect(() => {
    let active = true;
    setOverviewLoading(true);
    setOverviewError("");
    void getPlatformOverview(tokens.accessToken)
      .then(value => { if (active) setOverview(value); })
      .catch(exception => {
        if (active) setOverviewError(exception instanceof Error ? exception.message : "Could not load platform overview.");
      })
      .finally(() => { if (active) setOverviewLoading(false); });
    return () => { active = false; };
  }, [tokens.accessToken, refreshKey]);

  useEffect(() => {
    let active = true;
    setTenantLoading(true);
    setTenantError("");
    void getPlatformTenants(tokens.accessToken, { search, status, page, pageSize: 20 })
      .then(value => { if (active) setTenants(value); })
      .catch(exception => {
        if (active) setTenantError(exception instanceof Error ? exception.message : "Could not load the tenant directory.");
      })
      .finally(() => { if (active) setTenantLoading(false); });
    return () => { active = false; };
  }, [tokens.accessToken, search, status, page, refreshKey]);

  async function signOut() {
    setSigningOut(true);
    try { await logout(tokens.accessToken); } catch { /* Local sign-out always clears this browser session. */ }
    onSignOut();
  }

  function navigate(next: Section) {
    setSection(next);
    setSidebarOpen(false);
  }

  const title = section === "Overview" ? "Platform overview" : section === "Tenants" ? "Shop tenants" : "Plans and entitlements";
  const subtitle = section === "Overview"
    ? "A cross-platform view of tenant health, subscriptions, devices and recent activity."
    : section === "Tenants"
      ? "Find shops and inspect their current subscription and connected-device footprint."
      : "Configure versioned SaaS plans, prices and centrally managed capabilities.";

  return (
    <div className={`app-shell platform-admin-shell${dark ? "" : " light"}`}>
      {sidebarOpen && <button className="mobile-scrim" aria-label="Close navigation" onClick={() => setSidebarOpen(false)}/>}
      <aside className={`sidebar platform-sidebar${sidebarOpen ? " sidebar-open" : ""}`}>
        <div className="brand-lockup">
          <div className="brand-mark"><Printer size={21}/></div>
          <div><div className="brand-name">smart<span>print</span></div><div className="brand-caption">BY QRtoPrint</div></div>
        </div>
        <div className="workspace-switcher platform-workspace">
          <div className="platform-shield"><ShieldCheck size={18}/></div>
          <div className="workspace-copy"><strong>Platform control</strong><span>Owner workspace</span></div>
          <span className="platform-online-dot"/>
        </div>
        <div className="nav-label">ADMINISTRATION</div>
        <nav className="primary-nav" aria-label="Platform administration">
          <button className={section === "Overview" ? "nav-item active" : "nav-item"} onClick={() => navigate("Overview")}>
            <Activity size={17}/> Overview
          </button>
          <button className={section === "Tenants" ? "nav-item active" : "nav-item"} onClick={() => navigate("Tenants")}>
            <Building2 size={17}/> Shop tenants
            {overview && <span className="nav-count">{overview.tenantCount}</span>}
          </button>
          <button className={section === "Plans" ? "nav-item active" : "nav-item"} onClick={() => navigate("Plans")}>
            <Layers3 size={17}/> Plans & entitlements
          </button>
        </nav>
        <div className="sidebar-bottom">
          <div className="platform-security-note"><ShieldCheck size={15}/><span>Platform scope is separate from shop accounts and tenant data.</span></div>
          <div className="profile-row">
            <div className="profile-avatar platform-avatar">PA</div>
            <div className="profile-copy"><strong>Platform administrator</strong><span>Privileged access</span></div>
            <button className="icon-button" title="Sign out" aria-label="Sign out" onClick={() => void signOut()} disabled={signingOut}>
              <LogOut size={16}/>
            </button>
          </div>
        </div>
      </aside>

      <main className="main-panel">
        <header className="topbar">
          <div className="topbar-left"><button className="icon-button mobile-menu" onClick={() => setSidebarOpen(true)} aria-label="Open navigation"><Menu size={19}/></button><div className="breadcrumb"><span>Smart Print</span><span className="crumb-slash">/</span><strong>{title}</strong></div></div>
          <div className="topbar-actions">
            <span className="platform-session-pill"><i/> Platform session secured</span>
            <span className="topbar-divider"/>
            <button className="icon-button" onClick={() => setDark(value => !value)} title={dark ? "Switch to light theme" : "Switch to dark theme"} aria-label={dark ? "Switch to light theme" : "Switch to dark theme"}>
              {dark ? <Sun size={17}/> : <Moon size={17}/>}
            </button>
            <button className="icon-button" onClick={() => void signOut()} disabled={signingOut} title="Sign out" aria-label="Sign out">
              <LogOut size={17}/>
            </button>
          </div>
        </header>

        <div className="content-wrap">
          <div className="page-heading platform-page-heading">
            <div>
              <div className="eyebrow"><span className="eyebrow-line"/> PLATFORM CONTROL CENTRE</div>
              <h1>{title}</h1>
              <p>{subtitle}</p>
            </div>
            <div className="platform-heading-actions">
              <span className="platform-readonly-label"><ShieldCheck size={13}/> {section === "Plans" ? "Plan configuration" : "Read-only monitoring"}</span>
              <button className="secondary-button platform-refresh" onClick={() => setRefreshKey(value => value + 1)} disabled={overviewLoading || tenantLoading}>
                <RefreshCw size={14}/> Refresh data
              </button>
            </div>
          </div>

          {overviewError && <div className="inline-error" role="alert">{overviewError}</div>}
          {section === "Overview" && (
            <section className="platform-overview">
              {overviewLoading && !overview ? (
                <div className="panel platform-loading"><span className="button-spinner"/> Loading platform metrics…</div>
              ) : overview ? (
                <>
                  <div className="stat-grid platform-stat-grid">
                    <Metric icon={<Building2 size={18}/>} label="Shop tenants" value={overview.tenantCount} note={`${overview.activeTenantCount} active shops`} tone="violet"/>
                    <Metric icon={<Clock3 size={18}/>} label="Active trials" value={overview.trialSubscriptionCount} note="Subscriptions still within trial" tone="amber"/>
                    <Metric icon={<CheckCircle2 size={18}/>} label="Active subscriptions" value={overview.activeSubscriptionCount} note="Paid/active state recorded" tone="mint"/>
                    <Metric icon={<Printer size={18}/>} label="Printers registered" value={overview.printerCount} note={`${overview.activePrinterCount} enabled in shops`} tone="blue"/>
                    <Metric icon={<Monitor size={18}/>} label="Online agents" value={overview.onlineAgentCount} note={`${overview.agentCount} non-revoked agents`} tone="blue"/>
                    <Metric icon={<FileIcon/>} label="Print jobs · 30 days" value={overview.jobsLast30Days} note="Across all shops" tone="violet"/>
                  </div>

                  <div className="platform-overview-grid">
                    <section className="panel platform-summary-panel">
                      <div className="panel-heading">
                        <div><h2>Subscription status</h2><p>Recorded subscription states across all tenants</p></div>
                        <span className="neutral-chip">Platform-wide</span>
                      </div>
                      {overview.subscriptionsByStatus.length === 0 ? (
                        <div className="platform-empty">No subscription records are available yet.</div>
                      ) : (
                        <div className="platform-status-list">
                          {overview.subscriptionsByStatus.map(item => (
                            <div className="platform-status-row" key={item.status}>
                              <span className={`platform-status-dot ${toneForStatus(item.status)}`}/>
                              <strong>{prettyStatus(item.status)}</strong>
                              <span className="platform-status-count">{item.count.toLocaleString("en-IN")}</span>
                            </div>
                          ))}
                        </div>
                      )}
                    </section>

                    <section className="panel platform-summary-panel">
                      <div className="panel-heading">
                        <div><h2>Operational health</h2><p>Connected devices and recent print activity</p></div>
                        <span className="neutral-chip">Live snapshot</span>
                      </div>
                      <div className="platform-health-list">
                        <HealthRow icon={<Monitor size={16}/>} title="Print agents online" value={`${overview.onlineAgentCount} / ${overview.agentCount}`} detail="Heartbeat observed within two minutes"/>
                        <HealthRow icon={<Printer size={16}/>} title="Enabled printers" value={`${overview.activePrinterCount} / ${overview.printerCount}`} detail="Configured across shop tenants"/>
                        <HealthRow icon={<Activity size={16}/>} title="Jobs created" value={overview.jobsLast30Days.toLocaleString("en-IN")} detail="Last 30 days, all tenant scopes"/>
                      </div>
                    </section>
                  </div>
                  <div className="platform-footer-note">
                    <ShieldCheck size={15}/>
                    <span>Aggregate operational metrics only. This console does not expose customer documents, UPI details, passwords, or shop payment credentials.</span>
                  </div>
                  <div className="platform-generated-note">Snapshot generated {new Date(overview.generatedUtc).toLocaleString("en-IN")}.</div>
                </>
              ) : !overviewError ? <div className="panel platform-loading">No platform summary is available.</div> : null}
            </section>
          )}

          {section === "Tenants" && (
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
                  </select>
                </label>
              </div>
              {tenantError && <div className="inline-error" role="alert">{tenantError}</div>}
              {tenantLoading && !tenants ? <div className="platform-loading-inline"><span className="button-spinner"/> Loading shops…</div> : null}
              {tenants && (
                <>
                  <div className="platform-table-scroll">
                    <table className="platform-tenant-table">
                      <thead><tr><th>SHOP</th><th>TENANT STATUS</th><th>SUBSCRIPTION</th><th>DEVICES</th><th>JOBS · 30 DAYS</th><th>CREATED</th></tr></thead>
                      <tbody>
                        {tenants.items.map(tenant => (
                          <tr key={tenant.id}>
                            <td><div className="platform-shop-cell"><div className="platform-shop-icon"><Building2 size={15}/></div><div><strong>{tenant.businessName}</strong><small>{tenant.slug}</small></div></div></td>
                            <td><span className={`platform-tenant-badge ${toneForStatus(tenant.status)}`}>{prettyStatus(tenant.status)}</span></td>
                            <td><div className="platform-subscription-cell"><strong>{tenant.planCode || "Unassigned"}</strong><small>{tenant.subscriptionStatus ? prettyStatus(tenant.subscriptionStatus) : "No subscription"}</small>{tenant.subscriptionPeriodEndUtc && <small>Until {new Date(tenant.subscriptionPeriodEndUtc).toLocaleDateString("en-IN")}</small>}</div></td>
                            <td><div className="platform-device-cell"><strong>{tenant.activePrinterCount} / {tenant.printerCount} printers</strong><small>{tenant.agentCount} paired agents</small></div></td>
                            <td><strong className="platform-numeric">{tenant.jobsLast30Days.toLocaleString("en-IN")}</strong></td>
                            <td><span className="platform-date">{new Date(tenant.createdUtc).toLocaleDateString("en-IN")}</span></td>
                          </tr>
                        ))}
                        {tenants.items.length === 0 && <tr><td className="platform-no-results" colSpan={6}>No shops match this filter.</td></tr>}
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
          )}
          {section === "Plans" && <PlatformPlansSection tokens={tokens}/>}
        </div>
      </main>
    </div>
  );
}

function Metric({ icon, label, value, note, tone }: {
  icon: ReactNode; label: string; value: number; note: string; tone: string;
}) {
  return (
    <section className="metric-card platform-metric-card">
      <div className="metric-top"><div className={`metric-icon ${tone}`}>{icon}</div></div>
      <div className="metric-label">{label}</div>
      <div className="metric-value">{value.toLocaleString("en-IN")}</div>
      <div className="metric-foot"><span>{note}</span></div>
    </section>
  );
}

function HealthRow({ icon, title, value, detail }: {
  icon: React.ReactNode; title: string; value: string; detail: string;
}) {
  return (
    <div className="platform-health-row">
      <div className="platform-health-icon">{icon}</div>
      <div><strong>{title}</strong><small>{detail}</small></div>
      <span>{value}</span>
    </div>
  );
}

function FileIcon() {
  return <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6M8 13h8M8 17h8"/></svg>;
}

function toneForStatus(value: string): string {
  const status = value.toLowerCase();
  if (status === "active" || status === "completed" || status === "verified") return "tone-success";
  if (status === "trial" || status.includes("pending") || status.includes("awaiting")) return "tone-warning";
  if (status === "suspended" || status === "cancelled" || status === "expired" || status === "revoked") return "tone-danger";
  return "tone-neutral";
}

function prettyStatus(value: string): string {
  return value.replace(/[_-]+/g, " ").toLowerCase().replace(/\b\w/g, letter => letter.toUpperCase());
}
