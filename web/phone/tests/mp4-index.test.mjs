import test from 'node:test';import assert from 'node:assert/strict';
import {mp4PresentationTimes} from '../src/mp4-index.mjs';
test('fragment tfdt gaps are not replaced by MP4Box estimated last-sample duration',()=>{
 const file={getTrackSamplesInfo:()=>[{moof_number:1,dts:0,cts:0,timescale:30000},{moof_number:1,dts:200038,cts:200038,timescale:30000},{moof_number:2,dts:201037,cts:201037,timescale:30000},{moof_number:2,dts:203081,cts:203081,timescale:30000}],moofs:[{trafs:[{tfhd:{track_id:1},tfdt:{baseMediaDecodeTime:0}}]},{trafs:[{tfhd:{track_id:1},tfdt:{baseMediaDecodeTime:202076}}]}]};
 assert.deepEqual(mp4PresentationTimes(file,1),[0,200038/30000,202076/30000,204120/30000]);
});
test('ordinary MP4 uses actual composition timestamps including B-frame reorder',()=>{
 const file={getTrackSamplesInfo:()=>[{dts:0,cts:100,timescale:1000},{dts:40,cts:180,timescale:1000},{dts:80,cts:140,timescale:1000}]};
 assert.deepEqual(mp4PresentationTimes(file,1).map(t=>Math.round(t*1e6)),[0,40000,80000]);
});
