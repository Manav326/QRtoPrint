import { apiRequest } from "./core";

export function createAgentEnrollmentCode(token: string) {
  return apiRequest<{ code: string; expiresUtc: string }>("/api/v1/shop/agent-enrollment-codes", {
    method: "POST"
  }, token);
}

export function revokeAgentDevice(token: string, agentId: string) {
  return apiRequest<{ id: string; status: string; revokedUtc: string }>(
    `/api/v1/shop/agent-devices/${agentId}/revoke`,
    { method: "PATCH" },
    token
  );
}

export function updateShopPaymentSettings(token: string, values: {
