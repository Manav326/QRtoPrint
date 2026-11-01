const IST_TIME_ZONE = "Asia/Kolkata";

function asValidDate(value: string | Date | null | undefined): Date | null {
  if (value == null) return null;
  const date = value instanceof Date ? value : new Date(value);
  return Number.isFinite(date.getTime()) ? date : null;
}

export function formatIstDateTime(value: string | Date | null | undefined): string {
  const date = asValidDate(value);
  return date
    ? `${new Intl.DateTimeFormat("en-IN", {
        timeZone: IST_TIME_ZONE,
        day: "2-digit",
        month: "short",
        year: "numeric",
        hour: "2-digit",
        minute: "2-digit",
        hour12: true
      }).format(date)} IST`
    : "—";
}

export function formatIstDate(value: string | Date | null | undefined): string {
  const date = asValidDate(value);
  return date
    ? `${new Intl.DateTimeFormat("en-IN", {
        timeZone: IST_TIME_ZONE,
        day: "numeric",
        month: "short",
        year: "numeric"
      }).format(date)} IST`
    : "—";
}

export function formatIstTime(value: string | Date | null | undefined): string {
  const date = asValidDate(value);
  return date
    ? `${new Intl.DateTimeFormat("en-IN", {
        timeZone: IST_TIME_ZONE,
        hour: "2-digit",
        minute: "2-digit",
        hour12: true
      }).format(date)} IST`
    : "—";
}

export function istDateKey(value: string | Date): string {
  const date = asValidDate(value);
  return date
    ? new Intl.DateTimeFormat("en-CA", {
        timeZone: IST_TIME_ZONE,
        year: "numeric",
        month: "2-digit",
        day: "2-digit"
      }).format(date)
    : "";
}
