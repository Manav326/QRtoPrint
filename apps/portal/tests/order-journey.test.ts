import assert from "node:assert/strict";
import test from "node:test";
import type { ShopPayment, ShopPrintJob } from "../src/api.ts";
import { buildShopOrderJourneys } from "../src/order-journey.ts";
function job(v: Partial<ShopPrintJob> & Pick<ShopPrintJob,"id"|"jobNumber"|"status">): ShopPrintJob {
  return {
    id:v.id, jobNumber:v.jobNumber, status:v.status, batchId:v.batchId??null,
    batchName:v.batchName??null, batchGroupName:v.batchGroupName??null, rejectedPageNumbersJson:"[]",
    pageCount:v.pageCount??2,copies:v.copies??1,colourMode:"Mono",paperSize:"A4",isDuplex:false,
    currency:v.currency??"INR",quotedAmountMinor:v.quotedAmountMinor??1000,
    createdUtc:v.createdUtc??"2026-11-04T10:00:00Z",
    document:v.document??{originalFileName:v.jobNumber+".pdf",mediaType:"application/pdf",sizeBytes:1024},
    payment:v.payment??null,failureCode:v.failureCode??null
  } as ShopPrintJob;
}
function payment(v: Partial<ShopPayment> & Pick<ShopPayment,"id"|"printJobId"|"status">): ShopPayment {
  return {
    id:v.id,printJobId:v.printJobId,batchPaymentId:v.batchPaymentId??null,batchFileCount:v.batchFileCount??null,
    method:v.method??"UPI",status:v.status,amountMinor:v.amountMinor??2000,currency:v.currency??"INR",
    paymentReference:v.paymentReference??null,createdUtc:v.createdUtc??"2026-11-04T10:05:00Z",
    updatedUtc:v.updatedUtc??"2026-11-04T10:05:00Z"
  } as ShopPayment;
}
test("groups files in one batch and associates its batch payment",()=>{
  const jobs=[job({id:"a",jobNumber:"SP-1",batchId:"batch-1",batchName:"Morning orders",status:"AWAITING_SHOP_APPROVAL",pageCount:3}),
    job({id:"b",jobNumber:"SP-2",batchId:"batch-1",batchName:"Morning orders",status:"AWAITING_SHOP_APPROVAL",pageCount:5})];
  const pay=payment({id:"batch-payment-id",printJobId:"a",batchPaymentId:"batch-payment-id",status:"CUSTOMER_REPORTED"});
  const orders=buildShopOrderJourneys(jobs,[pay]);
  assert.equal(orders.length,1);assert.equal(orders[0].name,"Morning orders");
  assert.equal(orders[0].jobs.length,2);assert.equal(orders[0].payment?.id,"batch-payment-id");
  assert.equal(orders[0].stage,"payment");assert.equal(orders[0].pageCount,8);
});
test("keeps unbatched uploads as separate orders",()=>{
  const orders=buildShopOrderJourneys([job({id:"a",jobNumber:"SP-1",status:"AWAITING_SHOP_APPROVAL"}),job({id:"b",jobNumber:"SP-2",status:"AWAITING_SHOP_APPROVAL"})],[]);
  assert.equal(orders.length,2);assert.notEqual(orders[0].key,orders[1].key);
});
test("maps the lifecycle to received, review, payment, printing and ready",()=>{
  const cases=[["UPLOADED","received"],["AWAITING_SHOP_APPROVAL","review"],["AWAITING_PAYMENT","payment"],
    ["PAYMENT_REJECTED","payment"],["PAID_AUTHORISED","printing"],["LOCAL_QUEUE_CLAIMED","printing"],
    ["LOCAL_QUEUE_READY","printing"],["PRINT_AUTHORISED","printing"],["SPOOLER_ACCEPTED","printing"],
    ["PRINTING","printing"],["COMPLETED","ready"],["PDF_EXPORTED","ready"]];
  for(const [status,expected] of cases) assert.equal(buildShopOrderJourneys([job({id:status,jobNumber:status,status})],[])[0].stage,expected,status);
});
test("one rejected file does not hide accepted files still printing",()=>{
  const order=buildShopOrderJourneys([job({id:"a",jobNumber:"SP-1",batchId:"b",status:"REJECTED"}),
    job({id:"b",jobNumber:"SP-2",batchId:"b",status:"PRINTING"})],[])[0];
  assert.equal(order.stage,"printing");assert.equal(order.acceptedCount,1);assert.equal(order.rejectedCount,1);assert.equal(order.isClosed,false);
});
test("output uncertainty and failures are visible and never marked ready",()=>{
  for(const status of ["OUTPUT_UNCONFIRMED","FAILED"]) {
    const order=buildShopOrderJourneys([job({id:status,jobNumber:status,status,failureCode:status==="FAILED"?"PRINTER_OFFLINE":null})],[])[0];
    assert.equal(order.stage,"printing");assert.equal(order.attentionJobs.length,1);assert.notEqual(order.stage,"ready");
  }
});
test("reported payments are eligible for the existing manual verification action",()=>{
  const pay=payment({id:"pay",printJobId:"a",status:"CUSTOMER_REPORTED"});
  const order=buildShopOrderJourneys([job({id:"a",jobNumber:"SP-1",status:"AWAITING_PAYMENT"})],[pay])[0];
  assert.equal(order.canVerifyPayment,true);assert.equal(order.stage,"payment");
});
test("a verified payment advances the journey into printing preparation",()=>{
  const pay=payment({id:"pay",printJobId:"a",status:"VERIFIED"});
  const order=buildShopOrderJourneys([job({id:"a",jobNumber:"SP-1",status:"PAID_AUTHORISED"})],[pay])[0];
  assert.equal(order.stage,"printing");
});
