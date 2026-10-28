import {id,nowUs,type Clip} from './types';
import {link} from './link';
import {saveVision} from './storage';
import {pixelHash} from './torso.mjs';
export const MODEL='bodypix-2.2.1-m075-s8-q2-torso-v1';
export const defaultVision={lineX:.5,top:.1,bottom:.95,left:.05,right:.95,direction:1,fixedSide:false};
export class VisionCapture {
 private worker?:Worker;private loaded?:Promise<void>;private busy=false;private handle=0;private video?:HTMLVideoElement;private clip?:Clip;private serial=0;
 private canvas=document.createElement('canvas');private state='尚未加载';private listeners=new Set<()=>void>();private clips=new Map<string,Clip>();private queue:any[]=[];
 private captured=0;private analyzed=0;private people=0;private torso=0;private generated=0;private saved=0;private lastError='';private lastReport=0;private startedUs=0;
 constructor(){setInterval(()=>this.report(),2000);}
 subscribe=(fn:()=>void)=>{this.listeners.add(fn);return()=>this.listeners.delete(fn);};
 snapshot=()=>this.state;
 private status(s:string){this.state=s;this.listeners.forEach(fn=>fn());this.report();}
 private report(force=false){
  const current=link.snapshot().snapshot?.currentHeat,heatId=this.clip?.heatId??current?.heat.id;
  if(this.clip&&this.clip.registrationPending!==false)return;
  if(!heatId||!force&&nowUs()-this.lastReport<2e6)return;this.lastReport=nowUs();
  link.send('visionStatus',{heatId,recordingId:this.clip?.id??'',status:this.state,captured:this.captured,analyzed:this.analyzed,people:this.people,torsoPixels:this.torso,queued:this.queue.length,generated:this.generated,saved:this.saved,lastError:(this.lastError||link.uploadError).slice(0,1000)});
 }
 load(){return this.loaded??=new Promise<void>((resolve,reject)=>{
  this.status('正在加载本地躯干模型（不启用相机）');
  const worker=new Worker(new URL('./vision.worker.ts',import.meta.url),{type:'module'});this.worker=worker;
  worker.onerror=e=>{this.lastError=e.message;this.status('模型不可用，录像仍可保存');this.report(true);reject(new Error(e.message));};
  worker.onmessage=({data})=>{
   if(data.type==='ready'){this.status('本地躯干模型就绪 · WASM');resolve();}
   if(data.type==='error'){this.busy=false;this.lastError=data.message;this.status(`检测待处理：${data.message}`);this.report(true);if(!this.clip)reject(new Error(data.message));else this.pump();}
   if(data.type==='analyzed'){
    this.busy=false;this.analyzed++;this.people=data.people;this.torso=data.torsoPixels??0;this.generated+=data.events.length;this.lastError='';const rate=this.analyzed/Math.max(.1,(nowUs()-this.startedUs)/1e6);this.status(`检测中 · 分析 ${rate.toFixed(1)} fps · ${data.people} 个目标 · 候选 ${this.generated}（原片独立保存）`);
    for(const event of data.events)void this.persist(event).catch(e=>{this.status(`候选保存失败：${e.message}`);link.notify('候选保存失败，原片继续保留，请人工复核');});
    this.pump();
   }
  };worker.postMessage({type:'load'});
 });}
 start(video:HTMLVideoElement,clip:Clip){
  this.video=video;this.clip=clip;this.clips.set(clip.id,clip);this.queue=[];this.busy=false;this.serial=0;this.captured=0;this.analyzed=0;this.generated=0;this.saved=0;this.lastError='';this.startedUs=nowUs();this.worker?.postMessage({type:'reset',settings:clip.vision});
  const next=(_time:number,meta:VideoFrameCallbackMetadata)=>{
   if(this.clip!==clip)return;this.handle=video.requestVideoFrameCallback(next);
   this.captured++;
   // Never build an inference backlog. Keep recording at the camera's actual rate,
   // and sample the newest pixel/timestamp pair only when the Worker can accept it.
   // Missing analysis frames remain visible in MaxFrameGapUs and approval gates.
   if(this.busy){this.report();return;}
   const callbackClientUs=nowUs(),extra=meta as VideoFrameCallbackMetadata&{captureTime?:number};
   const source=Number.isFinite(extra.captureTime)?'rvfc-captureTime':'rvfc-presentationTime';
   const clientUs=Math.round((source==='rvfc-captureTime'?extra.captureTime!:meta.presentationTime)*1000);
   const scale=Math.min(1,320/Math.max(video.videoWidth,video.videoHeight));
   this.canvas.width=Math.max(1,Math.round(video.videoWidth*scale));this.canvas.height=Math.max(1,Math.round(video.videoHeight*scale));
   const context=this.canvas.getContext('2d',{willReadFrequently:true})!;context.drawImage(video,0,0,this.canvas.width,this.canvas.height);
   const image=context.getImageData(0,0,this.canvas.width,this.canvas.height);
   const frame={recordingId:clip.id,sequence:++this.serial,pixels:image.data,width:image.width,height:image.height,hash:pixelHash(image.data,image.width,image.height),
    mediaUs:Math.round(meta.mediaTime*1e6),clientUs,callbackClientUs,source,clock:link.clockEvidence(clip.pageClockId)};
   if(!clip.previewFrames.length)clip.previewFrames.push({mediaUs:frame.mediaUs,presentationUs:clientUs});
   this.queue=[frame];this.pump();this.report();
  };this.handle=video.requestVideoFrameCallback(next);
 }
 private pump(){if(!this.busy&&this.queue.length){this.busy=true;this.worker?.postMessage({type:'frame',frame:this.queue.shift()});}}
 stop(){if(this.video)this.video.cancelVideoFrameCallback(this.handle);this.video=undefined;this.status('录像结束，缓冲分析继续，候选与原片保留');this.report(true);}
 private async persist(event:any){
  const {before,frame,after}=event;const clip=this.clips.get(frame.recordingId);if(!clip)return;
  const blob=async(f:any,mask=false)=>{
   const c=document.createElement('canvas');c.width=f.width;c.height=f.height;const ctx=c.getContext('2d')!;
   if(mask){const p=new Uint8ClampedArray(f.width*f.height*4);for(let i=0;i<event.mask.length;i++){p[i*4]=60;p[i*4+1]=255;p[i*4+2]=130;p[i*4+3]=event.mask[i];}ctx.putImageData(new ImageData(p,f.width,f.height),0,0);}
   else ctx.putImageData(new ImageData(new Uint8ClampedArray(f.pixels),f.width,f.height),0,0);
   return new Promise<Blob>((resolve,reject)=>c.toBlob(b=>b?resolve(b):reject(new Error('证据编码失败')),mask?'image/png':'image/jpeg',.95));
  };
  const eventId=id(),payload={requestId:id(),eventId,heatId:clip.heatId,recordingId:clip.id,deviceId:link.deviceId,trackId:`${clip.id}-${event.trackId}`,
   modelVersion:MODEL,captureFingerprint:clip.captureFingerprint,settings:clip.vision,
   clock:{...frame.clock,source:frame.source,mediaUs:frame.mediaUs,clientUs:frame.clientUs,callbackClientUs:frame.callbackClientUs},
   beforeMediaUs:before.mediaUs,afterMediaUs:after.mediaUs,maxFrameGapUs:event.maxFrameGapUs,confidence:event.confidence,ambiguous:event.ambiguous,
   issues:event.issues,frameHashes:[before.hash,frame.hash,after.hash]};
  const [a,b,c,mask]=await Promise.all([blob(before),blob(frame),blob(after),blob(frame,true)]);
  await saveVision({id:eventId,clipId:clip.id,payload,files:{before:a,image:b,after:c,mask,metadata:new Blob([JSON.stringify({...payload,track: [before,frame,after].map(f=>({sequence:f.sequence,mediaUs:f.mediaUs,clientUs:f.clientUs,hash:f.hash}))})],{type:'application/json'})},created:Date.now(),complete:false});
  this.saved++;this.report(true);
  link.notify('躯干过线候选已保存在本机；请电脑匹配号码，等待时间与证据核验');void link.retry();
 }
}
export const vision=new VisionCapture();
