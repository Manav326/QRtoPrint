import { useEffect, useState } from "react";
import { ShieldCheck, Wallet } from "lucide-react";
import { formatIstDateTime } from "../../date-time";
import type { ShopPricingProfile } from "../../api";

export interface PricingUpdate {
  monoPerSideMinor: number;
  colourPerSideMinor: number;
  monoDuplexPerSheetMinor: number;
  colourDuplexPerSheetMinor: number;
  minimumChargeMinor: number;
  photo35x45Minor: number;
  photo25x35Minor: number;
  photo2x2Minor: number;
  photo4x6Minor: number;
}

interface PricingPageProps {
  pricing: ShopPricingProfile;
  onSave: (values: PricingUpdate) => void;
  busy: boolean;
}

export default function PricingPage({ pricing, onSave, busy }: PricingPageProps) {
  const [mono,setMono]=useState((pricing.monoPerSideMinor/100).toFixed(2));
  const [colour,setColour]=useState((pricing.colourPerSideMinor/100).toFixed(2));
  const [monoDuplex,setMonoDuplex]=useState((pricing.monoDuplexPerSheetMinor/100).toFixed(2));
  const [colourDuplex,setColourDuplex]=useState((pricing.colourDuplexPerSheetMinor/100).toFixed(2));
  const [minimum,setMinimum]=useState((pricing.minimumChargeMinor/100).toFixed(2));
  const [photo35x45,setPhoto35x45]=useState((pricing.photo35x45Minor/100).toFixed(2));const [photo25x35,setPhoto25x35]=useState((pricing.photo25x35Minor/100).toFixed(2));const [photo2x2,setPhoto2x2]=useState((pricing.photo2x2Minor/100).toFixed(2));const [photo4x6,setPhoto4x6]=useState((pricing.photo4x6Minor/100).toFixed(2));
  useEffect(()=>{
    setMono((pricing.monoPerSideMinor/100).toFixed(2));
    setColour((pricing.colourPerSideMinor/100).toFixed(2));
    setMonoDuplex((pricing.monoDuplexPerSheetMinor/100).toFixed(2));
    setColourDuplex((pricing.colourDuplexPerSheetMinor/100).toFixed(2));
    setMinimum((pricing.minimumChargeMinor/100).toFixed(2));
    setPhoto35x45((pricing.photo35x45Minor/100).toFixed(2));setPhoto25x35((pricing.photo25x35Minor/100).toFixed(2));setPhoto2x2((pricing.photo2x2Minor/100).toFixed(2));setPhoto4x6((pricing.photo4x6Minor/100).toFixed(2));
  },[pricing]);
  const toMinor=(value:string)=>Math.round(Number(value)*100);
  const valid=[mono,colour,monoDuplex,colourDuplex,minimum,photo35x45,photo25x35,photo2x2,photo4x6].every(value=>value.trim()!==""&&Number.isFinite(Number(value))&&Number(value)>=0);
  return <section className="panel settings-panel pricing-page">
    <div className="settings-intro"><div className="settings-icon"><Wallet size={20}/></div><div><h2>Print rate card</h2><p>Set the prices your customers see before they authorise a job.</p></div><span className={pricing.isConfigured?"pricing-status configured":"pricing-status"}>{pricing.isConfigured?"Active rate card":"Not configured"}</span></div>
    <div className="pricing-disclaimer"><ShieldCheck size={16}/><span>Prices are stored in paise and calculated by the server. Updating this rate card affects new quotes; existing quotes retain their saved price and pricing version.</span></div>
    <form className="settings-form" onSubmit={event=>{
      event.preventDefault();
      if(!valid||toMinor(mono)<=0||toMinor(colour)<=0)return;
      onSave({
        monoPerSideMinor:toMinor(mono),
        colourPerSideMinor:toMinor(colour),
        monoDuplexPerSheetMinor:toMinor(monoDuplex),
        colourDuplexPerSheetMinor:toMinor(colourDuplex),
        minimumChargeMinor:toMinor(minimum),photo35x45Minor:toMinor(photo35x45),photo25x35Minor:toMinor(photo25x35),photo2x2Minor:toMinor(photo2x2),photo4x6Minor:toMinor(photo4x6)
      });
    }}>
      <div className="pricing-group-heading"><strong>Single-sided printing</strong><span>Price per A4 printed side</span></div>
      <div className="pricing-input-grid">
        <label>Black & white <span className="price-input-wrap"><span>₹</span><input type="number" min="0.01" max="1000" step="0.01" value={mono} onChange={e=>setMono(e.target.value)} required/></span></label>
        <label>Colour <span className="price-input-wrap"><span>₹</span><input type="number" min="0.01" max="1000" step="0.01" value={colour} onChange={e=>setColour(e.target.value)} required/></span></label>
      </div>
      <div className="pricing-group-heading"><strong>Double-sided printing</strong><span>Price per physical A4 sheet (two sides)</span></div>
      <div className="pricing-input-grid">
        <label>Black & white duplex <span className="price-input-wrap"><span>₹</span><input type="number" min="0" max="1000" step="0.01" value={monoDuplex} onChange={e=>setMonoDuplex(e.target.value)}/></span></label>
        <label>Colour duplex <span className="price-input-wrap"><span>₹</span><input type="number" min="0" max="1000" step="0.01" value={colourDuplex} onChange={e=>setColourDuplex(e.target.value)}/></span></label>
      </div>
      <div className="pricing-group-heading"><strong>Minimum order</strong><span>Applied if the calculated print total is lower</span></div>
      <label>Minimum job charge <span className="price-input-wrap minimum-price"><span>₹</span><input type="number" min="0" max="100000" step="0.01" value={minimum} onChange={e=>setMinimum(e.target.value)}/></span></label>
      <div className="pricing-group-heading"><strong>Photo printing</strong><span>Price per finished photo · ₹0 disables a size</span></div><div className="pricing-input-grid photo-pricing-grid"><label>35 × 45 mm <span className="price-input-wrap"><span>₹</span><input type="number" min="0" step="0.01" value={photo35x45} onChange={e=>setPhoto35x45(e.target.value)}/></span></label><label>25 × 35 mm <span className="price-input-wrap"><span>₹</span><input type="number" min="0" step="0.01" value={photo25x35} onChange={e=>setPhoto25x35(e.target.value)}/></span></label><label>2 × 2 inch <span className="price-input-wrap"><span>₹</span><input type="number" min="0" step="0.01" value={photo2x2} onChange={e=>setPhoto2x2(e.target.value)}/></span></label><label>4 × 6 inch <span className="price-input-wrap"><span>₹</span><input type="number" min="0" step="0.01" value={photo4x6} onChange={e=>setPhoto4x6(e.target.value)}/></span></label></div><div className="settings-summary"><ShieldCheck size={16}/><span>Photo rates are per finished photo, not per A4 sheet. The quote records the rate used.</span></div>
      <div className="settings-summary"><ShieldCheck size={16}/><span>Customer payment gateway setup is separate and is not active yet. This screen configures print quotations only; no money is collected here.</span></div>
      <div className="settings-actions"><button className="primary-button" disabled={busy||!valid||toMinor(mono)<=0||toMinor(colour)<=0}>{busy?<span className="button-spinner"/>:"Save print prices"}</button></div>
    </form>
    <div className="pricing-version-row"><span>Rate card version</span><strong>v{pricing.version}</strong><span>Last updated</span><strong>{formatIstDateTime(pricing.updatedUtc)}</strong></div>
  </section>;
}
