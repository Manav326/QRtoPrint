import assert from "node:assert/strict";
import test from "node:test";
import {buildCustomerUpiIntent} from "../src/upi-payment-intent.ts";

test("complete API UPI intent remains authoritative",()=>{
 const server="upi://pay?pa=shop%40bank&pn=Shop&am=12.00&cu=INR&tr=abc123";
 assert.equal(buildCustomerUpiIntent({serverUri:server,upiId:"other@bank",payeeName:"Other",amountMinor:999,currency:"INR",transactionReference:"other",description:"Other"}),server);
});
test("incomplete API URI is rebuilt with amount and transaction reference",()=>{
 const uri=buildCustomerUpiIntent({serverUri:"upi://pay?pa=shop%40bank",upiId:"counter@bank",payeeName:"Counter",amountMinor:12345,currency:"INR",transactionReference:"123e4567-e89b-12d3-a456-426614174000",description:"SmartPrint batch"});
 assert.ok(uri);const parsed=new URL(uri);
 assert.equal(parsed.searchParams.get("pa"),"counter@bank");
 assert.equal(parsed.searchParams.get("pn"),"Counter");
 assert.equal(parsed.searchParams.get("am"),"123.45");
 assert.equal(parsed.searchParams.get("cu"),"INR");
 assert.equal(parsed.searchParams.get("tr"),"123e4567e89b12d3a456426614174000");
});
test("incomplete payment snapshot never produces payee-only intent",()=>{
 assert.equal(buildCustomerUpiIntent({upiId:"shop@bank",payeeName:"Shop",amountMinor:0,currency:"INR",transactionReference:"abc123",description:"SmartPrint"}),null);
});
