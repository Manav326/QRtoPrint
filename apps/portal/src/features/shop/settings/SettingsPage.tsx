import { useEffect, useState } from "react";
import { Settings, ShieldCheck } from "lucide-react";
import type { ShopProfile } from "../../../api";

export interface ShopProfileUpdate {
  displayName: string;
  logoUrl: string | null;
  accentColor: string;
}

interface SettingsPageProps {
  profile: ShopProfile;
  onSave: (values: ShopProfileUpdate) => void;
  busy: boolean;
}

export default function SettingsPage({ profile, onSave, busy }: SettingsPageProps) {
  const [displayName,setDisplayName]=useState(profile.displayName);
  const [logoUrl,setLogoUrl]=useState(profile.logoUrl || "");
  const [accentColor,setAccentColor]=useState(profile.accentColor || "#7662E8");
  useEffect(()=>{setDisplayName(profile.displayName);setLogoUrl(profile.logoUrl||"");setAccentColor(profile.accentColor||"#7662E8");},[profile]);
  return <section className="panel settings-panel"><div className="settings-intro"><div className="settings-icon"><Settings size={20}/></div><div><h2>Shop profile &amp; branding</h2><p>Update the shop name and brand customers see when they connect through your QR stations.</p></div></div>
    <form className="settings-form" onSubmit={event=>{event.preventDefault();onSave({displayName:displayName.trim(),logoUrl:logoUrl.trim()||null,accentColor});}}>
      <label>Customer-facing shop name<input value={displayName} onChange={e=>setDisplayName(e.target.value)} required maxLength={180}/></label>
      <label>Logo URL <span className="field-hint">Optional · secure HTTPS image URL</span><input type="url" value={logoUrl} onChange={e=>setLogoUrl(e.target.value)} placeholder="https://example.com/logo.png"/></label>
      <label>Brand accent colour<div className="colour-picker-row"><input type="color" value={accentColor} onChange={e=>setAccentColor(e.target.value)}/><input value={accentColor} onChange={e=>setAccentColor(e.target.value)} pattern="^#[0-9A-Fa-f]{6}$" required/></div></label>
      <div className="settings-summary"><ShieldCheck size={16}/><span>Standard Smart Print branding is active. Full white-label branding is a paid plan upgrade.</span></div>
      <div className="settings-actions"><button className="primary-button" disabled={busy}>{busy?<span className="button-spinner"/>:"Save shop identity"}</button></div>
    </form>
    <div className="settings-meta"><span>Shop slug</span><strong>{profile.slug}</strong><span>Currency</span><strong>{profile.currency}</strong><span>Time zone</span><strong>{profile.timeZone}</strong></div>
  </section>;
}
