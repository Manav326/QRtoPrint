import type { ReactNode } from "react";
import { Activity, Building2, CheckCircle2, Clock3, Monitor, Printer, ShieldCheck } from "lucide-react";
import type { PlatformOverview } from "../../api";
import { formatIstDateTime } from "../../date-time";
import { prettyStatus, toneForStatus } from "./platform-admin-formatting";

export default function PlatformOverviewSection({overview,overviewLoading}:{overview:PlatformOverview|null;overviewLoading:boolean}) {
  return (
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
                      <div className="platform-generated-note">Snapshot generated {formatIstDateTime(overview.generatedUtc)}.</div>
                    </>
                  ) : !overviewError ? <div className="panel platform-loading">No platform summary is available.</div> : null}
                </section>
  );
}

function Metric({icon,label,value,note,tone}:{icon:ReactNode;label:string;value:number;note:string;tone:string}) {
  return (
    <section className="metric-card platform-metric-card">
      <div className="metric-top"><div className={`metric-icon ${tone}`}>{icon}</div></div>
      <div className="metric-label">{label}</div>
      <div className="metric-value">{value.toLocaleString("en-IN")}</div>
      <div className="metric-foot"><span>{note}</span></div>
    </section>
  );
}

function HealthRow({icon,title,value,detail}:{icon:React.ReactNode;title:string;value:string;detail:string}) {
  return <div className="platform-health-row"><div className="platform-health-icon">{icon}</div><div><strong>{title}</strong><small>{detail}</small></div><span>{value}</span></div>;
}

function FileIcon() {
  return <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6M8 13h8M8 17h8"/></svg>;
}
