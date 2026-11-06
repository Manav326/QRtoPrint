import { formatIstDate } from "./date-time";
import { CheckCircle2, CirclePlus, Layers3, LoaderCircle, LockKeyhole, RefreshCw, Save, ShieldCheck, Sparkles } from "lucide-react";
import type { AuthTokens } from "./api";
import { usePlatformPlans } from "./usePlatformPlans";
import { Field, PlanVersionEditor } from "./PlatformPlanVersionEditor";
import { formatPrice } from "./platform-plans-model";

export default function PlatformPlansSection({ tokens }: { tokens: AuthTokens }) {
  const {
    plans,selectedPlanId,setSelectedPlanId,creating,setCreating,loading,busy,error,setError,success,setSuccess,
    code,setCode,name,setName,description,setDescription,active,setActive,form,
    selectedPlan,currentDraft,hasDraft,isSystemPlan,monthlyMinor,annualMinor,
    reload,startCreate,changeForm,savePlan,saveMetadata,publishDraft
  } = usePlatformPlans(tokens);

  return (
    <section className="platform-plans">
      <div className="platform-plans-intro">
        <div>
          <div className="platform-plan-kicker"><Layers3 size={14}/> COMMERCIAL CATALOGUE</div>
          <h2>Plans and entitlements</h2>
          <p>Configure plan pricing and product capabilities. Changes are versioned so published terms remain stable.</p>
        </div>
        <div className="platform-plans-actions">
          <button className="secondary-button" onClick={() => void reload()} disabled={loading || Boolean(busy)}>
            <RefreshCw size={14}/> Refresh
          </button>
          <button className="primary-button" onClick={startCreate} disabled={loading || Boolean(busy)}>
            <CirclePlus size={15}/> New plan
          </button>
        </div>
      </div>

      {error && <div className="inline-error" role="alert">{error}</div>}
      {success && <div className="platform-plan-success" role="status"><CheckCircle2 size={15}/>{success}</div>}

      <div className="platform-plan-layout">
        <aside className="panel platform-plan-list-panel">
          <div className="platform-plan-list-heading"><strong>Plan catalogue</strong><span>{plans.length}</span></div>
          {loading ? (
            <div className="platform-plan-empty"><LoaderCircle size={17} className="platform-spin"/> Loading plans…</div>
          ) : plans.length === 0 ? (
            <div className="platform-plan-empty">No plans have been created.</div>
          ) : (
            <div className="platform-plan-list">
              {plans.map(plan => {
                const version = latestVersion(plan);
                return (
                  <button
                    type="button"
                    className={selectedPlanId === plan.id && !creating ? "platform-plan-list-item selected" : "platform-plan-list-item"}
                    key={plan.id}
                    onClick={() => { setCreating(false); setSelectedPlanId(plan.id); setSuccess(""); setError(""); }}
                  >
                    <span className="platform-plan-list-icon">{plan.isSystemPlan ? <LockKeyhole size={16}/> : <Layers3 size={16}/>}</span>
                    <span className="platform-plan-list-copy">
                      <strong>{plan.name}</strong>
                      <small>{plan.code} · {version ? `v${version.version} · ${version.status}` : "No versions"}</small>
                    </span>
                    <span className={plan.isActive ? "platform-plan-state active" : "platform-plan-state inactive"}>{plan.isActive ? "Active" : "Inactive"}</span>
                  </button>
                );
              })}
            </div>
          )}
          <div className="platform-plan-list-foot"><ShieldCheck size={14}/> System trial plan is protected.</div>
        </aside>

        <div className="platform-plan-editor">
          {creating ? (
            <section className="panel platform-plan-panel">
              <div className="platform-plan-panel-heading">
                <div><span className="platform-plan-panel-kicker">NEW COMMERCIAL PLAN</span><h3>Create a plan</h3><p>The initial version is saved as a draft and requires explicit publication.</p></div>
                <span className="platform-plan-draft-pill">Draft</span>
              </div>
              <div className="platform-plan-meta-grid">
                <Field label="Plan code" hint="Stable uppercase identifier">
                  <input value={code} onChange={event => setCode(event.target.value)} placeholder="e.g. STARTER" maxLength={40}/>
                </Field>
                <Field label="Plan name">
                  <input value={name} onChange={event => setName(event.target.value)} placeholder="Plan name" maxLength={100}/>
                </Field>
                <Field label="Description" wide>
                  <textarea value={description} onChange={event => setDescription(event.target.value)} placeholder="What this subscription includes" rows={2} maxLength={500}/>
                </Field>
              </div>
              <PlanVersionEditor form={form} onChange={changeForm}/>
              <div className="platform-plan-editor-actions">
                <span className="platform-plan-note">Prices are entered in minor currency units. Blank values are not configured; zero is explicitly free.</span>
                <button className="primary-button" onClick={() => void savePlan()} disabled={Boolean(busy)}>
                  {busy === "save" ? <LoaderCircle size={14} className="platform-spin"/> : <Save size={14}/>} Create plan draft
                </button>
              </div>
            </section>
          ) : selectedPlan ? (
            <>
              <section className="panel platform-plan-panel">
                <div className="platform-plan-panel-heading">
                  <div>
                    <span className="platform-plan-panel-kicker">{selectedPlan.isSystemPlan ? "PROTECTED SYSTEM PLAN" : "PLAN DETAILS"}</span>
                    <h3>{selectedPlan.name}</h3>
                    <p>{selectedPlan.code} · created {formatIstDate(selectedPlan.createdUtc)}</p>
                  </div>
                  {selectedPlan.isSystemPlan ? <span className="platform-plan-protected"><LockKeyhole size={13}/> Protected</span> : <span className={active ? "platform-plan-state active" : "platform-plan-state inactive"}>{active ? "Active" : "Inactive"}</span>}
                </div>
                <div className="platform-plan-meta-grid">
                  <Field label="Plan name"><input value={name} onChange={event => setName(event.target.value)} disabled={isSystemPlan} maxLength={100}/></Field>
                  <Field label="Plan code"><input value={selectedPlan.code} disabled/></Field>
                  <Field label="Description" wide><textarea value={description} onChange={event => setDescription(event.target.value)} disabled={isSystemPlan} rows={2} maxLength={500}/></Field>
                </div>
                {!isSystemPlan && (
                  <div className="platform-plan-editor-actions platform-plan-metadata-actions">
                    <label className="platform-plan-toggle">
                      <input type="checkbox" checked={active} onChange={event => setActive(event.target.checked)}/>
                      <span>{active ? "Available for future assignment" : "Hidden from future assignment"}</span>
                    </label>
                    <button className="secondary-button" onClick={() => void saveMetadata()} disabled={Boolean(busy)}>
                      {busy === "metadata" ? <LoaderCircle size={14} className="platform-spin"/> : <Save size={14}/>} Save details
                    </button>
                  </div>
                )}
              </section>

              <section className="panel platform-plan-panel">
                <div className="platform-plan-panel-heading">
                  <div><span className="platform-plan-panel-kicker">VERSIONED TERMS</span><h3>{hasDraft ? `Edit draft · v${currentDraft?.version}` : `Prepare next version · v${(latestVersion(selectedPlan)?.version ?? 0) + 1}`}</h3><p>Saving creates or updates a draft. Published terms cannot be edited in place.</p></div>
                  <span className={hasDraft ? "platform-plan-draft-pill" : "platform-plan-protected"}>{hasDraft ? "Unpublished draft" : "New draft"}</span>
                </div>
                {isSystemPlan ? (
                  <div className="platform-plan-system-note"><LockKeyhole size={16}/><div><strong>Trial safeguards</strong><span>This system plan preserves existing 14-day trials. Its terms are intentionally not editable from the commercial catalogue.</span></div></div>
                ) : (
                  <>
                    <PlanVersionEditor form={form} onChange={changeForm}/>
                    <div className="platform-plan-editor-actions">
                      <span className="platform-plan-note">Current entries: monthly {monthlyMinor === null ? "not set" : `${form.currency.toUpperCase()} ${monthlyMinor.toLocaleString("en-IN")} minor units`}; annual {annualMinor === null ? "not set" : `${form.currency.toUpperCase()} ${annualMinor.toLocaleString("en-IN")} minor units`}.</span>
                      <button className="primary-button" onClick={() => void savePlan()} disabled={Boolean(busy)}>
                        {busy === "save" ? <LoaderCircle size={14} className="platform-spin"/> : <Save size={14}/>} Save draft
                      </button>
                    </div>
                    {hasDraft && (
                      <div className="platform-plan-publish-row">
                        <div><strong>Ready to publish?</strong><span>At least one price is required. Enter zero for an explicit free plan.</span></div>
                        <button className="secondary-button" onClick={() => void publishDraft()} disabled={Boolean(busy) || (currentDraft?.monthlyPriceMinor == null && currentDraft?.annualPriceMinor == null)}>
                          {busy === "publish" ? <LoaderCircle size={14} className="platform-spin"/> : <Sparkles size={14}/>} Publish version
                        </button>
                      </div>
                    )}
                  </>
                )}
              </section>
            </>
          ) : (
            <section className="panel platform-plan-panel platform-plan-empty-editor">
              <Layers3 size={24}/><h3>Select or create a plan</h3><p>Choose a plan from the catalogue to inspect its versions and configure draft terms.</p>
            </section>
          )}
        </div>
      </div>

      {selectedPlan && !creating && (
        <section className="panel platform-plan-history">
          <div className="platform-plan-history-heading"><div><h3>Version history</h3><p>Each published version remains available for existing subscription references.</p></div><span>{selectedPlan.versions.length} versions</span></div>
          {selectedPlan.versions.length === 0 ? <div className="platform-plan-empty">No versions yet.</div> : (
            <div className="platform-plan-version-list">
              {[...selectedPlan.versions].sort((a,b)=>b.version-a.version).map(version => (
                <div className="platform-plan-version-row" key={version.id}>
                  <span className="platform-plan-version-number">v{version.version}</span>
                  <div className="platform-plan-version-copy">
                    <strong>{version.status}</strong>
                    <small>{version.currency} · monthly {formatPrice(version.monthlyPriceMinor, version.currency)} · annual {formatPrice(version.annualPriceMinor, version.currency)}</small>
                  </div>
                  <span className="platform-plan-version-date">{version.publishedUtc ? `Published ${formatIstDate(version.publishedUtc)}` : `Updated ${formatIstDate(version.updatedUtc)}`}</span>
                </div>
              ))}
            </div>
          )}
        </section>
      )}
    </section>
  );
}

function optionalPreview(value: string): number | null {
  if (!value.trim()) return null;
  const number = Number(value);
  return Number.isSafeInteger(number) && number >= 0 ? number : null;
}

