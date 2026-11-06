import { apiRequest } from "./core";
import type { ShopPaymentSettings } from "./shop";

export function updateShopPaymentSettings(token: string, values: {
  upiEnabled: boolean;
  upiId: string | null;
  payeeName: string | null;
  cashEnabled: boolean;
  instantPaymentEnabled: boolean;
}) {
  return apiRequest<ShopPaymentSettings>("/api/v1/shop/payment-settings", {
    method: "PUT",
    body: JSON.stringify(values)
  }, token);
}

export function verifyShopPayment(token: string, paymentId: string) {
  return apiRequest<{ id: string; status: string; jobNumber: string; jobStatus: string }>(
    `/api/v1/shop/payments/${paymentId}/verify`,
    { method: "POST" },
    token
  );
}

export function rejectShopPayment(token: string, paymentId: string, reason: string) {
  return apiRequest<{ id: string; status: string; rejectionReason: string }>(
    `/api/v1/shop/payments/${paymentId}/reject`,
    { method: "POST", body: JSON.stringify({ reason }) },
    token
  );
}

export interface CustomerBatchPayment {
  id: string;
  method: "UPI" | "CASH";
  status: string;
  amountMinor: number;
  currency: string;
  upiIdSnapshot?: string | null;
  payeeNameSnapshot?: string | null;
  paymentReference?: string | null;
  rejectionReason?: string | null;
  upiUri?: string | null;
  batchId: string;
  fileCount: number;
  createdUtc: string;
  updatedUtc?: string;
  verifiedUtc?: string | null;
}

export interface CustomerPayment {
  id: string;
  batchPaymentId?: string | null;
  method: "UPI" | "CASH";
  status: string;
  amountMinor: number;
  refundedAmountMinor?: number;
  currency: string;
  upiIdSnapshot?: string | null;
  payeeNameSnapshot?: string | null;
  paymentReference?: string | null;
  upiUri?: string | null;
  batchPayment?: CustomerBatchPayment | null;
  jobNumber?: string;
  jobStatus?: string;
  createdUtc?: string;
  updatedUtc?: string;
  verifiedUtc?: string | null;
}

export function startCustomerBatchPayment(sessionToken: string, batchId: string, method: "UPI" | "CASH") {
  return apiRequest<CustomerBatchPayment>(`/api/v1/public/batches/${batchId}/payments`, {
    method: "POST",
    headers: { "X-Print-Session": sessionToken },
    body: JSON.stringify({ method })
  });
}

export function reportCustomerBatchPayment(sessionToken: string, paymentId: string, referenceNumber: string) {
  return apiRequest<{
    id: string;
    status: string;
    amountMinor: number;
    currency: string;
    paymentReference: string;
    fileCount: number;
    message: string;
  }>(`/api/v1/public/batch-payments/${paymentId}/report`, {
    method: "POST",
    headers: { "X-Print-Session": sessionToken },
    body: JSON.stringify({ referenceNumber })
  });
}

export function startCustomerPayment(sessionToken: string, jobId: string, method: "UPI" | "CASH") {
  return apiRequest<CustomerPayment>(`/api/v1/public/jobs/${jobId}/payments`, {
    method: "POST",
    headers: { "X-Print-Session": sessionToken },
    body: JSON.stringify({ method })
  });
}

export function reportCustomerUpiPayment(sessionToken: string, paymentId: string, referenceNumber: string) {
  return apiRequest<{ id: string; status: string; paymentReference: string; jobStatus: string; message: string }>(
    `/api/v1/public/payments/${paymentId}/report`,
    {
      method: "POST",
      headers: { "X-Print-Session": sessionToken },
      body: JSON.stringify({ referenceNumber })
    }
  );
}


