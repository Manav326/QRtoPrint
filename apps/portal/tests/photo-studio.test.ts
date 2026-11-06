import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";

const read = (path: string) => readFileSync(new URL(path, import.meta.url), "utf8");
import { buildPhotoPdf,getPhotoSheetLayout } from "../src/photo-studio-layout.ts";

test("exact physical photo layout starts at the top safe margin on A4",()=>{
 const layout=getPhotoSheetLayout({widthMm:35,heightMm:45});
 assert.equal(layout.perSheet,25);
 assert.ok(Math.abs((210-layout.contentWidthMm)/2-layout.offsetXmm)<.001);
 assert.equal(layout.offsetYmm,8);
});
test("caption labels reserve their own area and never shrink photo dimensions",()=>{
 const plain=getPhotoSheetLayout({widthMm:35,heightMm:45});
 const labelled=getPhotoSheetLayout({widthMm:35,heightMm:45},7);
 assert.ok(labelled.perSheet<=plain.perSheet);
 assert.equal(labelled.offsetXmm,plain.offsetXmm);
 assert.equal(labelled.contentWidthMm,plain.contentWidthMm);
 assert.equal(labelled.contentHeightMm,labelled.rows*52+(labelled.rows-1)*3);
});
test("all configured photo sizes have positive safe layouts",()=>{
 for(const size of [{widthMm:35,heightMm:45},{widthMm:25,heightMm:35},{widthMm:50.8,heightMm:50.8},{widthMm:101.6,heightMm:152.4}]){
  const layout=getPhotoSheetLayout(size,7);
  assert.ok(layout.columns>=1&&layout.rows>=1&&layout.perSheet>=1);
  assert.ok(layout.offsetXmm>=8&&layout.offsetYmm>=8);
  assert.ok(layout.offsetXmm+layout.contentWidthMm<=202.001);
  assert.ok(layout.offsetYmm+layout.contentHeightMm<=289.001);
 }
});
test("multiple prepared A4 pages become one multi-page PDF",()=>{
 const jpeg=new Uint8Array([0xff,0xd8,0xff,0xd9]);
 const pdf=buildPhotoPdf([jpeg,jpeg,jpeg]);
 const value=new TextDecoder("latin1").decode(pdf);
 assert.ok(value.startsWith("%PDF-1.4"));
 assert.match(value,/\/Count 3/);
 assert.equal((value.match(/\/Type \/Page \/Parent 2 0 R/g)??[]).length,3);
 assert.match(value,/\/MediaBox \[0 0 595\.275590551 841\.88976378\]/);
 const xrefOffset=Number(value.match(/startxref\n(\d+)\n%%EOF/)?.[1]);
 assert.ok(Number.isInteger(xrefOffset));
 assert.ok(value.slice(xrefOffset).startsWith("xref\n"));
 let objectNumber=1;
 for(const match of value.slice(xrefOffset).matchAll(/(\d{10}) 00000 n/g)){
  const offset=Number(match[1]);
  assert.ok(value.slice(offset).startsWith(`${objectNumber} 0 obj`),`PDF object ${objectNumber} has a valid cross-reference offset`);
  objectNumber+=1;
 }
 assert.equal(objectNumber,12);
});
test("photo PDF generation rejects empty or incomplete JPEG pages",()=>{
 assert.throws(()=>buildPhotoPdf([]),/At least one/);
 assert.throws(()=>buildPhotoPdf([new Uint8Array([1,2,3])]),/complete JPEG/);
});

