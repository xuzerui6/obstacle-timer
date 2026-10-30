// Bounded reads: encoded payloads are skipped; only actual block timestamps are indexed.
export async function webmBlobTimes(blob){
 let cache=new Uint8Array(),base=-1,scale=1000000;const frames=[];
 async function bytes(pos,n){if(pos<0||pos+n>blob.size)throw new Error('WebM 文件被截断');if(pos<base||pos+n>base+cache.length){base=pos;cache=new Uint8Array(await blob.slice(pos,Math.min(blob.size,pos+1024*1024)).arrayBuffer());}return cache.subarray(pos-base,pos-base+n);}
 async function vint(pos,keep=false){const first=(await bytes(pos,1))[0];let length=1,mask=128;while(length<=8&&!(first&mask)){length++;mask>>=1;}if(length>8)throw new Error('WebM 整数无效');const b=await bytes(pos,length);let value=keep?first:first&(mask-1),unknown=!keep&&value===mask-1;for(let i=1;i<length;i++){value=value*256+b[i];unknown=unknown&&b[i]===255;}return {length,value,unknown};}
 async function uint(pos,end){const b=await bytes(pos,end-pos);let n=0;for(const v of b)n=n*256+v;return n;}
 async function walk(start,end,cluster=0,unknownCluster=false){let pos=start;while(pos<end){const at=pos,tag=await vint(pos,true);if(unknownCluster&&tag.value===0x1f43b675)return at;pos+=tag.length;const size=await vint(pos);pos+=size.length;const stop=size.unknown?end:pos+size.value;if(stop>end||stop<pos)throw new Error('WebM 文件被截断或长度无效');
  if(tag.value===0x2ad7b1)scale=await uint(pos,stop);
  else if(tag.value===0xe7)cluster=await uint(pos,stop);
  else if(tag.value===0xa3||tag.value===0xa1){const track=await vint(pos);const b=await bytes(pos+track.length,3);if(pos+track.length+3>stop)throw new Error('WebM 帧不完整');let relative=(b[0]<<8)|b[1];if(relative&0x8000)relative-=65536;if(b[2]&6)throw new Error('该 WebM 视频使用暂不支持的帧交织');frames.push((cluster+relative)*scale/1e9);}
  else if([0x18538067,0x1549a966,0x1f43b675,0xa0].includes(tag.value)){pos=await walk(pos,stop,tag.value===0x1f43b675?0:cluster,tag.value===0x1f43b675&&size.unknown);if(size.unknown&&pos<stop)continue;}
  pos=stop;
 }return pos;}
 await walk(0,blob.size);if(!frames.length)throw new Error('没有找到可判读 WebM 帧');const sorted=[...new Set(frames)].sort((a,b)=>a-b),origin=sorted[0];return sorted.map(t=>t-origin);
}
