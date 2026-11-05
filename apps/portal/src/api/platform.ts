import { apiRequest } from "./core";


export interface PlatformOverview {
  generatedUtc: string;
  tenantCount: number;
  activeTenantCount: number;
  trialSubscriptionCount: number;
  activeSubscriptionCount: number;
  printerCount: number;
  activePrinterCount: number;
  agentCount: number;
  onlineAgentCount: number;
  jobsLast30Days: number;
  subscriptionsByStatus: { status: string; count: number }[];
}

export interface PlatformTenant {
  id: string;
  slug: string;
  businessName: string;
  status: string;
  createdUtc: string;
  planCode?: string | null;
  subscriptionStatus?: string | null;
  subscriptionPeriodEndUtc?: string | null;
  printerCount: number;
  activePrinterCount: number;
  agentCount: number;
  onlineAgentCount: number;
  offlineAgentCount: number;
  latestAgentHeartbeatUtc: string | null;
  jobsLast30Days: number;
}

export interface PlatformTenantsPage {
  page: number;
  pageSize: number;
  totalCount: number;
  items: PlatformTenant[];
}

export function updatePlatformTenantStatus(token: string, tenantId: string, status: "Active" | "Suspended" | "Inactive", reason: string) {
  return apiRequest<{ id: string; status: string; updatedUtc: string; duplicateRequest: boolean }>(
    `/api/v1/platform/tenants/${tenantId}/status`,
    { method: "PATCH", body: JSON.stringify({ status, reason }) },
    token
  );
}

export function getPlatformTenantStatusHistory(token: string, tenantId: string) {
  return apiRequest<Array<{
    id: string;
    previousStatus: string;
    newStatus: string;
    reason: string;
    actorUserId: string | null;
    createdUtc: string;
  }>>(`/api/v1/platform/tenants/${tenantId}/status-history`, {}, token);
}

export function getPlatformOverview(token: string) {
  return apiRequest<PlatformOverview>("/api/v1/platform/overview", {}, token);
}

export function getPlatformTenants(token: string, values: {
  search?: string;
  status?: string;
  page?: number;
  pageSize?: number;
}) {
  const params = new URLSearchParams();
  if (values.search?.trim()) params.set("search", values.search.trim());
  if (values.status) params.set("status", values.status);
  params.set("page", String(values.page ?? 1));
  params.set("pageSize", String(values.pageSize ?? 25));
  return apiRequest<PlatformTenantsPage>(`/api/v1/platform/tenants?${params.toString()}`, {}, token);
}
export interface PlatformPlanVersion {
  id: string;
  version: number;
  status: "Draft" | "Published" | "Retired" | string;
  currency: string;
  monthlyPriceMinor: number | null;
  annualPriceMinor: number | null;
  maxPrinters: number | null;
  maxAgentDevices: number | null;
  maxQrStations: number | null;
  maxMonthlyPrintJobs: number | null;
  maxDocumentBytes: number | null;
  cloudAssistedPrintingEnabled: boolean;
  whiteLabelEnabled: boolean;
  advancedReportsEnabled: boolean;
  createdUtc: string;
  updatedUtc: string;
  publishedUtc: string | null;
}

export interface PlatformPlan {
  id: string;
  code: string;
  name: string;
  description: string | null;
  isActive: boolean;
  isSystemPlan: boolean;
  createdUtc: string;
  updatedUtc: string;
  versions: PlatformPlanVersion[];
}

export interface PlatformPlanVersionInput {
  currency: string;
  monthlyPriceMinor: number | null;
  annualPriceMinor: number | null;
  maxPrinters: number | null;
  maxAgentDevices: number | null;
  maxQrStations: number | null;
  maxMonthlyPrintJobs: number | null;
  maxDocumentBytes: number | null;
  cloudAssistedPrintingEnabled: boolean;
  whiteLabelEnabled: boolean;
  advancedReportsEnabled: boolean;
}

export interface PlatformSubscriptionItem {
  id: string;
  slug: string;
  businessName: string;
  status: string;
  createdUtc: string;
  subscription: {
    id: string;
    planCode: string;
    status: string;
    billingInterval: string;
    periodStartUtc: string;
    periodEndUtc: string;
    createdUtc: string;
    planVersionId: string | null;
  } | null;
  plan: {
    id: string;
    code: string;
    name: string;
    version: number;
    status: string;
    currency: string;
    monthlyPriceMinor: number | null;
    annualPriceMinor: number | null;
    isActive: boolean;
  } | null;
}

export interface PlatformSubscriptionsPage {
  page: number;
  pageSize: number;
  totalCount: number;
  items: PlatformSubscriptionItem[];
}

export interface PlatformSubscriptionAudit {
  id: string;
  previousSubscriptionId: string | null;
  previousStatus: string | null;
  assignedSubscriptionId: string;
  planVersionId: string;
  actorUserId: string | null;
  eventType: string;
  planCode: string;
  billingInterval: string;
  status: string;
  amountMinor: number;
  currency: string;
  periodStartUtc: string;
  periodEndUtc: string;
  reason: string;
  paymentReference: string | null;
  idempotencyKey: string;
  createdUtc: string;
}