test("name and DOB are opt-in and are absent from the default photo sheet",()=>{
 const studio=readFileSync(new URL("../src/PhotoStudio.tsx",import.meta.url),"utf8");
 assert.match(studio,/const \[includeFormDetails,setIncludeFormDetails\]=useState\(false\)/);
 assert.match(studio,/const hasCaption=includeFormDetails&&Boolean\(photoName\.trim\(\)\|\|dateOfBirth\.trim\(\)\)/);
 assert.match(studio,/checked=\{includeFormDetails\}/);
 assert.match(studio,/\{includeFormDetails&&<>[\s\S]*?Name for form[\s\S]*?Date of birth/);
 assert.match(studio,/if\(!enabled\)\{setPhotoName\(""\);setDateOfBirth\(""\);\}/);
});


test("customer Photo Studio cannot turn an already uploaded original into a second print job",()=>{
 const customer=readFileSync(new URL("../src/features/customer/CustomerFilePreview.tsx",import.meta.url),"utf8");
 const upload=readFileSync(new URL("../src/features/customer/useCustomerPrintUpload.ts",import.meta.url),"utf8");
 const flow=readFileSync(new URL("../src/features/customer/useCustomerPrintFlow.ts",import.meta.url),"utf8");
 assert.match(upload,/if\(uploadedFileFingerprints\.includes\(fingerprint\)\)/);
 assert.match(flow,/const photoAlreadyUploaded=Boolean\(file&&uploadedFileFingerprints\.includes\(customerFileFingerprint\(file\)\)\)/);
 assert.match(customer,/disabled=\{busy\|\|batchLockedForPayment\|\|photoAlreadyUploaded\}/);
 assert.match(upload,/This photo is already with the shop[\s\S]*?without creating another print job/);
});
test("customer photo preparation depends on configured shop pricing, not the current printer capability",()=>{
 const app=readFileSync(new URL("../src/App.tsx",import.meta.url),"utf8");
 assert.doesNotMatch(app,/photoStudioReady|isPhotoStudioPrinterReady/);
 assert.match(app,/if\(!photoRatesAvailable\)/);
 assert.match(app,/photoPricing=\{result\.photoPricing\}/);
});

test("the shared Photo Studio explains different save actions for customers and shop owners",()=>{
 const studio=readFileSync(new URL("../src/PhotoStudio.tsx",import.meta.url),"utf8");
 const app=readFileSync(new URL("../src/App.tsx",import.meta.url),"utf8");
 assert.match(studio,/mode = "customer"/);
 assert.match(studio,/mode === "shop"/);
 assert.match(studio,/Prepare A4 sheet/);
 assert.match(studio,/Save sheet to this job/);
 assert.match(app,/PhotoStudio source=\{photoStudioSource\} mode="customer"/);
 assert.match(app,/PhotoStudio source=\{shopPhotoStudio\.source\} mode="shop"/);
});

test("shop owners can inspect the actual prepared photo PDF before approving the job",()=>{
 const app=readFileSync(new URL("../src/App.tsx",import.meta.url),"utf8");
 assert.match(app,/isPdf&&job\.printKind==="PHOTO"[\s\S]*?<iframe src=\{url\} title=\{name\}\//);
 assert.match(app,/SMARTPRINT · SHOP PRINT ONLY · PRIVATE PREVIEW/);
});

test("Photo Studio locks background scroll and supports accessible keyboard dismissal",()=>{
 const studio=readFileSync(new URL("../src/PhotoStudio.tsx",import.meta.url),"utf8");
 const css=readFileSync(new URL("../src/PhotoStudio.module.css",import.meta.url),"utf8");
 assert.match(studio,/aria-labelledby="photo-studio-title"/);
 assert.match(studio,/document\.body\.style\.overflow = "hidden"/);
 assert.match(studio,/event\.key === "Escape"/);
 assert.match(studio,/event\.key !== "Tab"/);
 assert.match(studio,/disabled=\{busy\} aria-label="Close Photo Studio"/);
 assert.match(css,/@media\(max-width:390px\)[\s\S]*?topbarMeta span:last-child\{display:none\}/);
});

test("a successful shop photo PDF save is not misreported as failed when refreshing the queue fails",()=>{
 const app=readFileSync(new URL("../src/App.tsx",import.meta.url),"utf8");
 assert.match(app,/The enhanced multi-page A4 photo PDF is saved to the same print job/);
 assert.match(app,/The A4 photo PDF was saved to the existing print job, but the queue could not refresh/);
});


test("short portrait phones keep Photo Studio preview, controls and save actions usable",()=>{
 const css=readFileSync(new URL("../src/PhotoStudio.module.css",import.meta.url),"utf8");
 assert.match(css,/@media\(max-width:720px\) and \(max-height:620px\)/);
 assert.match(css,/grid-template-rows:40px minmax\(160px,1fr\) auto/);
 assert.match(css,/\.toolPanel\{grid-row:3;[^}]*max-height:34vh;[^}]*overflow:auto/);
 assert.match(css,/\.toolPanel\{max-height:26vh/);
 assert.match(css,/\.canvasWorkspace\{grid-template-rows:32px minmax\(0,1fr\) 26px/);
 assert.match(css,/\.footerActions button\{min-height:34px\}/);
});


test("Android image providers with blank MIME types still expose Photo Studio by supported filename",()=>{
 const customer=readFileSync(new URL("../src/features/customer/CustomerFilePreview.tsx",import.meta.url),"utf8");
 const utils=readFileSync(new URL("../src/features/customer/customer-print-utils.ts",import.meta.url),"utf8");
 assert.match(utils,/export function isCustomerImage\(file:File\):boolean/);
 assert.ok(utils.includes(String.raw`|| /\.(jpe?g|png)$/i.test(file.name);`));
 assert.match(customer,/isCustomerImage\(file\)&&photoRatesAvailable/);
});
test("shop cancellation returns to the original document preview",()=>{
 const actions=readFileSync(new URL("../src/features/shop/useShopPrintActions.ts",import.meta.url),"utf8");
 const shop=readFileSync(new URL("../src/features/shop/ShopConsole.tsx",import.meta.url),"utf8");
 const modal=readFileSync(new URL("../src/features/shop/modals/DocumentPreviewModal.tsx",import.meta.url),"utf8");
 assert.match(actions,/returnPreview:\{name:string;blob:Blob;mediaType:string;job:ShopPrintJob;readOnly:boolean\}/);
 assert.match(actions,/function cancelShopPhotoStudio\(\)[\s\S]*?URL\.createObjectURL\(restored\.blob\)[\s\S]*?setPreview/);
 assert.match(shop,/onCancel=\{cancelShopPhotoStudio\}/);
 assert.match(shop,/focusEditOnMount=\{focusShopPhotoEdit\}/);
 assert.match(actions,/setFocusShopPhotoEdit\(true\)/);
 assert.match(modal,/editPhotoButtonRef\.current\?\.focus\(\)/);
 assert.match(shop,/setPreview\(null\); setFocusShopPhotoEdit\(false\)/);
});
test("Photo Studio follows the saved light or dark appearance in both customer and shop routes",()=>{
 const app=readFileSync(new URL("../src/App.tsx",import.meta.url),"utf8");
 const studio=readFileSync(new URL("../src/PhotoStudio.tsx",import.meta.url),"utf8");
 const css=readFileSync(new URL("../src/PhotoStudio.module.css",import.meta.url),"utf8");
 assert.match(app,/<CustomerPrintFlow dark=\{dark\}/);
 assert.match(app,/PhotoStudio source=\{photoStudioSource\} mode="customer" dark=\{dark\}/);
 assert.match(app,/PhotoStudio source=\{shopPhotoStudio\.source\} mode="shop" dark=\{dark\}/);
 assert.match(studio,/dark = false/);
 assert.match(studio,/dark \? " " \+ styles\.dark/);
 for(const selector of [".dark .toolPanel",".dark .fieldLabel",".dark .canvasWorkspace",".dark .footer",".dark .captionOptIn"])
  assert.ok(css.includes(selector),"Missing dark Photo Studio styling: "+selector);
});

test("Photo Studio restores focus to the customer or shop control after it closes",()=>{
 const studio=readFileSync(new URL("../src/PhotoStudio.tsx",import.meta.url),"utf8");
 assert.match(studio,/const previouslyFocused = document\.activeElement instanceof HTMLElement/);
 assert.match(studio,/previouslyFocused\?\.focus\(\)/);
});

test("customers are told when the shop has not enabled photo printing",()=>{
 const customer=readFileSync(new URL("../src/features/customer/CustomerFilePreview.tsx",import.meta.url),"utf8");
 assert.match(customer,/Photo printing is not enabled at this shop yet/);
 assert.match(customer,/Ask the shop owner to configure photo sizes and rates/);
});
test("50 photos can be laid out as nine per A4 page with a partially filled final page",()=>{
 const layout=getPhotoSheetLayout({widthMm:35,heightMm:45},0,9);
 assert.equal(layout.perSheet,9);
 assert.equal(layout.columns,5);
 assert.equal(layout.rows,2);
 const photoCount=50;
 const pageCount=Math.ceil(photoCount/layout.perSheet);
 assert.equal(pageCount,6);
 assert.equal(photoCount-(pageCount-1)*layout.perSheet,5);
});

test("manual photo density validates against physical page capacity",()=>{
 assert.equal(getPhotoSheetLayout({widthMm:35,heightMm:45},0,9).perSheet,9);
 assert.throws(()=>getPhotoSheetLayout({widthMm:35,heightMm:45},0,26),/Photos per A4 page/);
 assert.throws(()=>getPhotoSheetLayout({widthMm:35,heightMm:45},0,0),/Photos per A4 page/);
});

test("50 photos at nine per page build one six-page A4 PDF",()=>{
 const jpeg=new Uint8Array([0xff,0xd8,0xff,0xd9]);
 const pdf=buildPhotoPdf(Array.from({length:6},()=>jpeg));
 const value=new TextDecoder("latin1").decode(pdf);
 assert.match(value,/\/Count 6/);
 assert.equal((value.match(/\/Type \/Page \/Parent 2 0 R/g)??[]).length,6);
});

test("changing photos per A4 page redraws the live preview",()=>{
 const studio=readFileSync(new URL("../src/PhotoStudio.tsx",import.meta.url),"utf8");
 assert.match(studio,/\},\[image,size,copies,photosPerPage,previewPageIndex,/);
});

test("Photo Studio returns preview-only JPEG pages and the customer UI brands prepared pages",()=>{
 const studio=readFileSync(new URL("../src/PhotoStudio.tsx",import.meta.url),"utf8");
 const preview=readFileSync(new URL("../src/features/customer/CustomerFilePreview.tsx",import.meta.url),"utf8");
 assert.match(studio,/previewPages:jpegPages\.map\(jpeg=>new Blob\(\[jpeg\],\{type:"image\/jpeg"\}\)\)/);
 assert.match(preview,/customer-original-image-preview/);
 assert.match(preview,/customer-edited-photo-frame/);
 assert.match(preview,/SmartPrint by QuantaEdge/);
});


test("Photo Studio canvas protection stays isolated in the preview workspace", () => {
  const workspace = read("../src/PhotoStudioCanvasWorkspace.tsx");
  assert.match(workspace, /previewProtection/);
});
