import type { ReactNode } from "react";

export interface MetricCardProps {
  icon: ReactNode;
  label: string;
  value: string;
  note: string;
  accent: string;
}

export default function MetricCard({ icon, label, value, note, accent }: MetricCardProps) {
  return <section className="metric-card"><div className="metric-top"><div className={"metric-icon "+accent}>{icon}</div><span className="metric-dots"><i/><i/><i/></span></div><div className="metric-label">{label}</div><div className="metric-value">{value}</div><div className="metric-foot"><span>{note}</span></div></section>;
}
