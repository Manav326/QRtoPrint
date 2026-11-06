import { useEffect, useState } from "react";
import {
  Activity, Building2, CreditCard, Layers3, LogOut, Menu, PanelLeftClose, PanelLeftOpen, Printer, RefreshCw, Settings2, ShieldCheck
} from "lucide-react";
import PlatformPlansSection from "./PlatformPlansSection";
import PlatformSubscriptionsSection from "./PlatformSubscriptionsSection";
import PlatformOperationsSection from "./PlatformOperationsSection";
import { adminPathForSection, adminSectionFromPath } from "./portal-routing";
import type { AuthTokens } from "./api";
import { usePlatformAdminData } from "./features/platform-admin/usePlatformAdminData";
import PlatformOverviewSection from "./features/platform-admin/PlatformOverviewSection";
import PlatformTenantsSection from "./features/platform-admin/PlatformTenantsSection";
import { readSidebarCollapsedPreference, saveSidebarCollapsedPreference } from "./ui-preferences";
import ThemeToggleButton from "./ThemeToggleButton";

type Section = "Overview" | "Tenants" | "Plans" | "Subscriptions" | "Operations";

export default function PlatformAdminConsole({
  tokens,
  onSignOut,
  dark,
  onToggleTheme
}: {
  tokens: AuthTokens;
  onSignOut: () => Promise<void>;
  dark: boolean;
  onToggleTheme: () => void;
}) {
  const [section, setSection] = useState<Section>(() => adminSectionFromPath(window.location.pathname));

  useEffect(() => {
    const syncSectionFromPath = () => setSection(adminSectionFromPath(window.location.pathname));
    window.addEventListener("popstate", syncSectionFromPath);
    return () => window.removeEventListener("popstate", syncSectionFromPath);
  }, []);
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(readSidebarCollapsedPreference);
  useEffect(() => { saveSidebarCollapsedPreference(sidebarCollapsed); }, [sidebarCollapsed]);
  const {
    overview,overviewLoading,overviewError,tenants,tenantLoading,tenantError,tenantActionError,tenantActionId,
    statusHistoryTenant,statusHistory,search,setSearch,status,setStatus,page,setPage,signingOut,
    refreshData,changeTenantStatus,showTenantStatusHistory,closeTenantStatusHistory
  } = usePlatformAdminData({tokens,onSignOut});

  function toggleSidebarCollapsed() { setSidebarCollapsed(value => !value); }

  function navigate(next: Section) {
    const nextPath = adminPathForSection(next);
    if (window.location.pathname !== nextPath) {
      window.history.pushState({}, "", nextPath);
    }
    setSection(next);
    setSidebarOpen(false);
  }

  const title = section === "Overview" ? "Platform overview" : section === "Tenants" ? "Shop tenants" : section === "Plans" ? "Plans and entitlements" : section === "Subscriptions" ? "Shop subscriptions" : "Operations and retention";
  const subtitle = section === "Overview"
    ? "A cross-platform view of tenant health, subscriptions, devices and recent activity."
    : section === "Tenants"
      ? "Find shops and inspect their current subscription and connected-device footprint."
      : section === "Plans" ? "Configure versioned SaaS plans, prices and centrally managed capabilities."
      : section === "Subscriptions" ? "Manage trial and free-plan assignments with a permanent audit trail."
      : "Configure platform-wide print quotas, retention, cleanup schedules and suspended-shop recovery policy.";

  return (
    <div className={`app-shell platform-admin-shell${dark ? "" : " light"}${sidebarCollapsed ? " sidebar-collapsed" : ""}`}>
      {sidebarOpen && <button className="mobile-scrim" aria-label="Close navigation" onClick={() => setSidebarOpen(false)}/>}
      <aside id="platform-navigation" className={`sidebar platform-sidebar${sidebarOpen ? " sidebar-open" : ""}`}>
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
          <button title="Overview" aria-current={section === "Overview" ? "page" : undefined} className={section === "Overview" ? "nav-item active" : "nav-item"} onClick={() => navigate("Overview")}><Activity size={17}/><span>Overview</span></button>
          <button title="Shop tenants" aria-current={section === "Tenants" ? "page" : undefined} className={section === "Tenants" ? "nav-item active" : "nav-item"} onClick={() => navigate("Tenants")}><Building2 size={17}/><span>Shop tenants</span>{overview && <span className="nav-count">{overview.tenantCount}</span>}</button>
          <button title="Plans & entitlements" aria-current={section === "Plans" ? "page" : undefined} className={section === "Plans" ? "nav-item active" : "nav-item"} onClick={() => navigate("Plans")}><Layers3 size={17}/><span>Plans & entitlements</span></button>
          <button title="Shop subscriptions" aria-current={section === "Subscriptions" ? "page" : undefined} className={section === "Subscriptions" ? "nav-item active" : "nav-item"} onClick={() => navigate("Subscriptions")}><CreditCard size={17}/><span>Shop subscriptions</span></button>
          <button title="Operations & retention" aria-current={section === "Operations" ? "page" : undefined} className={section === "Operations" ? "nav-item active" : "nav-item"} onClick={() => navigate("Operations")}><Settings2 size={17}/><span>Operations & retention</span></button>
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
          <div className="topbar-left">
            <button className="icon-button sidebar-toggle" aria-label={sidebarCollapsed ? "Expand navigation" : "Collapse navigation"} aria-controls="platform-navigation" aria-expanded={!sidebarCollapsed} title={sidebarCollapsed ? "Expand navigation" : "Collapse navigation"} onClick={toggleSidebarCollapsed}>{sidebarCollapsed ? <PanelLeftOpen size={18}/> : <PanelLeftClose size={18}/>}</button>
            <button className="icon-button mobile-menu" onClick={() => setSidebarOpen(true)} aria-label="Open navigation" aria-controls="platform-navigation"><Menu size={19}/></button>
            <div className="breadcrumb"><span>Smart Print</span><span className="crumb-slash">/</span><strong>{title}</strong></div>
          </div>
          <div className="topbar-actions">
            <span className="platform-session-pill"><i/> Platform session secured</span>
            <span className="topbar-divider"/>
            <ThemeToggleButton dark={dark} onToggle={onToggleTheme} className="theme-toggle-compact"/>
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
              <span className="platform-readonly-label"><ShieldCheck size={13}/> {section === "Plans" ? "Plan configuration" : section === "Operations" ? "Platform policy" : "Read-only monitoring"}</span>
              <button className="secondary-button platform-refresh" onClick={refreshData} disabled={overviewLoading || tenantLoading}>
                <RefreshCw size={14}/> Refresh data
              </button>
            </div>
          </div>

          {overviewError && <div className="inline-error" role="alert">{overviewError}</div>}
          {section === "Overview" && <PlatformOverviewSection overview={overview} overviewLoading={overviewLoading} overviewError={overviewError}/>}
          {section === "Tenants" && <PlatformTenantsSection
            tenants={tenants} tenantLoading={tenantLoading} tenantError={tenantError}
            tenantActionError={tenantActionError} tenantActionId={tenantActionId}
            statusHistoryTenant={statusHistoryTenant} statusHistory={statusHistory}
            search={search} setSearch={setSearch} status={status} setStatus={setStatus}
            page={page} setPage={setPage} changeTenantStatus={changeTenantStatus}
            showTenantStatusHistory={showTenantStatusHistory} closeTenantStatusHistory={closeTenantStatusHistory}
          />}
          {section === "Plans" && <PlatformPlansSection tokens={tokens}/>}
          {section === "Subscriptions" && <PlatformSubscriptionsSection tokens={tokens}/>}
          {section === "Operations" && <PlatformOperationsSection tokens={tokens}/>}
        </div>
      </main>
    </div>
  );
}

