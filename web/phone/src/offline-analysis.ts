import {detector} from './model-runtime';
import {vision,defaultVision} from './vision';
import {MODEL,ANALYSIS_MAX_EDGE,HASH_VERSION} from './protocol';
import {frameTimes,presentedFrame} from './frame-index';
import {getClip,listJobs,saveJob,videoBlob} from './storage';
import {digestText,saveCandidate} from './vision-evidence';
import {pixelHash} from './torso.mjs';
import {id,type Clip,type AnalysisJob} from './types';
import {link} from './link';
class OfflineAnalysis {
 private job?:AnalysisJob;private listeners=new Set<()=>void>();private generation=0;
 subscribe=(fn:()=>void)=>{this.listeners.add(fn);return()=>this.listeners.delete(fn);};
 snapshot=()=>this.job;
 constructor(){vision.onLive(active=>{if(active)void this.pause('live');else if(this.job?.state==='paused'&&this.job.pauseReason==='live')void this.resume();});}
 private async update(patch:Partial<AnalysisJob>){if(!this.job)return;this.job={...this.job,...patch};await saveJob(this.job);this.listeners.forEach(fn=>fn());}
 async restore(){const jobs=await listJobs();const job=jobs.sort((a,b)=>b.created-a.created).find(j=>j.state!=='complete');if(job){this.job=job;await this.update({state:'paused',pauseReason:'user'});}}
 async start(clip:Clip,fromUs=0,toUs=Number.MAX_SAFE_INTEGER){
  if(vision.isLive())throw new Error('正在录像，原片补检会在录像结束后进行');if(!clip.complete||!clip.heatId)throw new Error('原片尚未封装，分片已保留，可导出后复核');
  if(clip.view?.heat.finishAuthority!=='camera-primary')throw new Error('旧设备计时组请使用原有选帧留证，不改历史来源');
  if(!Number.isFinite(fromUs)||!Number.isFinite(toUs)||fromUs<0||toUs<=fromUs)throw new Error('补检片段范围无效');
  await this.pause('user');
  const configHash=await digestText(JSON.stringify({clipId:clip.id,model:MODEL,settings:clip.vision??defaultVision,edge:ANALYSIS_MAX_EDGE,mode:'offline-v1'}));
  this.job={id:id(),clipId:clip.id,configHash,modelVersion:MODEL,settings:clip.vision??defaultVision,fromUs,toUs,cursorUs:-1,processed:0,total:0,state:'paused',created:Date.now()};
  await this.resume();
 }
 async pause(reason:'user'|'live'='user'){
  if(this.job?.state!=='running')return;this.generation++;detector.dispose();await this.update({state:'paused',pauseReason:reason});
 }
 async resume(){
  if(!this.job||this.job.state==='running')return;if(vision.isLive()){await this.update({state:'paused',pauseReason:'live'});return;}
  const clip=await getClip(this.job.clipId);if(!clip){await this.update({state:'error',error:'本机原片不存在'});return;}
  if(vision.isLive()){await this.update({state:'paused',pauseReason:'live'});return;}
  const generation=++this.generation;await this.update({state:'running',error:'',pauseReason:undefined});void this.execute(clip,this.job,generation);
 }
 private async execute(clip:Clip,job:AnalysisJob,generation:number){
  link.analysisBusy=true;
  const video=document.createElement('video');video.muted=true;video.playsInline=true;let url='';
  try{
   const blob=await videoBlob(clip),times=await frameTimes(blob);if(generation!==this.generation)return;
   const selected=times.filter(t=>t*1e6>=job.fromUs&&t*1e6<=job.toUs);if(selected.length<3)throw new Error('片段缺少过线前后画面，请扩大范围');
   await this.update({total:selected.length});url=URL.createObjectURL(blob);video.src=url;
   await new Promise<void>((resolve,reject)=>{const timer=setTimeout(()=>reject(new Error('原片读取超时，文件已保留')),10000);video.onloadeddata=()=>{clearTimeout(timer);resolve();};video.onerror=()=>{clearTimeout(timer);reject(new Error('原片无法解码，文件已保留'));};});
   const scale=Math.min(1,ANALYSIS_MAX_EDGE/Math.max(clip.width,clip.height)),canvas=document.createElement('canvas');canvas.width=Math.max(1,Math.round(clip.width*scale));canvas.height=Math.max(1,Math.round(clip.height*scale));
   let retries=0;
   retry:while(generation===this.generation){
    try{
     if(vision.isLive()){await this.pause('live');return;}
     const session=await detector.begin(job.settings),resumeAt=Math.max(job.fromUs,(this.job?.cursorUs??-1)-2000000);
     for(const [frameIndex,pts] of selected.entries()){
      if(generation!==this.generation)return;if(pts*1e6<resumeAt)continue;
      const actual=await presentedFrame(video,pts);if(generation!==this.generation)return;
      const bitmap=await createImageBitmap(actual.image),ctx=canvas.getContext('2d',{willReadFrequently:true})!;ctx.drawImage(bitmap,0,0,canvas.width,canvas.height);bitmap.close();
      const image=ctx.getImageData(0,0,canvas.width,canvas.height),frame={recordingId:clip.id,pixels:image.data,width:image.width,height:image.height,mediaUs:actual.mediaUs,hash:pixelHash(image.data,image.width,image.height)};
      const data=await detector.analyze(frame,session);if(generation!==this.generation)return;
      for(const event of data.events)await saveCandidate({...clip,vision:job.settings},event,'offline',job.configHash,['原片补检，仅供人工复核']);
      const cursor=Math.max(this.job?.cursorUs??-1,actual.mediaUs);await this.update({cursorUs:cursor,processed:Math.max(this.job?.processed??0,frameIndex+1)});
      await new Promise(resolve=>setTimeout(resolve,0));
     }
     await this.update({state:'complete'});break;
    }catch(error){if(generation!==this.generation)return;if(retries++>=2)throw error;detector.invalidate(error as Error);await new Promise(resolve=>setTimeout(resolve,1000*retries));continue retry;}
   }
  }catch(error){if(generation===this.generation)await this.update({state:'error',error:(error as Error).message});}
  finally{if(generation===this.generation||this.job?.state!=='running')link.analysisBusy=false;video.pause();video.removeAttribute('src');video.load();if(url)URL.revokeObjectURL(url);}
 }
}
export const offlineAnalysis=new OfflineAnalysis();

