export interface PrintKindTagProps {
  printKind?: string | null;
  count?: number;
}

export default function PrintKindTag({ printKind, count }: PrintKindTagProps) {
  const isPhoto=printKind==="PHOTO";
  const label=isPhoto?"Photo":"Document";
  const countLabel=typeof count==="number"&&count>1?` · ${count}`:"";
  return <span className={`print-kind-tag print-kind-tag--${isPhoto?"photo":"document"}`} title={isPhoto?"Photo printing job":"Document printing job"}>{label}{countLabel}</span>;
}
