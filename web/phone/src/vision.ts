import {nowUs,type Clip} from './types';
import {link} from './link';
import {patchClip} from './storage';
import {pixelHash} from './torso.mjs';
import {detector} from './model-runtime';
import {StaleAnalysis} from './detector-client.mjs';
import {saveCandidate} from './vision-evidence';
import {MODEL,ANALYSIS_MAX_EDGE} from './protocol';
export {MODEL};
export const defaultVision={lineX:.5,top:.1,bottom:.95,left:.05,right:.95,direction:1,fixedSide:false};
export class VisionCapture {
 private state='尚未加载';private listeners=new Set<()=>void>();private liveListeners=new Set<(value:boolean)=>void>();
 private canvas=document.createElement('canvas');private handle=0;private video?:HTMLVideoElement;
 private generation=0;private context?:any;private reportAt=0;private live=false;private liveBusy=false;private preload?:Promise<void>;
 constructor(){setInterval(()=>this.report(),2000);}
 subscribe=(fn:()=>void)=>{this.listeners.add(fn);return()=>this.listeners.delete(fn);};
 snapshot=()=>this.state;
 onLive(fn:(active:boolean)=>void){this.liveListeners.add(fn);return()=>{this.liveListeners.delete(fn);};}
 isLive(){return this.live;}
 private markLive(value:boolean){if(this.live===value)return;this.live=value;this.liveListeners.forEach(fn=>fn(value));}
 private status(message:string){this.state=message;this.listeners.forEach(fn=>fn());this.report();}
 private report(){
  const current=link.snapshot().snapshot?.currentHeat,c=this.context,heatId=c?.clip.heatId??current?.heat.id;
  if(!heatId||c&&c.clip.registrationPending!==false||nowUs()-this.reportAt<2e6)return;this.reportAt=nowUs();
  const stage=c?.failed?'recoverable-error':!this.live?'stopped':!c?.ready?'loading':!c.people?'no-target':!c.torso?'no-torso':!c.generated?'tracking':'local-candidate';
  link.send('visionStatus',{heatId,recordingId:c?.clip.id??'',stage,analysisSessionId:c?.session??'',status:this.state,captured:c?.captured??0,analyzed:c?.analyzed??0,people:c?.people??0,torsoPixels:c?.torso??0,queued:this.liveBusy?1:0,generated:c?.generated??0,saved:c?.saved??0,lastError:String(c?.error||link.uploadError||'').slice(0,1000)});
 }
 async load(){
  if(this.live)return;if(this.preload)return this.preload;
  this.preload=(async()=>{
   for(let attempt=0;attempt<=2;attempt++){
    if(this.live)return;this.status(attempt?`模型加载重试 ${attempt}/2（不启用相机）`:'正在加载本地躯干模型（不启用相机）');
    try{await detector.load();if(!this.live)this.status('本地躯干模型就绪 · WASM');return;}
    catch(error){if(this.live)return;if(attempt===2){this.status(`模型暂不可用，可手动重试；原片仍可保存：${(error as Error).message}`);throw error;}await new Promise(resolve=>setTimeout(resolve,1000*attempt+1000));}
   }
  })().finally(()=>{this.preload=undefined;});return this.preload;
 }
 async retryModel(){
  const c=this.context;if(this.live&&c){c.retries=0;c.failed=false;c.restarted=true;await this.begin(c);}
  else{detector.dispose();await this.load();}
 }
 private async begin(c:any){
  c.ready=false;
  while(c.generation===this.generation){
   try{const session=await detector.begin(c.clip.vision);if(c.generation!==this.generation)return;c.session=session;c.ready=true;c.error='';this.status('检测已就绪，等待躯干通过终点线');return;}
   catch(error){if(c.generation!==this.generation||error instanceof StaleAnalysis)return;c.error=(error as Error).message;
    if(c.retries++>=2){c.failed=true;this.status(`检测暂停，可手动重试；原片继续保存：${c.error}`);return;}
    c.restarted=true;this.status(`模型恢复中（${c.retries}/2），原片继续保存`);await new Promise(resolve=>setTimeout(resolve,c.retries===1?1000:3000));
   }
  }
 }
 start(video:HTMLVideoElement,clip:Clip){
  if(this.video)this.video.cancelVideoFrameCallback(this.handle);
  this.markLive(true);this.video=video;this.liveBusy=false;
  const c={generation:++this.generation,clip,session:'',ready:false,failed:false,retries:0,restarted:false,captured:0,analyzed:0,people:0,torso:0,generated:0,saved:0,error:'',startedUs:nowUs()};this.context=c;
  void this.begin(c);
  const next=(_time:number,meta:VideoFrameCallbackMetadata)=>{
   if(this.video!==video||c.generation!==this.generation)return;this.handle=video.requestVideoFrameCallback(next);c.captured++;
   if(this.liveBusy||!c.ready||c.failed)return;
   try{
    const extra=meta as VideoFrameCallbackMetadata&{captureTime?:number},source=Number.isFinite(extra.captureTime)?'rvfc-captureTime':'rvfc-presentationTime';
    const clientUs=Math.round((source==='rvfc-captureTime'?extra.captureTime!:meta.presentationTime)*1000),scale=Math.min(1,ANALYSIS_MAX_EDGE/Math.max(video.videoWidth,video.videoHeight));
    this.canvas.width=Math.max(1,Math.round(video.videoWidth*scale));this.canvas.height=Math.max(1,Math.round(video.videoHeight*scale));
    const ctx=this.canvas.getContext('2d',{willReadFrequently:true})!;ctx.drawImage(video,0,0,this.canvas.width,this.canvas.height);
    const image=ctx.getImageData(0,0,this.canvas.width,this.canvas.height);
    const frame={recordingId:clip.id,sequence:c.captured,pixels:image.data,width:image.width,height:image.height,hash:pixelHash(image.data,image.width,image.height),mediaUs:Math.round(meta.mediaTime*1e6),clientUs,callbackClientUs:nowUs(),source,clock:link.clockEvidence(clip.pageClockId)};
    if(!clip.previewFrames.length){clip.previewFrames.push({mediaUs:frame.mediaUs,presentationUs:clientUs});clip.frameGeometry={width:image.width,height:image.height,hashVersion:'dhash256-v1'};void patchClip(clip.id,{previewFrames:clip.previewFrames,frameGeometry:clip.frameGeometry});}
    this.liveBusy=true;
    void detector.analyze(frame,c.session).then(async data=>{
     if(c.generation!==this.generation)return;c.analyzed++;c.people=data.people;c.torso=data.torsoPixels;c.generated+=data.events.length;c.error='';
     const rate=c.analyzed/Math.max(.1,(nowUs()-c.startedUs)/1e6),detail=!data.people?'没有目标':!data.torsoPixels?'躯干尚不可见':!c.generated?'躯干尚未过线':`本机候选 ${c.generated}`;this.status(`检测中 · 分析 ${rate.toFixed(1)} fps · ${data.people} 个目标 · ${detail}（原片独立保存）`);
     for(const event of data.events){try{if(await saveCandidate(clip,event,'live',c.session,c.restarted?['本组模型已重启，轨迹需人工复核']:[]))c.saved++;}catch(error){c.error=`候选本机保存失败：${(error as Error).message}`;this.status(c.error+'；原片保留，可补检');}}
    }).catch(error=>{
     if(c.generation!==this.generation||error instanceof StaleAnalysis)return;c.error=error.message;c.ready=false;c.restarted=true;this.status(`检测待恢复：${c.error}`);detector.invalidate(error);if(c.retries++>=2){c.failed=true;this.status('检测已暂停，请重试模型；原片继续保存');}else void this.begin(c);
    }).finally(()=>{if(c.generation===this.generation){this.liveBusy=false;if(!this.video){this.markLive(false);this.status('录像已结束，候选保留，原片可补检');}}});
   }catch(error){c.error=(error as Error).message;this.status(`画面读取失败，原片继续保存：${c.error}`);}
  };
  this.handle=video.requestVideoFrameCallback(next);
 }
 stop(){if(this.video)this.video.cancelVideoFrameCallback(this.handle);this.video=undefined;this.status('录像已停止，正在保存分析结果');if(!this.liveBusy)this.markLive(false);}
}
export const vision=new VisionCapture();
