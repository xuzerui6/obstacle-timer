import test from 'node:test';import assert from 'node:assert/strict';
import {TorsoTracker,pixelHash,hashDistance,torsoPixels} from '../src/torso.mjs';
const settings={lineX:.5,left:0,right:1,top:0,bottom:1,direction:1};
const person=(edge,y=.5)=>({points:Array.from({length:36},(_,i)=>[edge-(i%6)*.01,y+Math.floor(i/6)*.01]),confidence:.95,mask:new Uint8Array(10)});
const frame=n=>({mediaUs:n*8333});
test('100 later athletes on the same path are never absorbed by an emitted track',()=>{
 const t=new TorsoTracker(settings);let events=0,time=0;
 for(let athlete=0;athlete<100;athlete++){
  for(const edge of [.40,.44,.48,.51,.54,.65,.82]){time+=33333;events+=t.update([person(edge)],{mediaUs:time}).length;}
  time+=1000000;t.update([],{mediaUs:time});
 }
 assert.equal(events,100);assert.ok(t.tracks.length<10,'retired frames and tracks must be bounded');
});
test('front edge of torso crosses once, reverse does not start, first crossing needs a later frame',()=>{
 const t=new TorsoTracker(settings);assert.equal(t.update([person(.47)],frame(0)).length,0);assert.equal(t.update([person(.51)],frame(1)).length,0);
 const events=t.update([person(.54)],frame(2));assert.equal(events.length,1);assert.equal(events[0].frame.mediaUs,8333);
 assert.equal(t.update([person(.49)],frame(3)).length,0);assert.equal(t.update([person(.52)],frame(4)).length,0);assert.equal(t.update([person(.54)],frame(5)).length,0);
});
test('100 closely following runners remain candidates; a possible returning identity stays ambiguous',()=>{
 const t=new TorsoTracker(settings);let time=0;const events=[];
 for(let n=0;n<100;n++)for(const edge of [.42,.46,.49,.52,.55]){time+=16667;events.push(...t.update([person(edge)],{mediaUs:time}));}
 assert.equal(events.length,100);assert.equal(new Set(events.map(e=>e.trackId)).size,100);
 assert.ok(events.slice(1).every(e=>e.ambiguous),'new/returning identity must never be guessed');
 assert.ok(t.tracks.length<=10);
});
test('stationary torso and long disappearance cannot fabricate a crossing',()=>{
 const t=new TorsoTracker(settings);for(let n=0;n<100;n++)assert.equal(t.update([person(.49)],frame(n)).length,0);
 t.update([],{mediaUs:4000000});assert.equal(t.update([person(.6)],{mediaUs:5000000}).length,0);assert.equal(t.update([person(.65)],{mediaUs:5010000}).length,0);
});
test('wrong direction and torso not visible produce no guessed finish',()=>{
 const t=new TorsoTracker(settings);assert.equal(t.update([person(.55)],frame(0)).length,0);assert.equal(t.update([person(.45)],frame(1)).length,0);assert.equal(t.update([],frame(2)).length,0);
});
test('close people, gaps and return across line must remain ambiguous',()=>{
 const t=new TorsoTracker(settings);t.update([person(.45),person(.46,.55)],frame(0));t.update([person(.51)],frame(100));const e=t.update([person(.48)],frame(101));
 assert.ok(e.length===0||e.every(v=>v.ambiguous||v.issues.length));
});
test('two separated torsos crossing in arbitrary arrival order remain separate',()=>{
 const t=new TorsoTracker(settings);t.update([person(.47,.1),person(.43,.7)],frame(0));t.update([person(.51,.1),person(.46,.7)],frame(1));
 const a=t.update([person(.54,.1),person(.51,.7)],frame(2));const b=t.update([person(.57,.1),person(.54,.7)],frame(3));
 assert.equal(a.length+b.length,2);assert.notEqual(a[0].trackId,b[0].trackId);assert.equal(a[0].ambiguous,false);
});
test('two people crossing in exactly the same frame retain separate stable instance keys',()=>{
 const t=new TorsoTracker(settings);t.update([person(.47,.1),person(.47,.7)],frame(0));t.update([person(.51,.1),person(.51,.7)],frame(1));const events=t.update([person(.54,.1),person(.54,.7)],frame(2));
 assert.equal(events.length,2);assert.equal(events[0].frame.mediaUs,events[1].frame.mediaUs);assert.notEqual(events[0].personIndex,events[1].personIndex);
});
test('dHash preserves deterministic frame identity and uses 256 bits',()=>{
 const pixels=new Uint8ClampedArray(100*100*4);for(let i=0;i<pixels.length;i++)pixels[i]=i%251;
 const h=pixelHash(pixels,100,100);assert.equal(h.length,64);assert.equal(hashDistance(h,h),0);assert.equal(hashDistance('0'.repeat(64),'f'.repeat(64)),256);
});
test('hands feet head and detached segmentation noise are excluded from torso edge',()=>{
 const parts=new Int32Array(100).fill(-1);for(let y=2;y<7;y++)for(let x=2;x<7;x++)parts[y*10+x]=12;
 parts[29]=10;parts[39]=23;parts[49]=0;parts[59]=13;
 const result=torsoPixels(parts,10,10);assert.equal(result.points.length,25);assert.equal(Math.max(...result.points.map(p=>p[0])),.6);
});
test('large portrait torso masks do not overflow JS argument limits or drop crossing',()=>{
 const t=new TorsoTracker(settings);
 const dense=edge=>({points:Array.from({length:160000},(_,i)=>[edge-(i%400)*.0002,.2+Math.floor(i/400)*.001]),confidence:.95,mask:new Uint8Array(160000)});
 t.update([dense(.48)],frame(0));t.update([dense(.51)],frame(1));const result=t.update([dense(.54)],frame(2));
 assert.equal(result.length,1);assert.equal(result[0].ambiguous,false);
});
