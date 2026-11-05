import { A4_HEIGHT_MM, A4_WIDTH_MM, getPhotoSheetLayout } from "./photo-studio-layout";

export type PhotoSize = {
  id: string;
  label: string;
  detail: string;
  widthMm: number;
  heightMm: number;
};

export type LookPreset = "natural" | "warm" | "crisp" | "mono";
export type Tool = "crop" | "look" | "sheet" | "text" | "background";

export type Adjustments = {
  brightness: number;
  contrast: number;
  saturation: number;
};

export function formatPhotoRate(minor: number) {
  return new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', maximumFractionDigits: 2 }).format(minor / 100);
}
export const MAX_FILE_BYTES = 20 * 1024 * 1024;
export const MAX_PHOTO_COUNT = 100;

export const PHOTO_SIZES: PhotoSize[] = [
  { id: "35x45", label: "35 × 45 mm", detail: "Portrait ID photo", widthMm: 35, heightMm: 45 },
  { id: "25x35", label: "25 × 35 mm", detail: "Compact ID photo", widthMm: 25, heightMm: 35 },
  { id: "2x2", label: "2 × 2 inch", detail: "Square photo", widthMm: 50.8, heightMm: 50.8 },
  { id: "4x6", label: "4 × 6 inch", detail: "Standard photo print", widthMm: 101.6, heightMm: 152.4 }
];

export const LOOKS: Array<{ id: LookPreset; label: string; note: string; adjustments: Adjustments }> = [
  { id: "natural", label: "Natural", note: "Balanced, true-to-life colour", adjustments: { brightness: 100, contrast: 100, saturation: 100 } },
  { id: "warm", label: "Warm", note: "A gentle warmer tone", adjustments: { brightness: 102, contrast: 102, saturation: 108 } },
  { id: "crisp", label: "Crisp", note: "Slightly clearer and brighter", adjustments: { brightness: 104, contrast: 112, saturation: 104 } },
  { id: "mono", label: "Black & white", note: "Monochrome photo", adjustments: { brightness: 102, contrast: 108, saturation: 0 } }
];

function parseHexColor(value: string): [number, number, number] | null {
  const match=/^#([0-9a-f]{6})$/i.exec(value);
  if(!match)return null;
  return [Number.parseInt(match[1].slice(0,2),16),Number.parseInt(match[1].slice(2,4),16),Number.parseInt(match[1].slice(4,6),16)];
}
export function replaceEdgeBackground(canvas: HTMLCanvasElement, sourceHex: string, targetHex: string, tolerance: number) {
  const source=parseHexColor(sourceHex),target=parseHexColor(targetHex);
  if(!source||!target||tolerance<1)return;
  const context=canvas.getContext("2d");if(!context)return;
  const {width,height}=canvas,pixels=context.getImageData(0,0,width,height),data=pixels.data;
  const visited=new Uint8Array(width*height),queue=new Int32Array(width*height);
  let head=0,tail=0;const limit=tolerance*tolerance;
  const candidate=(i:number)=>{const p=i*4,r=data[p]-source[0],g=data[p+1]-source[1],b=data[p+2]-source[2];return r*r+g*g+b*b<=limit;};
  const add=(i:number)=>{if(visited[i]||!candidate(i))return;visited[i]=1;queue[tail++]=i;};
  for(let x=0;x<width;x++){add(x);add((height-1)*width+x);}
  for(let y=1;y<height-1;y++){add(y*width);add(y*width+width-1);}
  while(head<tail){const i=queue[head++],x=i%width,y=Math.floor(i/width);if(x>0)add(i-1);if(x+1<width)add(i+1);if(y>0)add(i-width);if(y+1<height)add(i+width);}
  for(let n=0;n<tail;n++){
    const p=queue[n]*4,r=data[p]-source[0],g=data[p+1]-source[1],b=data[p+2]-source[2];
    const distance=Math.sqrt(r*r+g*g+b*b),blend=Math.max(0,Math.min(1,(tolerance-distance+5)/10));
    data[p]=Math.round(data[p]*(1-blend)+target[0]*blend);
    data[p+1]=Math.round(data[p+1]*(1-blend)+target[1]*blend);
    data[p+2]=Math.round(data[p+2]*(1-blend)+target[2]*blend);
  }
  context.putImageData(pixels,0,0);
}
export function formatDobForPrint(value:string){
  const match=/^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  return match?match[3]+"/"+match[2]+"/"+match[1]:value.trim().slice(0,24);
}

