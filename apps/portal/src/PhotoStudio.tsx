import { useEffect, useMemo, useRef, useState, type MouseEvent } from "react";
import {
  Check, Crop, Image as ImageIcon, RotateCcw, SlidersHorizontal,
  Sparkles, X, Type, PaintBucket, Pipette
} from "lucide-react";
import styles from "./PhotoStudio.module.css";
import PhotoStudioCanvasWorkspace from "./PhotoStudioCanvasWorkspace";
import { isDesktopApp, setDesktopContentProtection } from "./desktop-bridge";
import { A4_HEIGHT_MM, A4_WIDTH_MM, A4_HEIGHT_PX, A4_WIDTH_PX, buildPhotoPdf, getPhotoSheetLayout } from "./photo-studio-layout";
import {
  MAX_FILE_BYTES, MAX_PHOTO_COUNT, PHOTO_SIZES, LOOKS,
  formatPhotoRate, drawPhotoSheetPage, cropSource, canvasToBlob, safeBaseName, sourceResolutionNote,
  type PhotoSize, type LookPreset, type Tool, type Adjustments
} from "./photo-studio-rendering";

export type PreparedPhotoSheet = { file: File; sizeCode: string; photoCount: number; photosPerPage: number; previewPages?: Blob[] };

export default function PhotoStudio({
  source,
  onCancel,
  onApply,
  photoPrices,
  mode = "customer",
  dark = false
}: {
  source: File;
  onCancel: () => void;
  photoPrices: Record<string, number>;
  onApply: (sheets: PreparedPhotoSheet[]) => void | Promise<void>;
  mode?: "customer" | "shop";
  dark?: boolean;
}) {
  const dialogRef = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const [image, setImage] = useState<HTMLImageElement | null>(null);
  const [imageError, setImageError] = useState("");
  const [activeTool, setActiveTool] = useState<Tool>("sheet");
  const [sizeId, setSizeId] = useState("35x45");
  const [look, setLook] = useState<LookPreset>("natural");
  const [adjustments, setAdjustments] = useState<Adjustments>(LOOKS[0].adjustments);
  const [copiesInput, setCopiesInput] = useState("8");
  const [photosPerPageInput,setPhotosPerPageInput]=useState("auto");
  const [previewPage,setPreviewPage]=useState(0);
  const [zoom, setZoom] = useState(1);
  const [focusX, setFocusX] = useState(50);
  const [focusY, setFocusY] = useState(50);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const busyRef = useRef(busy);
  const onCancelRef = useRef(onCancel);
  busyRef.current = busy;
  onCancelRef.current = onCancel;
  const [includeFormDetails,setIncludeFormDetails]=useState(false);
  const [photoName,setPhotoName]=useState("");
  const [dateOfBirth,setDateOfBirth]=useState("");
  const [captionFontSizePt,setCaptionFontSizePt]=useState(6);
  const [captionColor,setCaptionColor]=useState("#202020");
  const [targetBackgroundColor,setTargetBackgroundColor]=useState("#ffffff");
  const [sourceBackgroundColor,setSourceBackgroundColor]=useState<string|null>(null);
  const [backgroundTolerance,setBackgroundTolerance]=useState(30);
  const [pickingBackground,setPickingBackground]=useState(false);

  const availableSizes = PHOTO_SIZES.filter(item => (photoPrices[item.id] ?? 0) > 0);
  const size = availableSizes.find(item => item.id === sizeId) ?? availableSizes[0] ?? PHOTO_SIZES[0];
  const hasCaption=includeFormDetails&&Boolean(photoName.trim()||dateOfBirth.trim());
  const captionFontMm=captionFontSizePt*25.4/72;
  const captionHeightMm=hasCaption?Math.max(7,captionFontMm*2+1.2):0;
  const maximumLayout=useMemo(()=>getPhotoSheetLayout(size,captionHeightMm),[size,captionHeightMm]);
  const requestedPhotosPerPage=photosPerPageInput==="auto"?maximumLayout.perSheet:Number(photosPerPageInput);
  const photosPerPage=Number.isInteger(requestedPhotosPerPage)&&requestedPhotosPerPage>=1&&requestedPhotosPerPage<=maximumLayout.perSheet
    ? requestedPhotosPerPage : maximumLayout.perSheet;
  const layout=useMemo(()=>getPhotoSheetLayout(size,captionHeightMm,photosPerPage),[size,captionHeightMm,photosPerPage]);
  const maxCopies=MAX_PHOTO_COUNT;
  const copies = Math.max(1, Math.min(maxCopies, Number(copiesInput) || 1));
  const sheetCount = Math.ceil(copies / photosPerPage);
  const previewPageIndex=Math.max(0,Math.min(previewPage,sheetCount-1));
  const previewPhotoCount=Math.min(layout.perSheet,copies-previewPageIndex*layout.perSheet);
  useEffect(()=>{setPreviewPage(current=>Math.min(current,sheetCount-1));},[sheetCount]);
  useEffect(()=>{
    if(photosPerPageInput!=="auto"&&(!Number.isInteger(Number(photosPerPageInput))||Number(photosPerPageInput)>maximumLayout.perSheet))
      setPhotosPerPageInput("auto");
  },[photosPerPageInput,maximumLayout.perSheet]);

  useEffect(() => {
    const previousOverflow = document.body.style.overflow;
    const previouslyFocused = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    document.body.style.overflow = "hidden";
    const focusableSelector = 'button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';
    const focusableElements = () => Array.from(dialogRef.current?.querySelectorAll<HTMLElement>(focusableSelector) ?? []);
    const handleDialogKeydown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        if (!busyRef.current) {
          event.preventDefault();
          onCancelRef.current();
        }
        return;
      }
      if (event.key !== "Tab") return;
      const elements = focusableElements();
      if (!elements.length) {
        event.preventDefault();
        dialogRef.current?.focus();
        return;
      }
      const first = elements[0];
      const last = elements[elements.length - 1];
      if (event.shiftKey && (document.activeElement === first || !dialogRef.current?.contains(document.activeElement))) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && (document.activeElement === last || !dialogRef.current?.contains(document.activeElement))) {
        event.preventDefault();
        first.focus();
      }
    };
    document.addEventListener("keydown", handleDialogKeydown, true);
    focusableElements()[0]?.focus();
    return () => {
      document.removeEventListener("keydown", handleDialogKeydown, true);
      document.body.style.overflow = previousOverflow;
      previouslyFocused?.focus();
    };
  }, []);

  useEffect(() => {
    if (availableSizes.length && !availableSizes.some(item => item.id === sizeId)) setSizeId(availableSizes[0].id);
  }, [availableSizes, sizeId]);

  useEffect(()=>{
    if(!isDesktopApp())return;
    void setDesktopContentProtection(true);
    return ()=>{void setDesktopContentProtection(false);};
  },[]);

  useEffect(()=>{
    const bridge=(window as Window & {SmartPrintNative?:{postMessage:(value:string)=>void}}).SmartPrintNative;
    try{bridge?.postMessage("setSensitivePreview|on");}catch{/* Native screenshot protection is best-effort outside supported desktop builds. */}
    return ()=>{
      try{bridge?.postMessage("setSensitivePreview|off");}catch{/* The WebView may already be closing. */}
    };
  },[]);

  useEffect(()=>{
    const protectContextMenu=(event:globalThis.MouseEvent)=>event.preventDefault();
    const protectShortcuts=(event:KeyboardEvent)=>{
      const key=event.key.toLowerCase();
      if(key==="printscreen"||((event.ctrlKey||event.metaKey)&&["s","p"].includes(key)))event.preventDefault();
    };
    document.addEventListener("contextmenu",protectContextMenu,true);
    window.addEventListener("keydown",protectShortcuts,true);
    return ()=>{
      document.removeEventListener("contextmenu",protectContextMenu,true);
      window.removeEventListener("keydown",protectShortcuts,true);
    };
  },[]);

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
      context,image,canvas.width,size,copies,photosPerPage,previewPageIndex,zoom,focusX,focusY,adjustments,
      {name:photoName,dob:dateOfBirth,fontSizePt:captionFontSizePt,color:captionColor},
      {sourceColor:sourceBackgroundColor,targetColor:targetBackgroundColor,tolerance:backgroundTolerance},captionHeightMm
    );
  },[image,size,copies,photosPerPage,previewPageIndex,zoom,focusX,focusY,adjustments,photoName,dateOfBirth,captionFontSizePt,captionColor,sourceBackgroundColor,targetBackgroundColor,backgroundTolerance,captionHeightMm]);

  function handleBackgroundPick(event:MouseEvent<HTMLCanvasElement>){
    if(!pickingBackground||!image)return;
    const canvas=event.currentTarget,bounds=canvas.getBoundingClientRect();
    if(!bounds.width||!bounds.height)return;
    const px=(event.clientX-bounds.left)/bounds.width*canvas.width,py=(event.clientY-bounds.top)/bounds.height*canvas.height;
    const xMm=px/(canvas.width/A4_WIDTH_MM),yMm=py/(canvas.height/A4_HEIGHT_MM);
    const tileH=size.heightMm+captionHeightMm;
    const visibleRows=Math.max(1,Math.ceil(previewPhotoCount/layout.columns));
    const visibleContentHeightMm=visibleRows*tileH+(visibleRows-1)*3;
    const pageOffsetY=Math.max(8,(A4_HEIGHT_MM-visibleContentHeightMm)/2);
    const row=Math.floor((yMm-pageOffsetY)/(tileH+3));
    if(row<0||row>=visibleRows)return;
    const rowStart=row*layout.columns,rowCount=Math.min(layout.columns,previewPhotoCount-rowStart);
    const rowWidth=rowCount*size.widthMm+(rowCount-1)*3;
    const rowOffset=(layout.contentWidthMm-rowWidth)/2;
    const col=Math.floor((xMm-layout.offsetXmm-rowOffset)/(size.widthMm+3));
    if(col<0||col>=rowCount)return;
    const localX=xMm-layout.offsetXmm-rowOffset-col*(size.widthMm+3),localY=yMm-pageOffsetY-row*(tileH+3);
    if(localX<0||localX>size.widthMm||localY<0||localY>size.heightMm)return;
    const crop=cropSource(image,size,zoom,focusX,focusY);
    const sx=Math.max(0,Math.min(image.naturalWidth-1,Math.floor(crop.x+localX/size.widthMm*crop.width)));
    const sy=Math.max(0,Math.min(image.naturalHeight-1,Math.floor(crop.y+localY/size.heightMm*crop.height)));
    const sampler=document.createElement("canvas");sampler.width=1;sampler.height=1;
    const ctx=sampler.getContext("2d",{willReadFrequently:true});if(!ctx){setError("The background colour could not be sampled.");return;}
    ctx.filter=`brightness(${adjustments.brightness}%) contrast(${adjustments.contrast}%) saturate(${adjustments.saturation}%)`;
    ctx.drawImage(image,sx,sy,1,1,0,0,1,1);
    ctx.filter="none";
    const [r,g,b]=ctx.getImageData(0,0,1,1).data;
    setSourceBackgroundColor("#"+[r,g,b].map(v=>v.toString(16).padStart(2,"0")).join(""));
    setPickingBackground(false);
  }

  function selectLook(nextLook: LookPreset) {
    const preset = LOOKS.find(item => item.id === nextLook) ?? LOOKS[0];
    setLook(nextLook);
    setAdjustments(preset.adjustments);
  }

  async function createPrintSheets(){
    if(!image||busy)return;
    const parsedCopies=Number(copiesInput);
    if(!Number.isInteger(parsedCopies)||parsedCopies<1||parsedCopies>maxCopies){setError("Choose between 1 and "+maxCopies+" photos. The A4 page count is calculated automatically.");return;}
    setBusy(true);setError("");
    try{
      const pages=Math.ceil(parsedCopies/photosPerPage),jpegPages:Uint8Array[]=[];
      let jpegByteLength=0;
      const pdfStructureAllowance=4096+pages*1024;
      for(let page=0;page<pages;page++){
        const canvas=document.createElement("canvas");canvas.width=A4_WIDTH_PX;canvas.height=A4_HEIGHT_PX;
        const context=canvas.getContext("2d");if(!context)throw new Error("A print canvas could not be created.");
        drawPhotoSheetPage(context,image,canvas.width,size,parsedCopies,photosPerPage,page,zoom,focusX,focusY,adjustments,
          {name:photoName,dob:dateOfBirth,fontSizePt:captionFontSizePt,color:captionColor},
          {sourceColor:sourceBackgroundColor,targetColor:targetBackgroundColor,tolerance:backgroundTolerance},captionHeightMm);
        let blob=await canvasToBlob(canvas,"image/jpeg",.9);
        if(blob.size>MAX_FILE_BYTES)blob=await canvasToBlob(canvas,"image/jpeg",.82);
        jpegByteLength+=blob.size;
        if(jpegByteLength+pdfStructureAllowance>MAX_FILE_BYTES)
          throw new Error("The prepared photo PDF would exceed SmartPrint's 20 MB file limit. Use a smaller original photo, choose fewer photos per job, or split the order into smaller batches.");
        jpegPages.push(new Uint8Array(await blob.arrayBuffer()));
      }
      const bytes=buildPhotoPdf(jpegPages);
      const buffer=bytes.buffer.slice(bytes.byteOffset,bytes.byteOffset+bytes.byteLength) as ArrayBuffer;
      const pdf=new Blob([buffer],{type:"application/pdf"});
      if(pdf.size>MAX_FILE_BYTES)throw new Error("The prepared photo PDF exceeds SmartPrint's 20 MB file limit. Use a smaller original photo or reduce the number of copies.");
      const output=new File([pdf],safeBaseName(source.name)+"-"+size.id+"-photos.pdf",{type:"application/pdf",lastModified:Date.now()});
      await onApply([{file:output,sizeCode:size.id,photoCount:parsedCopies,photosPerPage,previewPages:jpegPages.map(jpeg=>new Blob([jpeg],{type:"image/jpeg"}))}]);
    }catch(cause){setError(cause instanceof Error?cause.message:"Photo sheet creation failed. Try again with this photo.");}
    finally{setBusy(false);}
  }

  const resolutionMessage = image ? sourceResolutionNote(image, size) : "";
  const footerHint = mode === "shop"
    ? "Save your prepared multi-page A4 photo PDF to this existing unpaid job. It returns to the review queue; printing still requires shop approval, verified payment and dispatch."
    : "The original upload stays untouched. This prepared multi-page A4 photo PDF replaces it in your batch when you submit your print request.";
  const actionLabel = mode === "shop" ? "Save sheet to this job" : "Prepare A4 sheet";
  const busyLabel = mode === "shop" ? "Saving to the existing job…" : "Preparing your A4 sheet…";
  const cropPanel=activeTool==="crop",lookPanel=activeTool==="look",textPanel=activeTool==="text",backgroundPanel=activeTool==="background";

  return (
    <div ref={dialogRef} className={styles.overlay + (dark ? " " + styles.dark : "")} role="dialog" aria-modal="true" aria-labelledby="photo-studio-title" aria-busy={busy} tabIndex={-1}>
      <header className={styles.topbar}>
        <div className={styles.brand}>
          <span className={styles.brandMark}><ImageIcon size={17}/></span>
          <span><strong id="photo-studio-title">Photo Studio</strong><small>{mode === "shop" ? "SHOP · EDIT EXISTING JOB" : "CUSTOMER · BEFORE UPLOAD"}</small></span>
        </div>
        <div className={styles.topbarMeta}><span>A4 · 210 × 297 mm</span><span>300 DPI</span></div>
        <button type="button" className={styles.iconButton} onClick={onCancel} disabled={busy} aria-label="Close Photo Studio" title="Close"><X size={18}/></button>
      </header>

      <div className={styles.editor}>
        <nav className={styles.toolRail} aria-label="Photo editing tools">
          <button type="button" className={activeTool === "sheet" ? styles.toolActive : ""} onClick={() => setActiveTool("sheet")} aria-pressed={activeTool === "sheet"}><ImageIcon size={17}/><span>Sheet</span></button>
          <button type="button" className={activeTool === "crop" ? styles.toolActive : ""} onClick={() => setActiveTool("crop")} aria-pressed={activeTool === "crop"}><Crop size={17}/><span>Crop</span></button>
          <button type="button" className={activeTool === "look" ? styles.toolActive : ""} onClick={() => setActiveTool("look")} aria-pressed={activeTool === "look"}><Sparkles size={17}/><span>Looks</span></button>
          <button type="button" className={activeTool==="text"?styles.toolActive:""} onClick={()=>setActiveTool("text")} aria-pressed={activeTool==="text"} disabled={!includeFormDetails} title={includeFormDetails?"Style the optional form details":"Enable form details in Sheet to style printed text"}><Type size={17}/><span>Text</span></button>
          <button type="button" className={activeTool==="background"?styles.toolActive:""} onClick={()=>setActiveTool("background")} aria-pressed={activeTool==="background"}><PaintBucket size={17}/><span>Background</span></button>
        </nav>

        <aside className={styles.toolPanel}>
          <div className={styles.panelHeading}>
            <div><strong>{cropPanel?"Crop & position":lookPanel?"Photo look":textPanel?"Caption styling":backgroundPanel?"Photo background":"Photo sheet & form details"}</strong><small>{cropPanel?"Frame the photo without changing facial features.":lookPanel?"Use subtle, print-friendly adjustments.":textPanel?"Set how the form details appear under each photo.":backgroundPanel?"Sample a flat background from the photo and replace only the edge-connected area.":"Set the ID photo size, form details and print quantity."}</small></div>
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

          {textPanel && <>
            <label className={styles.fieldLabel}>Caption font size<select value={captionFontSizePt} onChange={event=>setCaptionFontSizePt(Number(event.target.value))}><option value={5}>5 pt · Compact</option><option value={6}>6 pt · Standard</option><option value={7}>7 pt · Large</option></select></label>
            <label className={styles.fieldLabel}>Caption colour<input type="color" value={captionColor} onChange={event=>setCaptionColor(event.target.value)}/></label>
            <div className={styles.editorHelp}>These settings style the name and DOB printed below each photo. The photo dimensions stay unchanged.</div>
          </>}

          {backgroundPanel && <>
            <div className={styles.backgroundPreviewRow}><span>Sampled background</span><strong>{sourceBackgroundColor?sourceBackgroundColor.toUpperCase():"Not selected"}</strong>{sourceBackgroundColor&&<i style={{background:sourceBackgroundColor}}/>}</div>
            <button type="button" className={styles.secondaryButton} onClick={()=>{setPickingBackground(true);setError("");}}><Pipette size={14}/>{pickingBackground?"Click the background in the preview…":"Pick background from photo"}</button>
            <label className={styles.fieldLabel}>Replacement background<input type="color" value={targetBackgroundColor} onChange={event=>setTargetBackgroundColor(event.target.value)}/></label>
            <div className={styles.backgroundPresets}>{["#ffffff","#f3f6fa","#dcecff","#dff3e7","#f6eadb"].map(color=><button key={color} type="button" style={{background:color}} aria-label={"Set background "+color} aria-pressed={targetBackgroundColor===color} onClick={()=>setTargetBackgroundColor(color)}/>)}</div>
            <label className={styles.rangeControl}>Background tolerance<output>{backgroundTolerance}</output><input type="range" min="5" max="90" value={backgroundTolerance} onChange={event=>setBackgroundTolerance(Number(event.target.value))}/></label>
            <div className={styles.editorHelp}>This replaces sampled-colour pixels connected to the photo edges. Use a plain, evenly lit background and inspect hair/shoulders. This is a colour-key tool, not AI subject segmentation.</div>
            <button type="button" className={styles.secondaryButton} onClick={()=>{setSourceBackgroundColor(null);setPickingBackground(false);setBackgroundTolerance(30);setTargetBackgroundColor("#ffffff");}}>Reset background</button>
          </>}

          {activeTool === "sheet" && <>
            <label className={styles.captionOptIn}>
              <input type="checkbox" checked={includeFormDetails} onChange={event=>{
                const enabled=event.target.checked;
                setIncludeFormDetails(enabled);
                if(!enabled){setPhotoName("");setDateOfBirth("");}
              }}/>
              <span><strong>Add name and DOB to each photo</strong><small>Optional. Nothing is printed below the photos unless you switch this on.</small></span>
            </label>
            {includeFormDetails&&<>
              <label className={styles.fieldLabel}>Name for form (optional)
                <input type="text" maxLength={48} value={photoName} onChange={event=>setPhotoName(event.target.value)} placeholder="Enter the name to print below each photo"/>
              </label>
              <label className={styles.fieldLabel}>Date of birth (optional)
                <input type="date" min="1900-01-01" max={new Date().toISOString().slice(0,10)} value={dateOfBirth} onChange={event=>setDateOfBirth(event.target.value)}/>
              </label>
              <div className={styles.editorHelp}>Only the details you enter are printed below each photo. The photo dimensions stay unchanged.</div>
            </>}
            <label className={styles.fieldLabel}>Photo size
              <select value={sizeId} onChange={event => setSizeId(event.target.value)}>
                {availableSizes.map(item => <option key={item.id} value={item.id}>{item.label} · {item.detail} · {formatPhotoRate(photoPrices[item.id])}/photo</option>)}
              </select>
            </label>
            <label className={styles.fieldLabel}>Number of photos
              <input type="number" min="1" max={maxCopies} step="1" inputMode="numeric" value={copiesInput} onChange={event => setCopiesInput(event.target.value)} onBlur={() => setCopiesInput(String(copies))}/>
            </label>
            <label className={styles.fieldLabel}>Photos per A4 page
              <select value={photosPerPageInput} onChange={event=>setPhotosPerPageInput(event.target.value)}>
                <option value="auto">Fit as many as possible ({maximumLayout.perSheet})</option>
                {Array.from({length:maximumLayout.perSheet},(_,index)=>index+1).map(count=><option key={count} value={String(count)}>{count} per page</option>)}
              </select>
            </label>
            <div className={styles.sheetSummary}>
              <div><span>Photo dimensions</span><strong>{size.widthMm} × {size.heightMm} mm</strong></div>
              <div><span>Photos per A4 page</span><strong>{layout.perSheet}</strong></div>
              <div><span>Grid alignment</span><strong>{layout.columns} × {layout.rows}</strong></div>
              <div><span>A4 pages in this job</span><strong>{sheetCount}</strong></div>
              <div><span>Preview page</span><strong>{previewPageIndex+1} of {sheetCount}</strong></div>
              {copies>photosPerPage&&copies%photosPerPage!==0&&<small>Final A4 page: {copies%photosPerPage} photos, with the remaining sheet area left blank.</small>}
              <small>Shop rate: {formatPhotoRate(photoPrices[size.id] ?? 0)} per photo. The server confirms the final total in your quote.</small>
            </div>
          </>}

          <div className={styles.privacyNote}><SlidersHorizontal size={14}/><span>Your original photo is not changed. ID-safe tools adjust framing and print appearance only; they do not reshape or retouch facial features.</span></div>
        </aside>

        <PhotoStudioCanvasWorkspace
          canvasRef={canvasRef}
          image={Boolean(image)}
          imageError={imageError}
          pickingBackground={pickingBackground}
          sheetCount={sheetCount}
          previewPageIndex={previewPageIndex}
          previewPhotoCount={previewPhotoCount}
          zoom={zoom}
          size={size}
          copies={copies}
          resolutionMessage={resolutionMessage}
          onPreviousPage={()=>setPreviewPage(current=>Math.max(0,current-1))}
          onNextPage={()=>setPreviewPage(current=>Math.min(sheetCount-1,current+1))}
          onZoomOut={()=>setZoom(value=>Math.max(1,Number((value-0.1).toFixed(2))))}
          onZoomIn={()=>setZoom(value=>Math.min(3,Number((value+0.1).toFixed(2))))}
          onReset={()=>{setZoom(1);setFocusX(50);setFocusY(50);selectLook("natural");}}
          onBackgroundPick={handleBackgroundPick}
        />
      </div>

      <footer className={styles.footer}>
        <div className={styles.footerHint}><Check size={14}/> {footerHint}</div>
        <div className={styles.footerActions}>
          <button type="button" className={styles.cancelButton} onClick={onCancel} disabled={busy}>Cancel</button>
          <button type="button" className={styles.applyButton} onClick={() => void createPrintSheets()} disabled={!image || busy || !!imageError || availableSizes.length === 0 || !Number.isInteger(Number(copiesInput)) || Number(copiesInput) < 1 || Number(copiesInput) > maxCopies}>
            {busy ? busyLabel : actionLabel} <Check size={15}/>
          </button>
        </div>
      </footer>
      {error && <div className={styles.errorToast} role="alert">{error}</div>}
    </div>
  );
}
