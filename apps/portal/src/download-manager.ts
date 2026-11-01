import { useCallback, useEffect, useRef, useState } from "react";

export type FileDownloadState = {
  status: "idle" | "downloading" | "complete" | "cancelled" | "error";
  bytesReceived: number;
  totalBytes: number | null;
  errorMessage: string | null;
};

type WritableDownloadFile = {
  write(data: Uint8Array): Promise<void>;
  close(): Promise<void>;
  abort(): Promise<void>;
};

type SaveFileHandle = {
  createWritable(): Promise<WritableDownloadFile>;
};

type SaveFilePickerWindow = Window & {
  showSaveFilePicker?: (options: {
    suggestedName: string;
    types: Array<{
      description: string;
      accept: Record<string, string[]>;
    }>;
  }) => Promise<SaveFileHandle>;
};

const initialState: FileDownloadState = {
  status: "idle",
  bytesReceived: 0,
  totalBytes: null,
  errorMessage: null
};

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === "AbortError";
}

function getDownloadError(error: unknown): string {
  if (error instanceof Error && error.message) return error.message;
  return "The download could not be completed. Please try again.";
}

function saveBlob(blob: Blob, fileName: string): void {
  const objectUrl = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = objectUrl;
  anchor.download = fileName;
  anchor.style.display = "none";
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  window.setTimeout(() => URL.revokeObjectURL(objectUrl), 60_000);
}

/**
 * Downloads a same-origin release asset while measuring bytes from the actual
 * response stream. Where supported, the File System Access API writes chunks
 * directly to the selected file instead of buffering the whole installer.
 */
export function useFileDownload(fileName: string): {
  download: FileDownloadState;
  start: (url: string) => Promise<void>;
  cancel: () => void;
} {
  const [download, setDownload] = useState<FileDownloadState>(initialState);
  const controllerRef = useRef<AbortController | null>(null);

  const start = useCallback(async (url: string) => {
    if (controllerRef.current) return;

    const controller = new AbortController();
    controllerRef.current = controller;
    let writable: WritableDownloadFile | null = null;
    let completed = false;

    setDownload({
      status: "downloading",
      bytesReceived: 0,
      totalBytes: null,
      errorMessage: null
    });

    try {
      const pickerWindow = window as SaveFilePickerWindow;
      if (window.isSecureContext && typeof pickerWindow.showSaveFilePicker === "function") {
        const extension = fileName.toLowerCase().endsWith(".apk") ? ".apk" : ".exe";
        const handle = await pickerWindow.showSaveFilePicker({
          suggestedName: fileName,
          types: [{
            description: extension === ".apk" ? "Android application package" : "Windows installer",
            accept: { "application/octet-stream": [extension] }
          }]
        });
        writable = await handle.createWritable();
      }

      const response = await fetch(url, {
        method: "GET",
        headers: { Accept: "application/octet-stream" },
        cache: "no-store",
        credentials: "same-origin",
        signal: controller.signal
      });
      if (!response.ok) {
        if (response.status === 404) {
          throw new Error("This release file is no longer available. Refresh the page and try again.");
        }
        throw new Error(`The download server returned HTTP ${response.status}. Please retry.`);
      }

      const contentLength = Number(response.headers.get("content-length"));
      const totalBytes = Number.isFinite(contentLength) && contentLength > 0 ? contentLength : null;
      let bytesReceived = 0;
      const chunks: BlobPart[] = [];
      const reader = response.body?.getReader();

      if (reader) {
        while (true) {
          const result = await reader.read();
          if (result.done) break;
          const chunk = result.value;
          bytesReceived += chunk.byteLength;

          if (writable) {
            await writable.write(chunk);
          } else {
            chunks.push(chunk.slice().buffer as ArrayBuffer);
          }

          setDownload({
            status: "downloading",
            bytesReceived,
            totalBytes,
            errorMessage: null
          });
        }
      } else {
        const blob = await response.blob();
        bytesReceived = blob.size;
        if (writable) {
          const buffer = await blob.arrayBuffer();
          await writable.write(new Uint8Array(buffer));
        } else {
          chunks.push(await blob.arrayBuffer());
        }
        setDownload({
          status: "downloading",
          bytesReceived,
          totalBytes: totalBytes ?? bytesReceived,
          errorMessage: null
        });
      }

      if (totalBytes !== null && bytesReceived !== totalBytes) {
        throw new Error(`The download was incomplete (${bytesReceived} of ${totalBytes} bytes received). Please retry.`);
      }

      if (writable) {
        await writable.close();
        writable = null;
      } else {
        const blob = new Blob(chunks, { type: response.headers.get("content-type") || "application/octet-stream" });
        if (blob.size !== bytesReceived) {
          throw new Error("The downloaded file could not be assembled correctly. Please retry.");
        }
        saveBlob(blob, fileName);
      }

      completed = true;
      setDownload({
        status: "complete",
        bytesReceived,
        totalBytes: totalBytes ?? bytesReceived,
        errorMessage: null
      });
    } catch (error) {
      if (writable) {
        try { await writable.abort(); } catch { /* Ignore cleanup errors after an interrupted download. */ }
        writable = null;
      }
      if (isAbortError(error) || controller.signal.aborted) {
        setDownload(current => ({
          ...current,
          status: "cancelled",
          errorMessage: null
        }));
      } else {
        setDownload(current => ({
          ...current,
          status: "error",
          errorMessage: getDownloadError(error)
        }));
      }
    } finally {
      if (controllerRef.current === controller) controllerRef.current = null;
      if (!completed) {
        // The partial file is discarded on cancellation or error.
      }
    }
  }, [fileName]);

  const cancel = useCallback(() => {
    controllerRef.current?.abort();
  }, []);

  useEffect(() => () => controllerRef.current?.abort(), []);

  return { download, start, cancel };
}