export function getPlatformSubscriptions(token: string, values: {
  search?: string;
  page?: number;
  pageSize?: number;
}) {
  const query = new URLSearchParams();
  if (values.search) query.set("search", values.search);
  if (values.page) query.set("page", String(values.page));
  if (values.pageSize) query.set("pageSize", String(values.pageSize));
  const suffix = query.toString();
  return apiRequest<PlatformSubscriptionsPage>(
    `/api/v1/platform/subscriptions${suffix ? `?${suffix}` : ""}`,
    {},
    token
  );
}

export function assignPlatformSubscription(token: string, tenantId: string, values: {
  planVersionId: string;
  billingInterval: string;
  reason: string;
  idempotencyKey: string;
  paymentConfirmed?: boolean;
  paymentReference?: string;
}) {
  return apiRequest<{
    subscriptionId: string;
    previousSubscriptionId: string | null;
    planCode: string;
    status: string;
    billingInterval: string;
    periodStartUtc: string;
    periodEndUtc: string;
    eventType: string;
    duplicateRequest: boolean;
  }>(`/api/v1/platform/tenants/${tenantId}/subscriptions`, {
    method: "POST",
    body: JSON.stringify(values)
  }, token);
}

export function getPlatformSubscriptionHistory(token: string, tenantId: string) {
  return apiRequest<PlatformSubscriptionAudit[]>(
    `/api/v1/platform/tenants/${tenantId}/subscription-history`,
    {},
    token
  );
}

export function getPlatformPlans(token: string) {
  return apiRequest<PlatformPlan[]>("/api/v1/platform/plans", {}, token);
}

export function createPlatformPlan(token: string, values: {
  code: string;
  name: string;
  description?: string;
  version: PlatformPlanVersionInput;
}) {
  return apiRequest<{ id: string; code: string; name: string; description: string | null; isActive: boolean; isSystemPlan: boolean; version: PlatformPlanVersion }>("/api/v1/platform/plans", {
    method: "POST",
    body: JSON.stringify(values)
  }, token);
}

export function updatePlatformPlan(token: string, planId: string, values: {
  name: string;
  description?: string;
  isActive: boolean;
}) {
  return apiRequest<{ id: string; code: string; name: string; description: string | null; isActive: boolean; updatedUtc: string }>(`/api/v1/platform/plans/${planId}`, {
    method: "PUT",
    body: JSON.stringify(values)
  }, token);
}

export function savePlatformPlanDraft(token: string, planId: string, values: PlatformPlanVersionInput) {
  return apiRequest<PlatformPlanVersion>(`/api/v1/platform/plans/${planId}/draft`, {
    method: "PUT",
    body: JSON.stringify(values)
  }, token);
}

export function publishPlatformPlanVersion(token: string, planId: string, version: number) {
  return apiRequest<PlatformPlanVersion>(
    `/api/v1/platform/plans/${planId}/versions/${version}/publish`,
    { method: "POST" },
    token
  );
}


export interface PlatformOperationalSettings {
  trialMaxMonthlyPrintJobs: number;
  cloudCompletedDocumentRetentionDays: number;
  agentCompletedDocumentRetentionDays: number;
  abandonedDocumentRetentionDays: number;
  pendingLocalUploadRetentionDays: number;
  expiredUploadTicketRetentionDays: number;
  emptyCustomerSessionRetentionDays: number;
  staleRefreshTokenRetentionDays: number;
  apiRetentionSweepHours: number;
  agentRetentionSweepHours: number;
  allowPaidJobClaimsWhenSuspended: boolean;
  updatedByUserId: string | null;
  updatedUtc: string;
}

export interface PlatformOperationalSettingsInput {
  trialMaxMonthlyPrintJobs: number;
  cloudCompletedDocumentRetentionDays: number;
  agentCompletedDocumentRetentionDays: number;
  abandonedDocumentRetentionDays: number;
  pendingLocalUploadRetentionDays: number;
  expiredUploadTicketRetentionDays: number;
  emptyCustomerSessionRetentionDays: number;
  staleRefreshTokenRetentionDays: number;
  apiRetentionSweepHours: number;
  agentRetentionSweepHours: number;
  allowPaidJobClaimsWhenSuspended: boolean;
  reason: string;
}

export interface PlatformOperationalSettingsAudit {
  id: string;
  reason: string;
  actorUserId: string | null;
  createdUtc: string;
  changesJson: string;
}

export function getPlatformOperationalSettings(token: string) {
  return apiRequest<PlatformOperationalSettings>("/api/v1/platform/settings/operations", {}, token);
}

export function getPlatformOperationalSettingsHistory(token: string) {
  return apiRequest<PlatformOperationalSettingsAudit[]>("/api/v1/platform/settings/operations/history", {}, token);
}

export function updatePlatformOperationalSettings(token: string, values: PlatformOperationalSettingsInput) {
  return apiRequest<PlatformOperationalSettings>("/api/v1/platform/settings/operations", {
    method: "PUT",
    body: JSON.stringify(values)
  }, token);
}

