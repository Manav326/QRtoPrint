export function toneForStatus(value:string):string {
  const status=value.toLowerCase();
  if(status==="active"||status==="completed"||status==="verified")return "tone-success";
  if(status==="trial"||status.includes("pending")||status.includes("awaiting"))return "tone-warning";
  if(status==="suspended"||status==="cancelled"||status==="expired"||status==="revoked")return "tone-danger";
  return "tone-neutral";
}

export function prettyStatus(value:string):string {
  return value.replace(/[_-]+/g," ").toLowerCase().replace(/\b\w/g,letter=>letter.toUpperCase());
}
