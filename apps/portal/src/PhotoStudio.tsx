import { useEffect, useMemo, useRef, useState } from "react";
import {
  Check, ChevronDown, Crop, Image as ImageIcon, RotateCcw, SlidersHorizontal,
  Sparkles, X, ZoomIn, ZoomOut
} from "lucide-react";
import styles from "./PhotoStudio.module.css";

export type PreparedPhotoSheet = { file: File; sizeCode: string; photoCount: number };

type PhotoSize = {
  id: string;
  label: string;
  detail: string;
  widthMm: number;
  heightMm: number;
};

type LookPreset = "natural" | "warm" | "crisp" | "mono";
type Tool = "crop" | "look" | "sheet";

type Adjustments = {
  brightness: number;
  contrast: number;
  saturation: number;
};

const A4_WIDTH_MM = 210;
const A4_HEIGHT_MM = 297;
const A4_WIDTH_PX = 2480;
const A4_HEIGHT_PX = 3508;

function formatPhotoRate(minor: number) {
  return new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', maximumFractionDigits: 2 }).format(minor / 100);
}
const MAX_FILE_BYTES = 20 * 1024 * 1024;

const PHOTO_SIZES: PhotoSize[] = [
  { id: "35x45", label: "35 × 45 mm", detail: "Portrait ID photo", widthMm: 35, heightMm: 45 },
  { id: "25x35", label: "25 × 35 mm", detail: "Compact ID photo", widthMm: 25, heightMm: 35 },
  { id: "2x2", label: "2 × 2 inch", detail: "Square photo", widthMm: 50.8, heightMm: 50.8 },
  { id: "4x6", label: "4 × 6 inch", detail: "Standard photo print", widthMm: 101.6, heightMm: 152.4 }
];

const LOOKS: Array<{ id: LookPreset; label: string; note: string; adjustments: Adjustments }> = [
  { id: "natural", label: "Natural", note: "Balanced, true-to-life colour", adjustments: { brightness: 100, contrast: 100, saturation: 100 } },
  { id: "warm", label: "Warm", note: "A gentle warmer tone", adjustments: { brightness: 102, contrast: 102, saturation: 108 } },
  { id: "crisp", label: "Crisp", note: "Slightly clearer and brighter", adjustments: { brightness: 104, contrast: 112, saturation: 104 } },
  { id: "mono", label: "Black & white", note: "Monochrome photo", adjustments: { brightness: 102, contrast: 108, saturation: 0 } }
];

function getLayout(size: PhotoSize) {
  const marginMm = 8;
  const gapMm = 3;
  const columns = Math.max(1, Math.floor((A4_WIDTH_MM - marginMm * 2 + gapMm) / (size.widthMm + gapMm)));
  const rows = Math.max(1, Math.floor((A4_HEIGHT_MM - marginMm * 2 + gapMm) / (size.heightMm + gapMm)));
  return { marginMm, gapMm, columns, rows, perSheet: columns * rows };
}

function cropSource(image: HTMLImageElement, size: PhotoSize, zoom: number, focusX: number, focusY: number) {
  const targetRatio = size.widthMm / size.heightMm;
  let width = image.naturalWidth;
  let height = image.naturalHeight;
  if (width / height > targetRatio) {
    width = height * targetRatio;
  } else {
    height = width / targetRatio;
  }
  width /= zoom;
  height /= zoom;
  const x = Math.max(0, (image.naturalWidth - width) * (focusX / 100));
  const y = Math.max(0, (image.naturalHeight - height) * (focusY / 100));
  return { x, y, width, height };
}

function drawPhotoSheetPage(
  context: CanvasRenderingContext2D,
  image: HTMLImageElement,
  canvasWidth: number,
  size: PhotoSize,
  copies: number,
  pageIndex: number,
  zoom: number,
  focusX: number,
  focusY: number,
  adjustments: Adjustments
) {
  const scale = canvasWidth / A4_WIDTH_MM;
  const layout = getLayout(size);
  const pageStart = pageIndex * layout.perSheet;
  const pageCopies = Math.max(0, Math.min(layout.perSheet, copies - pageStart));
  context.save();
  context.setTransform(1, 0, 0, 1, 0, 0);
  context.fillStyle = "#ffffff";
  context.fillRect(0, 0, canvasWidth, context.canvas.height);
  context.scale(scale, scale);

  const source = cropSource(image, size, zoom, focusX, focusY);
  const filter = "brightness(" + adjustments.brightness + "%) contrast(" + adjustments.contrast + "%) saturate(" + adjustments.saturation + "%)";
  for (let index = 0; index < pageCopies; index += 1) {
    const column = index % layout.columns;
    const row = Math.floor(index / layout.columns);
    const x = layout.marginMm + column * (size.widthMm + layout.gapMm);
    const y = layout.marginMm + row * (size.heightMm + layout.gapMm);
    context.save();
    context.beginPath();
    context.rect(x, y, size.widthMm, size.heightMm);
    context.clip();
    context.filter = filter;
    context.drawImage(
      image,
      source.x, source.y, source.width, source.height,
      x, y, size.widthMm, size.heightMm
    );
    context.restore();

    context.save();
    context.strokeStyle = "#8a929e";
    context.lineWidth = 0.18;
    context.setLineDash([0.8, 0.8]);
    context.strokeRect(x, y, size.widthMm, size.heightMm);
    context.restore();
  }
  context.restore();
}

