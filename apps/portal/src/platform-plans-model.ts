import type { PlatformPlan, PlatformPlanVersion, PlatformPlanVersionInput } from "./api";

export type PlanForm = {
  currency: string;
  monthlyPriceMinor: string;
  annualPriceMinor: string;
  maxPrinters: string;
  maxAgentDevices: string;
  maxQrStations: string;
  maxMonthlyPrintJobs: string;
  maxDocumentBytes: string;
  cloudAssistedPrintingEnabled: boolean;
  whiteLabelEnabled: boolean;
  advancedReportsEnabled: boolean;
};

export const emptyForm: PlanForm = {
  currency: "INR",
  monthlyPriceMinor: "",
  annualPriceMinor: "",
  maxPrinters: "",
  maxAgentDevices: "",
  maxQrStations: "",
  maxMonthlyPrintJobs: "",
  maxDocumentBytes: "",
  cloudAssistedPrintingEnabled: false,
  whiteLabelEnabled: false,
  advancedReportsEnabled: false
};

export function formFromVersion(version?: PlatformPlanVersion): PlanForm {
  if (!version) return { ...emptyForm };
  return {
    currency: version.currency,
    monthlyPriceMinor: version.monthlyPriceMinor?.toString() ?? "",
    annualPriceMinor: version.annualPriceMinor?.toString() ?? "",
    maxPrinters: version.maxPrinters?.toString() ?? "",
    maxAgentDevices: version.maxAgentDevices?.toString() ?? "",
    maxQrStations: version.maxQrStations?.toString() ?? "",
    maxMonthlyPrintJobs: version.maxMonthlyPrintJobs?.toString() ?? "",
    maxDocumentBytes: version.maxDocumentBytes?.toString() ?? "",
    cloudAssistedPrintingEnabled: version.cloudAssistedPrintingEnabled,
    whiteLabelEnabled: version.whiteLabelEnabled,
    advancedReportsEnabled: version.advancedReportsEnabled
  };
}

export function optionalInteger(value: string, label: string, allowZero: boolean): number | null {
  if (!value.trim()) return null;
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed) || parsed < (allowZero ? 0 : 1)) {
    throw new Error(`${label} must be a whole number of at least ${allowZero ? "zero" : "one"}, or left blank.`);
  }
  return parsed;
}

export function toVersionInput(form: PlanForm): PlatformPlanVersionInput {
  const currency = form.currency.trim().toUpperCase();
  if (!/^[A-Z]{3}$/.test(currency)) throw new Error("Enter a three-letter currency code.");
  return {
    currency,
    monthlyPriceMinor: optionalInteger(form.monthlyPriceMinor, "Monthly price (minor units)", true),
    annualPriceMinor: optionalInteger(form.annualPriceMinor, "Annual price (minor units)", true),
    maxPrinters: optionalInteger(form.maxPrinters, "Printer limit", false),
    maxAgentDevices: optionalInteger(form.maxAgentDevices, "Agent computer limit", false),
    maxQrStations: optionalInteger(form.maxQrStations, "QR station limit", false),
    maxMonthlyPrintJobs: optionalInteger(form.maxMonthlyPrintJobs, "Monthly print-job limit", false),
    maxDocumentBytes: optionalInteger(form.maxDocumentBytes, "Document size limit (bytes)", false),
    cloudAssistedPrintingEnabled: form.cloudAssistedPrintingEnabled,
    whiteLabelEnabled: form.whiteLabelEnabled,
    advancedReportsEnabled: form.advancedReportsEnabled
  };
}

export function latestVersion(plan: PlatformPlan): PlatformPlanVersion | undefined {
  return [...plan.versions].sort((a, b) => b.version - a.version)[0];
}

export function draftVersion(plan: PlatformPlan): PlatformPlanVersion | undefined {
  return plan.versions.find(version => version.status === "Draft") ?? latestVersion(plan);
}

export function formatPrice(value: number | null, currency: string): string {
  return value === null ? "Not set" : `${currency} ${value.toLocaleString("en-IN")} minor units`;
}

export function optionalPreview(value: string): number | null {
  if (!value.trim()) return null;
  const number = Number(value);
  return Number.isSafeInteger(number) && number >= 0 ? number : null;
}
