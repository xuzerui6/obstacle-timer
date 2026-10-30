import test from 'node:test';import assert from 'node:assert/strict';import {webmBlobTimes} from '../src/webm-index.mjs';
test('WebM bounded index uses actual variable block times without whole-file arrayBuffer',async()=>{
 const blob=new Blob([Uint8Array.from([0x1f,0x43,0xb6,0x75,0x91,0xe7,0x81,0,0xa3,0x85,0x81,0,0,0x80,0,0xa3,0x85,0x81,0,47,0x80,0])]);
 blob.arrayBuffer=()=>{throw new Error('whole file read forbidden');};assert.deepEqual(await webmBlobTimes(blob),[0,.047]);
});
test('truncated WebM is retained as an error, not nominal-rate inferred',async()=>{await assert.rejects(webmBlobTimes(new Blob([new Uint8Array([0x1f,0x43,0xb6])])),/截断/);});
test('a virtual 512 MiB original indexes timestamps with bounded reads and skips encoded bytes',async()=>{
 const size=512*1024*1024,header=Uint8Array.from([0x1f,0x43,0xb6,0x75,0xff,0xe7,0x81,0,0xa3,0x10,0,0,0]);
 // An 8-byte size VINT for a very large SimpleBlock; the payload is virtual.
 const prefix=[0x1f,0x43,0xb6,0x75,0xff,0xe7,0x81,0,0xa3,0x01,0,0,0,0,0,0,0];let value=BigInt(size);for(let n=16;n>=9;n--){prefix[n]=Number(value&255n);value>>=8n;}prefix[9]|=1;
 const first=Uint8Array.from([...prefix,0x81,0,0,0x80]),last=Uint8Array.from([0xa3,0x85,0x81,0,47,0x80,0]),end=prefix.length+size,reads=[];
 const blob={size:end+last.length,slice(start,stop){reads.push(stop-start);const bytes=new Uint8Array(stop-start);for(let i=0;i<first.length;i++)if(i>=start&&i<stop)bytes[i-start]=first[i];for(let i=0;i<last.length;i++)if(end+i>=start&&end+i<stop)bytes[end+i-start]=last[i];return new Blob([bytes]);}};
 assert.deepEqual(await webmBlobTimes(blob),[0,.047]);assert.ok(reads.every(n=>n<=1024*1024));assert.ok(reads.reduce((a,b)=>a+b,0)<2*1024*1024);
});