export function drawPhotoSheetPage(
  context:CanvasRenderingContext2D,image:HTMLImageElement,canvasWidth:number,size:PhotoSize,copies:number,photosPerPage:number,pageIndex:number,
  zoom:number,focusX:number,focusY:number,adjustments:Adjustments,
  caption:{name:string;dob:string;fontSizePt:number;color:string},
  background:{sourceColor:string|null;targetColor:string;tolerance:number},captionHeightMm:number
){
  const scale=canvasWidth/A4_WIDTH_MM,layout=getPhotoSheetLayout(size,captionHeightMm,photosPerPage);
  const tileHeight=size.heightMm+captionHeightMm,pageStart=pageIndex*photosPerPage;
  const pageCopies=Math.max(0,Math.min(photosPerPage,copies-pageStart));
  const visibleRows=Math.max(1,Math.ceil(pageCopies/layout.columns));
  const visibleContentHeightMm=visibleRows*tileHeight+(visibleRows-1)*3;
  const pageOffsetY=Math.max(8,(A4_HEIGHT_MM-visibleContentHeightMm)/2);
  context.save();context.setTransform(1,0,0,1,0,0);context.fillStyle="#fff";
  context.fillRect(0,0,canvasWidth,context.canvas.height);context.scale(scale,scale);
  const source=cropSource(image,size,zoom,focusX,focusY);
  const filter=`brightness(${adjustments.brightness}%) contrast(${adjustments.contrast}%) saturate(${adjustments.saturation}%)`;
  const photoW=Math.max(1,Math.round(size.widthMm/25.4*300)),photoH=Math.max(1,Math.round(size.heightMm/25.4*300));
  for(let index=0;index<pageCopies;index++){
    const col=index%layout.columns,row=Math.floor(index/layout.columns);
    const rowStart=row*layout.columns,rowCount=Math.min(layout.columns,pageCopies-rowStart);
    const rowWidth=rowCount*size.widthMm+(rowCount-1)*3;
    const rowOffset=(layout.contentWidthMm-rowWidth)/2;
    const x=layout.offsetXmm+rowOffset+col*(size.widthMm+3),y=pageOffsetY+row*(tileHeight+3);
    const crop=document.createElement("canvas");crop.width=photoW;crop.height=photoH;
    const cropContext=crop.getContext("2d");if(!cropContext)throw new Error("A photo crop could not be prepared.");
    cropContext.filter=filter;cropContext.drawImage(image,source.x,source.y,source.width,source.height,0,0,photoW,photoH);cropContext.filter="none";
    if(background.sourceColor)replaceEdgeBackground(crop,background.sourceColor,background.targetColor,background.tolerance);
    context.drawImage(crop,x,y,size.widthMm,size.heightMm);
    context.save();context.strokeStyle="#7b8491";context.lineWidth=.18;context.setLineDash([.8,.8]);context.strokeRect(x,y,size.widthMm,size.heightMm);context.restore();
    if(captionHeightMm>0){
      const lines=[caption.name.trim().slice(0,48),caption.dob.trim()?("DOB: "+formatDobForPrint(caption.dob)):""].filter(Boolean);
      const fontMm=caption.fontSizePt*25.4/72;context.save();context.fillStyle=caption.color;
      context.font=`600 ${fontMm}px Arial, sans-serif`;context.textAlign="center";context.textBaseline="top";
      const lineHeight=Math.max(fontMm+.35,1.7);
      lines.forEach((line,i)=>context.fillText(line,x+size.widthMm/2,y+size.heightMm+.45+i*lineHeight,size.widthMm-1.2));
      context.restore();
    }
  }
  context.restore();
}

export function cropSource(image: HTMLImageElement, size: PhotoSize, zoom: number, focusX: number, focusY: number) {
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

export function canvasToBlob(canvas: HTMLCanvasElement, type: string, quality?: number) {
  return new Promise<Blob>((resolve, reject) => {
    canvas.toBlob(blob => blob ? resolve(blob) : reject(new Error("The photo sheet could not be rendered.")), type, quality);
  });
}

export function safeBaseName(fileName: string) {
  const withoutExtension = fileName.replace(/\.[^.]+$/, "");
  return withoutExtension.replace(/[^a-zA-Z0-9_-]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 48) || "photo";
}

export function sourceResolutionNote(image: HTMLImageElement, size: PhotoSize) {
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
