import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import { Info } from "lucide-react";

export interface InfoTooltipProps {
  label: string;
  children: ReactNode;
}

export default function InfoTooltip({ label, children }: InfoTooltipProps) {
  const tooltipId=useId();
  const rootRef=useRef<HTMLSpanElement>(null);
  const [open,setOpen]=useState(false);
  useEffect(()=>{
    if(!open)return;
    const dismissOutside=(event:PointerEvent)=>{
      if(!rootRef.current?.contains(event.target as Node))setOpen(false);
    };
    document.addEventListener("pointerdown",dismissOutside);
    return ()=>document.removeEventListener("pointerdown",dismissOutside);
  },[open]);
  return <span ref={rootRef}
    className={open?"context-info context-info-open":"context-info"}
    onMouseEnter={()=>setOpen(true)}
    onMouseLeave={()=>setOpen(false)}
    onBlur={event=>{
      const nextTarget=event.relatedTarget;
      if(!(nextTarget instanceof Node)||!event.currentTarget.contains(nextTarget))setOpen(false);
    }}
  >
    <button type="button" className="context-info-trigger" aria-label={label}
      aria-expanded={open} aria-describedby={tooltipId}
      onFocus={()=>setOpen(true)} onClick={()=>setOpen(true)}
      onKeyDown={event=>{if(event.key==="Escape"){setOpen(false);event.currentTarget.blur();}}}
    ><Info size={14} strokeWidth={2}/></button>
    <span id={tooltipId} className="context-info-tooltip" role="tooltip" aria-hidden={!open}>{children}</span>
  </span>;
}