export async function saveManualCandidate(clip:Clip,pts:number[],index:number,line:number){
 if(index<1||index>=pts.length-1)throw new Error('请选择有前后相邻画面的帧');
 const video=document.createElement('video');video.muted=true;video.playsInline=true;const url=URL.createObjectURL(await videoBlob(clip));video.src=url;
 try{
  await new Promise<void>((resolve,reject)=>{const timer=setTimeout(()=>reject(new Error('原片读取超时')),10000);video.onloadeddata=()=>{clearTimeout(timer);resolve();};video.onerror=()=>{clearTimeout(timer);reject(new Error('原片解码失败'));};});
  const frames=[];const scale=ANALYSIS_MAX_EDGE/Math.max(clip.width,clip.height),canvas=document.createElement('canvas');canvas.width=Math.max(1,Math.round(clip.width*scale));canvas.height=Math.max(1,Math.round(clip.height*scale));
  for(const t of pts.slice(index-1,index+2)){const f=await presentedFrame(video,t),bitmap=await createImageBitmap(f.image),ctx=canvas.getContext('2d',{willReadFrequently:true})!;ctx.drawImage(bitmap,0,0,canvas.width,canvas.height);bitmap.close();const image=ctx.getImageData(0,0,canvas.width,canvas.height);frames.push({pixels:image.data,width:image.width,height:image.height,mediaUs:f.mediaUs,hash:pixelHash(image.data,image.width,image.height)});}
  const settings={...(clip.vision??defaultVision),lineX:line},config=await digestText(JSON.stringify({clip:clip.id,settings,mode:'manual-v1'}));
  await saveCandidate({...clip,vision:settings},{before:frames[0],frame:frames[1],after:frames[2],trackId:'manual',mask:new Uint8Array(canvas.width*canvas.height),confidence:0,ambiguous:true,issues:['裁判人工选帧，未进行自动躯干分割'],maxFrameGapUs:Math.max(frames[1].mediaUs-frames[0].mediaUs,frames[2].mediaUs-frames[1].mediaUs)},'manual',config);
 }finally{video.pause();URL.revokeObjectURL(url);}
}
