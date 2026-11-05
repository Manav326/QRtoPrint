import type { ReactNode } from "react";
import { Cloud, FileText, Sparkles, ToggleLeft, ToggleRight } from "lucide-react";
import type { PlanForm } from "./platform-plans-model";

export function Field({ label, hint, wide, children }: {
  label: string;
  hint?: string;
  wide?: boolean;
  children: ReactNode;
}) {
  return <label className={wide ? "platform-plan-field wide" : "platform-plan-field"}>
    <span>{label}{hint && <small>{hint}</small>}</span>
    {children}
  </label>;
}

function PlanVersionEditor({ form, onChange }: {
  form: PlanForm;
  onChange: <K extends keyof PlanForm>(key: K, value: PlanForm[K]) => void;
}) {
  return (
    <div className="platform-plan-version-editor">
      <div className="platform-plan-subheading"><div><h4>Pricing</h4><p>Amounts are stored as integer minor units for the selected currency.</p></div><Field label="Currency"><input value={form.currency} onChange={event => onChange("currency", event.target.value.toUpperCase())} maxLength={3} placeholder="INR"/></Field></div>
      <div className="platform-plan-meta-grid">
        <Field label="Monthly price · minor units"><input type="number" min="0" step="1" value={form.monthlyPriceMinor} onChange={event => onChange("monthlyPriceMinor", event.target.value)} placeholder="Blank = unavailable"/></Field>
        <Field label="Annual price · minor units"><input type="number" min="0" step="1" value={form.annualPriceMinor} onChange={event => onChange("annualPriceMinor", event.target.value)} placeholder="Blank = unavailable"/></Field>
      </div>

      <div className="platform-plan-subheading"><div><h4>Usage limits</h4><p>Blank means no configured cap; positive numbers enforce the configured limit when enforcement is connected.</p></div><span className="platform-plan-unlimited">Optional limits</span></div>
      <div className="platform-plan-meta-grid platform-plan-limits-grid">
        <Field label="Printers"><input type="number" min="1" step="1" value={form.maxPrinters} onChange={event => onChange("maxPrinters", event.target.value)} placeholder="No configured cap"/></Field>
        <Field label="Agent computers"><input type="number" min="1" step="1" value={form.maxAgentDevices} onChange={event => onChange("maxAgentDevices", event.target.value)} placeholder="No configured cap"/></Field>
        <Field label="QR stations"><input type="number" min="1" step="1" value={form.maxQrStations} onChange={event => onChange("maxQrStations", event.target.value)} placeholder="No configured cap"/></Field>
        <Field label="Print jobs / month"><input type="number" min="1" step="1" value={form.maxMonthlyPrintJobs} onChange={event => onChange("maxMonthlyPrintJobs", event.target.value)} placeholder="No configured cap"/></Field>
        <Field label="Maximum document size · bytes"><input type="number" min="1" step="1" value={form.maxDocumentBytes} onChange={event => onChange("maxDocumentBytes", event.target.value)} placeholder="No configured cap"/></Field>
      </div>

      <div className="platform-plan-subheading"><div><h4>Product capabilities</h4><p>Saved centrally and intended to be evaluated by the server.</p></div></div>
      <div className="platform-plan-feature-grid">
        <FeatureToggle icon={<Cloud size={16}/>} label="Cloud-assisted printing" description="Allow secure cloud transfer for print jobs" checked={form.cloudAssistedPrintingEnabled} onChange={value => onChange("cloudAssistedPrintingEnabled", value)}/>
        <FeatureToggle icon={<Sparkles size={16}/>} label="White-label experience" description="Remove or replace Smart Print platform branding" checked={form.whiteLabelEnabled} onChange={value => onChange("whiteLabelEnabled", value)}/>
        <FeatureToggle icon={<FileText size={16}/>} label="Advanced reports" description="Enable expanded shop reporting capabilities" checked={form.advancedReportsEnabled} onChange={value => onChange("advancedReportsEnabled", value)}/>
      </div>
    </div>
  );
}

function FeatureToggle({ icon, label, description, checked, onChange }: {
  icon: ReactNode;
  label: string;
  description: string;
  checked: boolean;
  onChange: (value: boolean) => void;
}) {
  return <label className={checked ? "platform-plan-feature enabled" : "platform-plan-feature"}>
    <span className="platform-plan-feature-icon">{icon}</span>
    <span className="platform-plan-feature-copy"><strong>{label}</strong><small>{description}</small></span>
    <input type="checkbox" checked={checked} onChange={event => onChange(event.target.checked)}/>
    <span className="platform-plan-feature-switch" aria-hidden="true">{checked ? <ToggleRight size={23}/> : <ToggleLeft size={23}/>}</span>
  </label>;
}
