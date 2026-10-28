import {frameTimes,presentedFrame} from './frame-index';
import {hashDistance,pixelHash} from './torso.mjs';
import {id,type Clip,type VisionItem} from './types';
export async function matchOriginal(clip:Clip,blob:Blob,item:VisionItem):Promise<{proof?:unknown;reason?:string}>{
 const origin=clip.previewFrames[0]?.mediaUs;if(origin==null)return {reason:'无可核验的首帧参考，保留候选'};
 const video=document.createElement('video');video.muted=true;video.playsInline=true;video.src=URL.createObjectURL(blob);
 const canvas=document.createElement('canvas');canvas.width=480;canvas.height=Math.round(480*clip.height/clip.width);
 try{
  await new Promise<void>((resolve,reject)=>{video.onloadeddata=()=>resolve();video.onerror=()=>reject(new Error('原片解码失败'));});
  const pts=await frameTimes(blob),actuals=new Map<number,{pts:number;hash:string}>(),selected=[];
  for(const [index,liveUs] of [item.payload.beforeMediaUs,item.payload.clock.mediaUs,item.payload.afterMediaUs].entries()){
   // The relative live PTS locates a search window only. It never determines elapsed time.
   const target=(liveUs-origin)/1e6,near=pts.filter(p=>Math.abs(p-target)<.75);
   if(near.length>180||!near.length)return {reason:'原片对应搜索范围异常'};
   const options=[];
   for(const p of near){let value=actuals.get(p);if(!value){const frame=await presentedFrame(video,p),bitmap=await createImageBitmap(frame.image);canvas.getContext('2d')!.drawImage(bitmap,0,0,canvas.width,canvas.height);bitmap.close();const image=canvas.getContext('2d',{willReadFrequently:true})!.getImageData(0,0,canvas.width,canvas.height);value={pts:frame.mediaUs,hash:pixelHash(image.data,image.width,image.height)};actuals.set(p,value);}options.push({...value,d:hashDistance(item.payload.frameHashes[index],value.hash)});}
   const unique=[...new Map(options.map(o=>[o.pts,o])).values()].sort((a,b)=>a.d-b.d);
   if(unique.length<2||unique[0].d>16||unique[1].d-unique[0].d<4)return {reason:'原片帧对应不唯一，保持待核验'};
   selected.push({...unique[0],margin:unique[1].d-unique[0].d});
  }
  if(!(selected[0].pts<selected[1].pts&&selected[1].pts<selected[2].pts))return {reason:'原片关键帧顺序不能核验'};
  const {sha256}=await import('@noble/hashes/sha2.js');const hash=sha256.create();for(let n=0;n<blob.size;n+=4*1024*1024)hash.update(new Uint8Array(await blob.slice(n,n+4*1024*1024).arrayBuffer()));
  return {proof:{requestId:id(),eventId:item.id,beforePtsUs:selected[0].pts,framePtsUs:selected[1].pts,afterPtsUs:selected[2].pts,
   maxHashDistance:Math.max(...selected.map(v=>v.d)),uniquenessMargin:Math.min(...selected.map(v=>v.margin)),hashes:selected.map(v=>v.hash),recordingSha256:Array.from(hash.digest(),n=>n.toString(16).padStart(2,'0')).join('')}};
 }finally{video.pause();URL.revokeObjectURL(video.src);video.removeAttribute('src');video.load();}
}