function canvasToBlob(canvas: HTMLCanvasElement, type: string, quality?: number) {
  return new Promise<Blob>((resolve, reject) => {
    canvas.toBlob(blob => blob ? resolve(blob) : reject(new Error("The photo sheet could not be rendered.")), type, quality);
  });
}

function safeBaseName(fileName: string) {
  const withoutExtension = fileName.replace(/\.[^.]+$/, "");
  return withoutExtension.replace(/[^a-zA-Z0-9_-]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 48) || "photo";
}

function sourceResolutionNote(image: HTMLImageElement, size: PhotoSize) {
  const requiredWidth = Math.round(size.widthMm / 25.4 * 300);
  const requiredHeight = Math.round(size.heightMm / 25.4 * 300);
  const targetAspect = size.widthMm / size.heightMm;
  const sourceAspect = image.naturalWidth / image.naturalHeight;
  const availableShortSide = sourceAspect > targetAspect ? image.naturalHeight : image.naturalWidth;
  const targetShortSide = Math.min(requiredWidth, requiredHeight);
  return availableShortSide >= targetShortSide
    ? "Source resolution is suitable for this print size."
    : "This photo may look soft at the selected size. A sharper original is recommended.";
}

export default function PhotoStudio({
  source,
  onCancel,
  onApply,
  photoPrices
}: {
  source: File;
  onCancel: () => void;
  photoPrices: Record<string, number>;
  onApply: (sheets: PreparedPhotoSheet[]) => void;
}) {
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const [image, setImage] = useState<HTMLImageElement | null>(null);
  const [imageError, setImageError] = useState("");
  const [activeTool, setActiveTool] = useState<Tool>("sheet");
  const [sizeId, setSizeId] = useState("35x45");
  const [look, setLook] = useState<LookPreset>("natural");
  const [adjustments, setAdjustments] = useState<Adjustments>(LOOKS[0].adjustments);
  const [copiesInput, setCopiesInput] = useState("8");
  const [zoom, setZoom] = useState(1);
  const [focusX, setFocusX] = useState(50);
  const [focusY, setFocusY] = useState(50);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  const availableSizes = PHOTO_SIZES.filter(item => (photoPrices[item.id] ?? 0) > 0);
  const size = availableSizes.find(item => item.id === sizeId) ?? availableSizes[0] ?? PHOTO_SIZES[0];
  const layout = useMemo(() => getLayout(size), [size]);
  const maxCopies = Math.min(100, layout.perSheet * 20);
  const copies = Math.max(1, Math.min(maxCopies, Number(copiesInput) || 1));
  const sheetCount = Math.ceil(copies / layout.perSheet);
  const previewCapacity = layout.perSheet;

  useEffect(() => {
    if (availableSizes.length && !availableSizes.some(item => item.id === sizeId)) setSizeId(availableSizes[0].id);
  }, [availableSizes, sizeId]);

  useEffect(() => {
    const url = URL.createObjectURL(source);
    const nextImage = new Image();
    let active = true;
    nextImage.onload = () => {
      if (active) {
        setImage(nextImage);
        setImageError("");
      }
    };
    nextImage.onerror = () => {
      if (active) {
        setImage(null);
        setImageError("This image could not be opened. Choose a JPG or PNG photo.");
      }
    };
    nextImage.src = url;
    return () => {
      active = false;
      URL.revokeObjectURL(url);
    };
  }, [source]);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || !image) return;
    const context = canvas.getContext("2d");
    if (!context) {
      setImageError("Photo preview is not available in this browser.");
      return;
    }
    drawPhotoSheetPage(
      context, image, canvas.width, size,
      previewCapacity, 0, zoom, focusX, focusY, adjustments
    );
  }, [image, size, previewCapacity, zoom, focusX, focusY, adjustments]);

  function selectLook(nextLook: LookPreset) {
    const preset = LOOKS.find(item => item.id === nextLook) ?? LOOKS[0];
    setLook(nextLook);
    setAdjustments(preset.adjustments);
  }

  async function createPrintSheets() {
    if (!image || busy) return;
    const parsedCopies = Number(copiesInput);
    if (!Number.isInteger(parsedCopies) || parsedCopies < 1 || parsedCopies > maxCopies) {
      setError("Choose between 1 and " + maxCopies + " photo copies so the result stays within the 20-sheet batch limit.");
      return;
    }
    setBusy(true);
    setError("");
    try {
      const numberOfSheets = Math.ceil(parsedCopies / layout.perSheet);
      const outputs: PreparedPhotoSheet[] = [];
      for (let page = 0; page < numberOfSheets; page += 1) {
        const pageCopies = Math.min(layout.perSheet, parsedCopies - page * layout.perSheet);
        const canvas = document.createElement("canvas");
        canvas.width = A4_WIDTH_PX;
        canvas.height = A4_HEIGHT_PX;
        const context = canvas.getContext("2d");
        if (!context) throw new Error("A print canvas could not be created.");
        drawPhotoSheetPage(
          context, image, canvas.width, size, parsedCopies,
          page, zoom, focusX, focusY, adjustments
        );
        let blob = await canvasToBlob(canvas, "image/png");
        let extension = "png";
        let mediaType = "image/png";
        if (blob.size > MAX_FILE_BYTES) {
          blob = await canvasToBlob(canvas, "image/jpeg", 0.94);
          extension = "jpg";
          mediaType = "image/jpeg";
        }
        if (blob.size > MAX_FILE_BYTES) {
          throw new Error("The print-ready sheet is larger than SmartPrint's 20 MB file limit. Use a smaller photo or a lower-resolution original.");
        }
        const name = safeBaseName(source.name) + "-" + size.id + "-sheet-" + String(page + 1).padStart(2, "0") + "." + extension;
        outputs.push({ file: new File([blob], name, { type: mediaType, lastModified: Date.now() }), sizeCode: size.id, photoCount: pageCopies });
      }
      onApply(outputs);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Photo sheet creation failed. Try again with this photo.");
    } finally {
      setBusy(false);
    }
  }

  const resolutionMessage = image ? sourceResolutionNote(image, size) : "";
  const cropPanel = activeTool === "crop";
  const lookPanel = activeTool === "look";

  return (
    <div className={styles.overlay} role="dialog" aria-modal="true" aria-label="SmartPrint Photo Studio">
      <header className={styles.topbar}>
        <div className={styles.brand}>
          <span className={styles.brandMark}><ImageIcon size={17}/></span>
          <span><strong>Photo Studio</strong><small>SMARTPRINT · PRINT PREPARATION</small></span>
        </div>
        <div className={styles.topbarMeta}><span>A4 print sheet</span><span>300 DPI</span></div>
        <button type="button" className={styles.iconButton} onClick={onCancel} aria-label="Close Photo Studio" title="Close"><X size={18}/></button>
      </header>

      <div className={styles.editor}>
        <nav className={styles.toolRail} aria-label="Photo editing tools">
          <button type="button" className={activeTool === "sheet" ? styles.toolActive : ""} onClick={() => setActiveTool("sheet")} aria-pressed={activeTool === "sheet"}><ImageIcon size={17}/><span>Sheet</span></button>
          <button type="button" className={activeTool === "crop" ? styles.toolActive : ""} onClick={() => setActiveTool("crop")} aria-pressed={activeTool === "crop"}><Crop size={17}/><span>Crop</span></button>
          <button type="button" className={activeTool === "look" ? styles.toolActive : ""} onClick={() => setActiveTool("look")} aria-pressed={activeTool === "look"}><Sparkles size={17}/><span>Looks</span></button>
        </nav>

        <aside className={styles.toolPanel}>
          <div className={styles.panelHeading}>
            <div><strong>{cropPanel ? "Crop & position" : lookPanel ? "Photo look" : "Photo sheet"}</strong><small>{cropPanel ? "Frame the photo without changing facial features." : lookPanel ? "Use subtle, print-friendly adjustments." : "Choose the photo size and how many copies you need."}</small></div>
          </div>

          {cropPanel && <>
            <label className={styles.rangeControl}>Zoom <output>{zoom.toFixed(1)}×</output><input type="range" min="1" max="3" step="0.05" value={zoom} onChange={event => setZoom(Number(event.target.value))}/></label>
            <label className={styles.rangeControl}>Horizontal position <output>{focusX}%</output><input type="range" min="0" max="100" value={focusX} onChange={event => setFocusX(Number(event.target.value))}/></label>
            <label className={styles.rangeControl}>Vertical position <output>{focusY}%</output><input type="range" min="0" max="100" value={focusY} onChange={event => setFocusY(Number(event.target.value))}/></label>
            <button type="button" className={styles.secondaryButton} onClick={() => { setZoom(1); setFocusX(50); setFocusY(50); }}><RotateCcw size={14}/> Reset crop</button>
          </>}

          {lookPanel && <>
            <div className={styles.lookGrid}>
              {LOOKS.map(item => <button type="button" key={item.id} className={look === item.id ? styles.lookActive : styles.lookButton} onClick={() => selectLook(item.id)}>
                <span className={styles.lookSwatch + " " + styles["look_" + item.id]}/><strong>{item.label}</strong><small>{item.note}</small>
              </button>)}
            </div>
            <label className={styles.rangeControl}>Brightness <output>{adjustments.brightness}%</output><input type="range" min="70" max="130" value={adjustments.brightness} onChange={event => { setLook("natural"); setAdjustments(current => ({ ...current, brightness: Number(event.target.value) })); }}/></label>
            <label className={styles.rangeControl}>Contrast <output>{adjustments.contrast}%</output><input type="range" min="70" max="140" value={adjustments.contrast} onChange={event => { setLook("natural"); setAdjustments(current => ({ ...current, contrast: Number(event.target.value) })); }}/></label>
            <label className={styles.rangeControl}>Colour <output>{adjustments.saturation}%</output><input type="range" min="0" max="160" value={adjustments.saturation} onChange={event => { setLook("natural"); setAdjustments(current => ({ ...current, saturation: Number(event.target.value) })); }}/></label>
          </>}

          {activeTool === "sheet" && <>
            <label className={styles.fieldLabel}>Photo size
              <select value={sizeId} onChange={event => setSizeId(event.target.value)}>
                {availableSizes.map(item => <option key={item.id} value={item.id}>{item.label} · {item.detail} · {formatPhotoRate(photoPrices[item.id])}/photo</option>)}
              </select>
            </label>
            <label className={styles.fieldLabel}>Number of photos
              <input type="number" min="1" max={maxCopies} step="1" value={copiesInput} onChange={event => setCopiesInput(event.target.value)} onBlur={() => setCopiesInput(String(copies))}/>
            </label>
            <div className={styles.sheetSummary}>
              <div><span>Photos per A4 sheet</span><strong>{layout.perSheet}</strong></div>
              <div><span>Print-ready sheets</span><strong>{sheetCount}</strong></div>
              <small>Shop rate: {formatPhotoRate(photoPrices[size.id] ?? 0)} per photo. The server confirms the final total in your quote.</small>
            </div>
          </>}

          <div className={styles.privacyNote}><SlidersHorizontal size={14}/><span>Your original photo is not changed. ID-safe tools adjust framing and print appearance only; they do not reshape or retouch facial features.</span></div>
        </aside>

        <main className={styles.canvasWorkspace}>
          <div className={styles.contextToolbar}>
            <span>PRINT SHEET PREVIEW</span>
            <span className={styles.toolbarDivider}/>
            <button type="button" onClick={() => setZoom(value => Math.max(1, Number((value - 0.1).toFixed(2))))} disabled={zoom <= 1} aria-label="Zoom out"><ZoomOut size={15}/></button>
            <span className={styles.zoomLabel}>{zoom.toFixed(1)}×</span>
            <button type="button" onClick={() => setZoom(value => Math.min(3, Number((value + 0.1).toFixed(2))))} disabled={zoom >= 3} aria-label="Zoom in"><ZoomIn size={15}/></button>
            <span className={styles.toolbarDivider}/>
            <button type="button" onClick={() => { setZoom(1); setFocusX(50); setFocusY(50); selectLook("natural"); }}><RotateCcw size={14}/> Reset</button>
          </div>
          <div className={styles.canvasViewport}>
            <div className={styles.sheetFrame}>
              {image ? <canvas ref={canvasRef} width={420} height={594} className={styles.previewCanvas} role="img" aria-label={"A4 photo sheet preview with " + Math.min(copies, previewCapacity) + " photos"}/> : <div className={styles.loadingState}>{imageError || "Preparing your photo…"}</div>}
            </div>
          </div>
          <div className={styles.canvasFooter}>
            <span>{size.label} · {layout.perSheet} per sheet</span>
            <span>{copies} photo copies · {sheetCount} A4 sheet(s)</span>
            {resolutionMessage && <small>{resolutionMessage}</small>}
          </div>
        </main>
      </div>

      <footer className={styles.footer}>
        <div className={styles.footerHint}><Check size={14}/> Original photo remains unchanged until you replace it with the prepared sheets.</div>
        <div className={styles.footerActions}>
          <button type="button" className={styles.cancelButton} onClick={onCancel} disabled={busy}>Cancel</button>
          <button type="button" className={styles.applyButton} onClick={() => void createPrintSheets()} disabled={!image || busy || !!imageError || availableSizes.length === 0 || !Number.isInteger(Number(copiesInput)) || Number(copiesInput) < 1 || Number(copiesInput) > maxCopies}>
            {busy ? "Preparing sheets…" : "Use photo sheets"} <ChevronDown size={15}/>
          </button>
        </div>
      </footer>
      {error && <div className={styles.errorToast} role="alert">{error}</div>}
    </div>
  );
}
