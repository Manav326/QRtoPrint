export interface StatusProps {
  label: string;
  tone: string;
}

export default function Status({ label, tone }: StatusProps) { return <span className={"status-badge "+tone}><i/>{label}</span>; }
