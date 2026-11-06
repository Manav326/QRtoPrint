import type { MouseEvent, RefObject } from "react";
import { ChevronLeft, ChevronRight, RotateCcw, ZoomIn, ZoomOut } from "lucide-react";
import type { PhotoSize } from "./photo-studio-rendering";
import styles from "./PhotoStudio.module.css";

type PhotoStudioCanvasWorkspaceProps = {
  canvasRef: RefObject<HTMLCanvasElement | null>;
  image: boolean;
  imageError: string;
  pickingBackground: boolean;
  sheetCount: number;
  previewPageIndex: number;
  previewPhotoCount: number;
  zoom: number;
  size: PhotoSize;
  copies: number;
  resolutionMessage: string;
  onPreviousPage: () => void;
  onNextPage: () => void;
  onZoomOut: () => void;
  onZoomIn: () => void;
  onReset: () => void;
  onBackgroundPick: (event: MouseEvent<HTMLCanvasElement>) => void;
};

export default function PhotoStudioCanvasWorkspace({
  canvasRef,image,imageError,pickingBackground,sheetCount,previewPageIndex,
  previewPhotoCount,zoom,size,copies,resolutionMessage,onPreviousPage,onNextPage,
  onZoomOut,onZoomIn,onReset,onBackgroundPick
}:PhotoStudioCanvasWorkspaceProps) {
  return <main className={styles.canvasWorkspace}>
    <div className={styles.contextToolbar}>
      <span>PRINT SHEET PREVIEW</span>
      {sheetCount>1&&<div className={styles.pageNavigator}>
        <button type="button" aria-label="Previous A4 page" onClick={onPreviousPage} disabled={previewPageIndex===0}><ChevronLeft size={14}/></button>
        <span>Page {previewPageIndex+1} of {sheetCount}</span>
        <button type="button" aria-label="Next A4 page" onClick={onNextPage} disabled={previewPageIndex>=sheetCount-1}><ChevronRight size={14}/></button>
      </div>}
      <span className={styles.toolbarDivider}/>
      <button type="button" onClick={onZoomOut} disabled={zoom<=1} aria-label="Zoom out"><ZoomOut size={15}/></button>
      <span className={styles.zoomLabel}>{zoom.toFixed(1)}×</span>
      <button type="button" onClick={onZoomIn} disabled={zoom>=3} aria-label="Zoom in"><ZoomIn size={15}/></button>
      <span className={styles.toolbarDivider}/>
      <button type="button" onClick={onReset}><RotateCcw size={14}/> Reset</button>
    </div>
    <div className={styles.canvasViewport}>
      {pickingBackground&&<div className={styles.pickInstruction}>Click a flat background area in the photo to sample its colour.</div>}
      <div className={styles.sheetFrame} onContextMenu={event=>event.preventDefault()} onDragStart={event=>event.preventDefault()}>
        {image
          ? <canvas ref={canvasRef} width={420} height={594} onClick={onBackgroundPick} style={{cursor:pickingBackground?"crosshair":"default"}} className={styles.previewCanvas} draggable={false} role="img" aria-label={"A4 page "+(previewPageIndex+1)+" of "+sheetCount+" with "+previewPhotoCount+" photos"}/>
          : <div className={styles.loadingState}>{imageError||"Preparing your photo…"}</div>}
        <div className={styles.previewProtection} aria-hidden="true"/>
      </div>
    </div>
    <div className={styles.canvasFooter}>
      <span>{size.label} · {size.widthMm} × {size.heightMm} mm</span>
      <span>{copies} photos · {sheetCount} A4 pages</span>
      <small>Page {previewPageIndex+1}: {previewPhotoCount} photos</small>
      {resolutionMessage&&<small>{resolutionMessage}</small>}
    </div>
  </main>;
}
