import test from 'node:test';import assert from 'node:assert/strict';import {allCaptured} from '../src/group-capture.mjs';
test('video-only group can save a complete original while formal times await file verification',()=>{
 const view={heat:{start:{},finishAuthority:'camera-primary',finishes:[],visionEvents:[{id:'one'},{id:'two'}]},runs:[{state:'pending'},{state:'pending'}]};
 assert.equal(allCaptured(view),true);assert.ok(view.runs.every(r=>r.state==='pending'));
 view.heat.visionEvents.pop();assert.equal(allCaptured(view),false);
});
test('ignored visual events and legacy hardware groups never count as complete video capture',()=>{
 const view={heat:{start:{},finishAuthority:'camera-primary',finishes:[],visionEvents:[{ignored:true}]},runs:[{state:'pending'}]};assert.equal(allCaptured(view),false);
 view.heat.visionEvents=[{}];view.heat.finishAuthority='timy-device';assert.equal(allCaptured(view),false);
 view.heat.finishes=[{}];assert.equal(allCaptured(view),true);
});
