import type { ReactNode } from "react";
import { X } from "lucide-react";

export interface ModalProps {
  title: string;
  subtitle: string;
  children: ReactNode;
  onClose: () => void;
}

export default function Modal({ title, subtitle, children, onClose }: ModalProps) {
  return <div className="modal-scrim" onMouseDown={e=>{if(e.target===e.currentTarget)onClose();}}><section className="modal-card" role="dialog" aria-modal="true" aria-label={title}><div className="modal-heading"><div><h2>{title}</h2><p>{subtitle}</p></div><button className="icon-button" onClick={onClose} aria-label="Close"><X size={18}/></button></div>{children}</section></div>;
}
