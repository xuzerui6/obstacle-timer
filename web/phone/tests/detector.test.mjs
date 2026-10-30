import test from 'node:test';import assert from 'node:assert/strict';
import {DetectorClient,StaleAnalysis} from '../src/detector-client.mjs';
function fake(){return {postMessage(m){queueMicrotask(()=>this.onmessage?.({data:{type:m.type==='frame'?'analyzed':'ready',requestId:m.requestId,sessionId:m.sessionId,events:[]}}));},terminate(){}};}
test('model load timeout is recoverable and does not cache a rejected promise',async()=>{
 let n=0;const c=new DetectorClient(()=>++n===1?{postMessage(){},terminate(){}}:fake(),{loadTimeout:10});
 await assert.rejects(c.load(),/30秒/);await c.load();assert.equal(n,2);c.dispose();
});
test('old analysis callbacks cannot complete a new session',async()=>{
 let workers=[];const c=new DetectorClient(()=>{const w=fake();workers.push(w);return w;});
 const first=await c.begin({});workers[0].postMessage=()=>{};
 const old=c.analyze({},first);const caught=assert.rejects(old,StaleAnalysis);const second=await c.begin({});await caught;
 const result=await c.analyze({},second);assert.equal(result.sessionId,second);await assert.rejects(c.analyze({},first),StaleAnalysis);c.dispose();
});
test('single frame timeout invalidates the busy worker and a new worker can start',async()=>{
 const c=new DetectorClient(fake,{frameTimeout:10});const session=await c.begin({});c.worker.postMessage=()=>{};
 await assert.rejects(c.analyze({},session),/10秒/);const next=await c.begin({});assert.ok(await c.analyze({},next));c.dispose();
});
