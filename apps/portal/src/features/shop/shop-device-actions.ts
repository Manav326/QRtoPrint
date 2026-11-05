import type { Dispatch, SetStateAction } from "react";
import {
  createPrinter, createStation, rotateStationCode, updatePrinterActive, updateStationEnabled,
  type PrinterDevice, type QrStation
} from "../../api";
import type { ShopModal } from "./shop-types";

type ShopDeviceActionOptions = {
  accessToken: string;
  busy: boolean;
  setBusy: Dispatch<SetStateAction<boolean>>;
  busyResourceId: string | null;
  setBusyResourceId: Dispatch<SetStateAction<string | null>>;
  setModal: Dispatch<SetStateAction<ShopModal>>;
  setStations: Dispatch<SetStateAction<QrStation[]>>;
  setError: Dispatch<SetStateAction<string>>;
  setToast: Dispatch<SetStateAction<string>>;
  reload: () => Promise<void>;
  confirmInApp: (message: string, title?: string, confirmLabel?: string) => Promise<boolean>;
};

export function createShopDeviceActions({
  accessToken,busy,setBusy,busyResourceId,setBusyResourceId,setModal,setStations,setError,setToast,reload,confirmInApp
}:ShopDeviceActionOptions) {
  async function submitPrinter(values: {
    displayName: string; manufacturer: string; model: string; connectionType: string;
    connectionAddress?: string; queueName?: string;
  }) {
    setBusy(true);
    try {
      await createPrinter(accessToken, values);
      setModal(null);
      setToast("Printer added to your shop.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not add printer.");
    } finally {
      setBusy(false);
    }
  }

  async function submitStation(values: { displayName: string; printerDeviceId?: string }) {
    setBusy(true);
    try {
      const created = await createStation(accessToken, values);
      setStations(current => [...current, created]);
      setModal(null);
      setToast("QR station created. Save or print its QR code now.");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not create QR station.");
    } finally {
      setBusy(false);
    }
  }

  async function togglePrinter(printer: PrinterDevice) {
    if (busyResourceId) return;
    if (printer.isActive && !(await confirmInApp(
      `Disable ${printer.displayName}? New jobs can no longer be assigned to it. A Windows job already accepted by the spooler is not cancelled; an active job may prevent disabling until it is resolved.`,
      "Disable this printer?", "Disable printer"
    ))) return;
    setBusyResourceId(printer.id);
    setError("");
    try {
      await updatePrinterActive(accessToken, printer.id, !printer.isActive);
      setToast(printer.isActive ? "Printer disabled. Existing Windows output is not cancelled." : "Printer enabled for new assignments.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not update printer.");
    } finally {
      setBusyResourceId(null);
    }
  }

  async function toggleStation(station: QrStation) {
    if (busyResourceId) return;
    if (station.isEnabled && !(await confirmInApp(
      `Disable the QR station “${station.displayName}”? Customers will not be able to start new sessions from this QR until you enable it again. Previously started customer sessions are not recalled.`,
      "Disable this QR station?", "Disable station"
    ))) return;
    setBusyResourceId(station.id);
    setError("");
    try {
      const updated = await updateStationEnabled(accessToken, station.id, !station.isEnabled);
      setStations(current => current.map(item => item.id === station.id ? { ...item, isEnabled: updated.isEnabled } : item));
      setToast(updated.isEnabled ? "QR station enabled." : "QR station disabled. Existing customer sessions are unchanged.");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not update QR station.");
    } finally {
      setBusyResourceId(null);
    }
  }

  async function createFreshStationCode(station: QrStation) {
    if (busyResourceId) return;
    const confirmed = await confirmInApp("Generating a replacement QR will invalidate every previously printed sign for this station. Continue only when you intend to replace them.", "Replace this station's QR?", "Replace QR");
    if (!confirmed) return;
    setBusyResourceId(station.id);
    setError("");
    try {
      const result = await rotateStationCode(accessToken, station.id);
      setStations(current => current.map(item => item.id === station.id
        ? { ...item, publicUrl: result.publicUrl, publicCodeHint: result.publicCodeHint }
        : item));
      setToast("New QR code generated. The previous code is now invalid.");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not generate a QR code.");
    } finally {
      setBusyResourceId(null);
    }
  }
  return { submitPrinter,submitStation,togglePrinter,toggleStation,createFreshStationCode };
}
