import { useRef,useState } from "react";
import { Download } from "lucide-react";
import QRCode from "react-qr-code";

type Props={uri:string|null;payeeName:string;upiId:string;amount:string;fileName:string};

/** Downloadable high-resolution UPI QR so it can be scanned from another device/gallery. */
export default function CustomerUpiQr({uri,payeeName,upiId,amount,fileName}:Props){
 const root=useRef<HTMLDivElement|null>(null);
 const [error,setError]=useState("");
 const [busy,setBusy]=useState(false);
 async function download(){
  const svg=root.current?.querySelector("svg");
  if(!uri||!svg||busy)return;
  setBusy(true);setError("");
  let svgUrl:string|null=null,pngUrl:string|null=null;
  try{
   const clone=svg.cloneNode(true) as SVGSVGElement;
   clone.setAttribute("xmlns","http://www.w3.org/2000/svg");
   clone.setAttribute("width","1024");clone.setAttribute("height","1024");
   svgUrl=URL.createObjectURL(new Blob([new XMLSerializer().serializeToString(clone)],{type:"image/svg+xml;charset=utf-8"}));
   const image=new Image();
   await new Promise<void>((resolve,reject)=>{image.onload=()=>resolve();image.onerror=()=>reject(new Error("The QR image could not be prepared."));image.src=svgUrl!;});
   const canvas=document.createElement("canvas"),quiet=80,size=1024;
   canvas.width=size+quiet*2;canvas.height=size+quiet*2;
   const context=canvas.getContext("2d");
   if(!context)throw new Error("This browser cannot create the QR image.");
   context.fillStyle="#fff";context.fillRect(0,0,canvas.width,canvas.height);
   context.imageSmoothingEnabled=false;context.drawImage(image,quiet,quiet,size,size);
   const png=await new Promise<Blob>((resolve,reject)=>canvas.toBlob(blob=>blob?resolve(blob):reject(new Error("The QR image could not be saved.")),"image/png"));
   pngUrl=URL.createObjectURL(png);
   const anchor=document.createElement("a");anchor.href=pngUrl;anchor.download=fileName;anchor.rel="noopener";
   document.body.appendChild(anchor);anchor.click();anchor.remove();
   const downloadedUrl=pngUrl;window.setTimeout(()=>URL.revokeObjectURL(downloadedUrl),1500);pngUrl=null;
  }catch(e){setError(e instanceof Error?e.message:"The QR image could not be downloaded.");}
  finally{if(svgUrl)URL.revokeObjectURL(svgUrl);if(pngUrl)URL.revokeObjectURL(pngUrl);setBusy(false);}
 }
 return <div className="customer-upi-details">
  <div className="customer-upi-qr" ref={root}>
   {uri?<QRCode value={uri} size={180} level="H" bgColor="#ffffff" fgColor="#111827"/>:
    <div className="customer-upi-qr-unavailable">The payment QR is unavailable. Ask the shop to verify its UPI ID and try again.</div>}
  </div>
  <strong>{payeeName}</strong><span>{upiId}</span><strong className="customer-upi-amount">{amount}</strong>
  {uri&&<button type="button" className="secondary-button customer-upi-download" onClick={()=>void download()} disabled={busy}><Download size={15}/>{busy?"Preparing QR…":"Download QR image"}</button>}
  {error&&<small className="customer-upi-download-error" role="alert">{error}</small>}
  {uri&&<a href={uri} className="customer-flow-button customer-upi-open">Open UPI app</a>}
 </div>;
}
