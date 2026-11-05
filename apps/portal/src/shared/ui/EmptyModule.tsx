import type { ReactNode } from "react";
import { ArrowRight } from "lucide-react";

export interface EmptyModuleProps {
  icon: ReactNode;
  title: string;
  text: string;
  actionLabel?: string;
  onAction?: () => void;
}

export default function EmptyModule({ icon, title, text, actionLabel, onAction }: EmptyModuleProps) {
  return <section className="panel empty-module"><div className="empty-module-icon">{icon}</div><h2>{title}</h2><p>{text}</p>{actionLabel&&onAction&&<button className="primary-button" onClick={onAction}>{actionLabel}<ArrowRight size={15}/></button>}</section>;
}
