import type { Dispatch, SetStateAction } from "react";
import {
  rejectShopPayment, updateShopOwnerAccount, updateShopPaymentSettings, updateShopPricing,
  updateShopProfile, verifyShopPayment,
  type ShopPayment, type ShopPaymentSettings, type ShopPricingProfile, type ShopProfile
} from "../../api";

type ShopBillingActionOptions = {
  accessToken: string;
  setBusy: Dispatch<SetStateAction<boolean>>;
  setBusyJobId: Dispatch<SetStateAction<string | null>>;
  setError: Dispatch<SetStateAction<string>>;
  setToast: Dispatch<SetStateAction<string>>;
  reload: () => Promise<void>;
  setPaymentSettings: Dispatch<SetStateAction<ShopPaymentSettings | null>>;
  setProfile: Dispatch<SetStateAction<ShopProfile | null>>;
  setPricing: Dispatch<SetStateAction<ShopPricingProfile | null>>;
  setOwnerAccountOpen: Dispatch<SetStateAction<boolean>>;
  confirmInApp: (message: string, title?: string, confirmLabel?: string) => Promise<boolean>;
  promptInApp: (message: string, title?: string, inputLabel?: string) => Promise<string | null>;
};

export function createShopBillingActions({
  accessToken,setBusy,setBusyJobId,setError,setToast,reload,setPaymentSettings,setProfile,setPricing,
  setOwnerAccountOpen,confirmInApp,promptInApp
}:ShopBillingActionOptions) {
  async function savePaymentSettings(values: {
    upiEnabled: boolean; upiId: string | null; payeeName: string | null; cashEnabled: boolean; instantPaymentEnabled: boolean;
  }) {
    setBusy(true);
    setError("");
    try {
      const updated = await updateShopPaymentSettings(accessToken, values);
      setPaymentSettings(updated);
      setToast("Shop payment settings saved. Customer print revenue continues to go directly to your shop.");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not save shop payment settings.");
    } finally {
      setBusy(false);
    }
  }

  async function verifyPayment(payment: ShopPayment) {
    const isBatch = Boolean(payment.batchPaymentId);
    const confirmation = payment.method === "UPI"
      ? isBatch
        ? `Have you independently confirmed the full batch UPI amount in your shop's bank/UPI app? Reference: ${payment.paymentReference || "not supplied"}`
        : `Have you independently confirmed this UPI transaction in your shop's bank/UPI app? Reference: ${payment.paymentReference || "not supplied"}`
      : isBatch
        ? "Have you physically received the full cash amount for this batch?"
        : "Have you physically received the cash for this print job?";
    if (!(await confirmInApp(confirmation, "Verify payment received", isBatch ? "Verify batch payment" : "Verify payment"))) return;

    setBusyJobId(payment.printJobId);
    setError("");
    try {
      await verifyShopPayment(accessToken, payment.id);
      setToast(isBatch
        ? "Batch payment verified. Every accepted file is now authorised; assign and dispatch each file through the print queue."
        : "Payment verified. The job is paid/authorised; printing still requires printer assignment and an explicit dispatch.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not verify this payment.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function rejectPayment(payment: ShopPayment) {
    const cancellationWarning = payment.method === "UPI" &&
      ["AWAITING_CUSTOMER_PAYMENT","CUSTOMER_REPORTED"].includes(payment.status)
      ? "Before rejecting this UPI attempt, check your bank/UPI ledger against the full amount and the customer's reference. A submitted or missing UTR does not prove that money was or was not received. If the credit is present, verify this attempt instead of rejecting it.\n\n"
      : "";
    const reason = await promptInApp(
      cancellationWarning + (payment.batchPaymentId
        ? "Explain why this full batch payment is being rejected."
        : "Explain why this payment report is being rejected."),
      payment.batchPaymentId ? "Reject batch payment" : "Reject payment report",
      "Rejection reason"
    );
    if (reason === null) return;
    if (reason.trim().length < 3) {
      setError("A rejection reason of at least 3 characters is required.");
      return;
    }

    setBusyJobId(payment.printJobId);
    setError("");
    try {
      await rejectShopPayment(accessToken, payment.id, reason.trim());
      setToast(payment.batchPaymentId
        ? "Batch payment rejected. The customer can retry checkout for the accepted files."
        : "Payment report rejected. The customer can choose a new payment attempt.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not reject this payment.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function saveProfile(values: { displayName: string; logoUrl: string | null; accentColor: string }) {
    setBusy(true);
    try {
      await updateShopProfile(accessToken, values);
      setToast("Shop identity updated.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not update shop identity.");
    } finally {
      setBusy(false);
    }
  }

  async function saveOwnerAccount(values: { ownerName: string; email: string; currentPassword?: string }) {
    setBusy(true);
    try {
      const updated = await updateShopOwnerAccount(accessToken, values);
      setProfile(current => current ? {
        ...current,
        ownerName: updated.ownerName,
        ownerEmail: updated.ownerEmail
      } : current);
      setOwnerAccountOpen(false);
      setToast(updated.emailChanged
        ? "Personal details saved. Your new email is now your sign-in address."
        : "Personal details updated.");
    } finally {
      setBusy(false);
    }
  }

  async function savePricing(values: {
    monoPerSideMinor: number;
    colourPerSideMinor: number;
    monoDuplexPerSheetMinor: number;
    colourDuplexPerSheetMinor: number;
    minimumChargeMinor: number;
    photo35x45Minor:number; photo25x35Minor:number; photo2x2Minor:number; photo4x6Minor:number;
  }) {
    setBusy(true);
    try {
      const updated = await updateShopPricing(accessToken, values);
      setPricing(updated);
      setToast("Print rate card saved. New quotes will use this pricing version.");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not save print prices.");
    } finally {
      setBusy(false);
    }
  }
  return { savePaymentSettings,verifyPayment,rejectPayment,saveProfile,saveOwnerAccount,savePricing };
}
