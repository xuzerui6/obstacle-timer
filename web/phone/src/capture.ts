import {id,nowUs,type Clip,type HeatView,type VisionSettings} from './types';
import {vision,MODEL,defaultVision} from './vision';
import {listClips,saveClip,saveChunk} from './storage';
import {link} from './link';
export class CameraCapture {
 stream?:MediaStream;recorder?:MediaRecorder;clip?:Clip;private saved=Promise.resolve();private chunk=0;private stopping=false;private wake?:WakeLockSentinel;
 private phase:(phase:string)=>void;private changed:()=>void;private firstSaved?:()=>void;private saveError?:Error;private pulse?:ReturnType<typeof setInterval>;private readySent=false;
 constructor(phase:(phase:string)=>void,changed:()=>void){this.phase=phase;this.changed=changed;}
 async open(video:HTMLVideoElement){
  if(!window.isSecureContext||!navigator.mediaDevices?.getUserMedia)throw new Error('相机需要安全连接，请扫码完成证书信任并打开 HTTPS 页面');
  if(!window.MediaRecorder||!video.requestVideoFrameCallback)throw new Error('浏览器缺少录像或逐帧接口，请使用较新 Safari / Chrome');
  this.stream?.getTracks().forEach(t=>t.stop());
  this.stream=await navigator.mediaDevices.getUserMedia({audio:false,video:{facingMode:{ideal:'environment'},width:{ideal:1280},height:{ideal:720},frameRate:{ideal:120,max:240}}});
  video.srcObject=this.stream;await video.play();this.phase('preview');this.changed();
 }
 settings(){return this.stream?.getVideoTracks()[0]?.getSettings();}
 async start(view:HeatView,video?:HTMLVideoElement,visionSettings:VisionSettings=defaultVision):Promise<void>{
  if(!this.stream)throw new Error('请先开启相机');if(this.recorder?.state==='recording')throw new Error('已经在录像');
  if((await listClips()).some(c=>c.heatId===view.heat.id))throw new Error('本组已有原片，不会覆盖；请恢复录像或新建组次');
  if(!link.snapshot().connected)throw new Error('手机未连接电脑');
  const mime=['video/mp4;codecs=avc1.42E01E','video/mp4','video/webm;codecs=vp8','video/webm'].find(t=>MediaRecorder.isTypeSupported(t));
  if(!mime)throw new Error('浏览器没有可用录像编码器');
  const settings=this.settings()!;const clip:Clip={id:id(),heatId:view.heat.id,view:structuredClone(view),created:Date.now(),recorderStartedClientUs:nowUs(),mime:mime.startsWith('video/mp4')?'video/mp4':'video/webm',
   width:settings.width??1280,height:settings.height??720,fps:settings.frameRate??30,chunkCount:0,complete:false,pageClockId:id(),previewFrames:[],status:'recording',model:navigator.userAgent,vision:structuredClone(visionSettings),
   captureFingerprint:JSON.stringify({ua:navigator.userAgent,device:link.deviceId,width:settings.width,height:settings.height,fps:settings.frameRate,camera:settings.deviceId,mime,model:MODEL,adapter:'rvfc-pixels-wasm-intcast-v2',app:'0.4.1',analysisMaxEdge:320,analysisPolicy:'latest-ready-v1'})};
  this.clip=clip;this.chunk=0;this.saved=Promise.resolve();this.saveError=undefined;this.stopping=false;this.readySent=false;await saveClip(clip);
  this.recorder=new MediaRecorder(this.stream,{mimeType:mime,videoBitsPerSecond:6_000_000});
  const initialSaved=new Promise<void>(resolve=>{this.firstSaved=resolve;});
  this.recorder.ondataavailable=event=>{
   if(!event.data.size)return;const index=this.chunk++;
   this.saved=this.saved.then(async()=>{await saveChunk(clip.id,index,event.data);clip.chunkCount=index+1;await saveClip(clip);this.firstSaved?.();this.firstSaved=undefined;})
    .catch(error=>{this.saveError=error;link.notify('本机存储失败，正在结束录像；Timy3 继续独立计时');this.stop(true);});
  };
  this.recorder.onerror=event=>{this.saveError=new Error((event as ErrorEvent).message??'编码失败');link.notify('录像失败，Timy3 已开始的计时仍继续');this.stop(true);};
  this.recorder.onstop=()=>{void this.finish(clip).catch(error=>{this.stream?.getTracks().forEach(t=>t.stop());void this.wake?.release();this.phase('error');link.notify(`原始分片保留，结束失败：${(error as Error).message}`);});};
  this.recorder.start(1000);this.phase('recording');if(video)vision.start(video,clip);
  this.pulse=setInterval(()=>{if(this.readySent&&this.recorder?.state==='recording'&&!this.saveError)link.send('recordingStatus',{heatId:clip.heatId,recordingId:clip.id,recording:true,complete:false,note:'',savedChunks:clip.chunkCount});},3000);
  try{this.wake=await navigator.wakeLock?.request('screen');}catch{/* Optional. */}
  try{await navigator.storage?.persist();}catch{/* Writes are checked independently. */}
  let timeout:ReturnType<typeof setTimeout>|undefined;
  try{
   await Promise.race([initialSaved,new Promise<never>((_,reject)=>{timeout=setTimeout(()=>reject(new Error('浏览器未及时保存录像分片；Timy3 计时不受影响')),15000);})]);
   if(this.saveError)throw this.saveError;if(this.recorder.state!=='recording')throw new Error('录像已停止，请新建组次');
   this.readySent=true;clip.registrationPending=true;await saveClip(clip);
   link.send('recordingReady',{heatId:clip.heatId,recordingId:clip.id,deviceId:link.deviceId,videoMime:clip.mime,fps:clip.fps,width:clip.width,height:clip.height,savedChunks:clip.chunkCount,model:clip.model,captureFingerprint:clip.captureFingerprint,vision:clip.vision});
   void link.retry();
   link.notify('已开始整组录像与躯干候选检测；尚无实测达标配置时保持待核验');
  }catch(error){this.stop(true);throw error;}finally{if(timeout)clearTimeout(timeout);}
 }
 closePreview(){if(this.recorder?.state!=='recording'){this.stream?.getTracks().forEach(t=>t.stop());this.stream=undefined;this.changed();}}
 stop(early=false){if(this.recorder?.state==='recording'&&!this.stopping){this.stopping=true;vision.stop();if(this.clip){this.clip.recordingEndClientUs=nowUs();this.clip.earlyStop=early;}this.recorder.stop();}}
 private async finish(clip:Clip){
  if(this.pulse)clearInterval(this.pulse);await this.saved;clip.complete=!this.saveError;clip.status=this.saveError?'storage-error':'review';clip.recordingEndClientUs??=nowUs();clip.statusPending=this.readySent;
  await saveClip(clip);await this.wake?.release();this.wake=undefined;this.stream?.getTracks().forEach(t=>t.stop());this.stream=undefined;
  this.phase('review');this.changed();void link.retry();
 }
}
