import {PROTOCOL,APP_VERSION} from './protocol';
import {HttpFailure,UploadYield,retryable,failed,acknowledged} from './retry-policy.mjs';
import {estimateClock} from './clock.mjs';
import {id,nowUs,type ClockMap,type Snapshot,type Outbox} from './types';
import {getClip,listClips,listOutbox,saveClip,saveOutbox,videoBlob,listVision,saveVision,patchClip} from './storage';
type Sample={id:string;proof:string;t1:number;t2:number;t3:number;t4:number};
type View={connected:boolean;status:string;snapshot:Snapshot|null;clock:ClockMap|null;updateAvailable?:boolean};
class PhoneLink {
 uploadError='';recording=false;analysisBusy=false;private largeBusy=false;private verificationBusy=false;private needsPair=false;
 readonly deviceId=localStorage.getItem('obstacle-device')??id();
 private token=localStorage.getItem('obstacle-token')??'';
 private ws?:WebSocket; private listeners=new Set<()=>void>();private samples:Sample[]=[];private requests=new Map<string,number>();
 private view:View={connected:false,status:'',snapshot:null,clock:null};private initialized=false;private uploading=false;
 private lastReceivedUs=0;
 constructor(){localStorage.setItem('obstacle-device',this.deviceId);}
 subscribe=(fn:()=>void)=>{this.listeners.add(fn);return()=>this.listeners.delete(fn);};
 snapshot=()=>this.view;
 private publish(next:Partial<View>){this.view={...this.view,...next};this.listeners.forEach(fn=>fn());}
 notify=(status:string)=>this.publish({status});
 async initialize(){
  if(this.initialized)return;this.initialized=true;
  const secret=new URLSearchParams(location.hash.slice(1)).get('secret');
  if(secret)await this.pair(secret);else if(this.token)this.connect();
  window.addEventListener('hashchange',()=>{const value=new URLSearchParams(location.hash.slice(1)).get('secret');if(value)void this.pair(value).catch(error=>this.notify((error as Error).message));});
  window.addEventListener('offline',()=>this.disconnect('网络离线，本机继续保存录像与截图，恢复后补传'));
  window.addEventListener('online',()=>{if(this.token)this.connect();});
  setInterval(()=>{if(this.view.connected&&nowUs()-this.lastReceivedUs>15e6)this.disconnect('电脑连接超时，录像与判读保留在本机');
   if(this.view.connected){this.sync();void this.retry();}else if(this.token)this.connect();},5000);
 }
 async pair(secret:string){
  if(!secret.trim())throw new Error('请扫描电脑上的二维码，或输入配对码');
  const controller=new AbortController(),timer=setTimeout(()=>controller.abort(),15000);let response:Response;
  try{response=await fetch('/api/pair',{method:'POST',signal:controller.signal,headers:{'Content-Type':'application/json'},body:JSON.stringify({secret:secret.trim(),deviceId:this.deviceId,appVersion:APP_VERSION})});}finally{clearTimeout(timer);}
  const data=await this.decode(response);if(data.protocol!==PROTOCOL)throw new Error('电脑协议版本不匹配，请更新电脑安装包');this.token=data.token;localStorage.setItem('obstacle-token',this.token);
  history.replaceState(null,'',location.pathname);this.needsPair=false;this.disconnect('正在重新配对，录像文件保留');this.samples=[];this.requests.clear();this.publish({clock:null});
  for(const item of await listVision()){
   let changed=false;if(item.blocked==='authentication'){Object.assign(item,acknowledged(item));changed=true;}
   if(item.verificationRetry?.blocked==='authentication'){item.verificationRetry=acknowledged(item.verificationRetry);changed=true;}
   if(changed)await saveVision(item);
  }
  for(const item of await listOutbox())if(item.blocked==='authentication')await saveOutbox(acknowledged(item));
  for(const item of await listClips())if(item.blocked==='authentication')await patchClip(item.id,{blocked:undefined,error:undefined,nextAttemptAt:0});this.connect();
 }
 private connect(){
  if(!navigator.onLine||this.needsPair)return;
  if(this.ws?.readyState===WebSocket.OPEN||this.ws?.readyState===WebSocket.CONNECTING)return;
  if(location.protocol!=='https:'){this.notify('请从电脑的安全 HTTPS 地址打开相机页面');return;}
  const ws=new WebSocket(`${location.origin.replace('https:','wss:')}/api/ws?access_token=${encodeURIComponent(this.token)}`);this.ws=ws;
  ws.onopen=()=>{this.lastReceivedUs=nowUs();this.publish({connected:true,status:'电脑已连接，正在同步'});for(let i=0;i<30;i++)setTimeout(()=>this.sync(),i*50);};
  ws.onmessage=(message)=>{
   if(this.ws!==ws)return;this.lastReceivedUs=nowUs();
   const t4=nowUs();try{
    const {type,data}=JSON.parse(message.data);
    if(type==='state'){
     if(data.protocol!==PROTOCOL){ws.close();this.notify('电脑与网页协议不同，请结束录像后更新至 v4');return;}
     if(this.view.snapshot?.sessionId!==data.sessionId){this.samples=[];this.requests.clear();for(let i=0;i<30;i++)setTimeout(()=>this.sync(),i*50);}
     this.publish({snapshot:data,updateAvailable:data.appVersion&&data.appVersion!==APP_VERSION});
    }else if(type==='sync'){
     const t1=this.requests.get(data.id);this.requests.delete(data.id);
     if(t1===data.t1&&data.sessionId===this.view.snapshot?.sessionId){
      this.samples.push({id:data.id,proof:data.proof,t1:t1!,t2:data.t2,t3:data.t3,t4});this.samples=this.samples.slice(-120);
      if(this.samples.length>=5)this.publish({clock:estimateClock(data.sessionId,this.samples)});
     }
    }else if(type==='error')this.notify(data.message);
    else if(type==='recordingAccepted')this.notify('电脑已确认本组录像；拍到最后一人后再结束录像');
   }catch(error){this.notify(`消息处理失败：${(error as Error).message}`);}
  };
  ws.onclose=()=>{if(this.ws===ws)this.publish({connected:false,status:'连接已断开，正在保留本机录像；恢复后补传'});};
  ws.onerror=()=>{this.notify('连接失败，文件保留；正在检查配对状态');void this.api('/api/state').catch(()=>{});};
 }
 private disconnect(status:string){const old=this.ws;this.ws=undefined;old?.close();this.publish({connected:false,status});}
 private sync(){const request=id(),t1=nowUs();this.requests.set(request,t1);for(const [key,time]of this.requests)if(t1-time>1e7)this.requests.delete(key);this.send('sync',{id:request,t1});}
 clock():ClockMap{
  const map=this.view.clock;if(!map||map.samples<5||nowUs()-map.sampledClientUs>6e7)throw new Error('时钟尚未同步，请等待数秒');return structuredClone(map);
 }
 clockEvidence(pageClockId:string){return {sessionId:this.view.snapshot?.sessionId??'',pageClockId,mapId:id(),samples:structuredClone(this.samples)};}
 send(type:string,data:unknown){if(this.ws?.readyState===WebSocket.OPEN)this.ws.send(JSON.stringify({type,data}));}
 async api(path:string,init?:RequestInit,timeout=15000){
  const controller=new AbortController(),timer=setTimeout(()=>controller.abort(),timeout);
  try{return await this.decode(await fetch(path,{...init,signal:controller.signal,headers:{Authorization:`Bearer ${this.token}`,...init?.headers}}));}
  finally{clearTimeout(timer);}
 }
 private async decode(response:Response){
  const body=await response.text();let data:any={};try{data=body?JSON.parse(body):{};}catch{throw new HttpFailure(response.status,`服务器响应异常 ${response.status}`);}
  if(!response.ok){
   if(response.status===401||response.status===403){this.needsPair=true;this.disconnect('配对已失效，请重新扫描电脑二维码；本机录像与待传记录保留');}
   throw new HttpFailure(response.status,data.error??(response.status===401||response.status===403?'配对已失效，请重新扫码':`请求被拒绝 ${response.status}`));
  }return data;
 }
 async retry(){
  if(this.uploading||!this.view.connected||this.needsPair)return;this.uploading=true;
  const failures:string[]=[];
  try{
   for(const clip of await listClips()){
    if(!clip.heatId||clip.chunkCount<1||!retryable(clip)||clip.registrationPending===false&&!clip.statusPending)continue;
    try{
     if(clip.registrationPending!==false){
      await this.api('/api/recording-ready',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({heatId:clip.heatId,recordingId:clip.id,deviceId:this.deviceId,videoMime:clip.mime,fps:clip.fps,width:clip.width,height:clip.height,savedChunks:clip.chunkCount,model:clip.model??'',captureFingerprint:clip.captureFingerprint??'',vision:clip.vision})});
      await patchClip(clip.id,{registrationPending:false});
     }
     if(clip.statusPending){await this.api('/api/recording-status',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({heatId:clip.heatId,recordingId:clip.id,recording:false,complete:clip.complete&&!clip.earlyStop,note:clip.earlyStop?'提前停止，证据可能不完整':'整组原片已封装',savedChunks:clip.chunkCount})});await patchClip(clip.id,{statusPending:false});}
     await patchClip(clip.id,{attempts:0,error:undefined,blocked:undefined,nextAttemptAt:0});
    }catch(error){const state=failed(clip,error);await patchClip(clip.id,{attempts:state.attempts,error:state.error,blocked:state.blocked,nextAttemptAt:state.nextAttemptAt});failures.push((error as Error).message);if(this.needsPair)break;}
   }
   for(const item of (await listVision()).filter(v=>!v.evidenceUploaded&&retryable(v))){
    try{
     const clip=await getClip(item.clipId);if(!clip||clip.chunkCount<1||clip.registrationPending!==false)continue;
     if(!item.submitted){await this.api(item.payload.origin&&item.payload.origin!=='live'?'/api/vision-candidates/offline':'/api/vision-events',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(item.payload)});item.submitted=true;await saveVision(item);}
     for(const kind of ['image','before','after','mask','metadata'])await this.upload(item.clipId,item.id,item.payload.heatId,kind,item.files[kind]);
     Object.assign(item,acknowledged(item),{evidenceUploaded:true});await saveVision(item);
    }catch(error){await saveVision(failed(item,error));failures.push((error as Error).message);if(this.needsPair)break;}
   }
   for(const item of (await listOutbox()).filter(v=>!v.reviewUploaded&&v.payload.heatId&&retryable(v))){
    try{
     const ack=await this.api('/api/reviews',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(item.payload)});item.acceptedRevision=ack.review.revision;await saveOutbox(item);
     await this.upload(item.clipId,item.payload.evidenceId,item.payload.heatId,'image',item.image);await this.upload(item.clipId,item.payload.evidenceId,item.payload.heatId,'metadata',item.metadata);
     Object.assign(item,acknowledged(item),{reviewUploaded:true});await saveOutbox(item);
    }catch(error){await saveOutbox(failed(item,error));failures.push((error as Error).message);if(this.needsPair)break;}
   }
   this.uploadError=failures[0]??'';if(failures.length)this.notify(`本机记录保留，待处理：${failures[0]}`);
  }catch(error){this.uploadError=(error as Error).message;this.notify(`文件保留：${this.uploadError}`);}
  finally{this.uploading=false;void this.retryOriginals();void this.retryVerification();}
 }
 private async retryOriginals(){
  if(this.largeBusy||this.recording||!this.view.connected||this.needsPair)return;this.largeBusy=true;
  try{
   const candidates=(await listVision()).filter(v=>v.evidenceUploaded&&!v.complete&&retryable(v)),reviews=(await listOutbox()).filter(v=>v.reviewUploaded&&!v.complete&&retryable(v));
   const owners=new Map<string,{clipId:string;id:string;heat:string}>();for(const v of candidates)owners.set(v.clipId,{clipId:v.clipId,id:v.id,heat:v.payload.heatId});for(const v of reviews)owners.set(v.clipId,{clipId:v.clipId,id:v.payload.evidenceId,heat:v.payload.heatId});
   for(const owner of owners.values()){
    if(this.recording)break;const clip=await getClip(owner.clipId);if(!clip?.complete)continue;
    try{
     await this.upload(clip.id,owner.id,owner.heat,'video',await videoBlob(clip));await patchClip(clip.id,{serverAcknowledged:true});
     for(const v of candidates.filter(v=>v.clipId===clip.id))await saveVision({...acknowledged(v),complete:true});
     for(const v of reviews.filter(v=>v.clipId===clip.id))await saveOutbox({...acknowledged(v),complete:true});
     this.notify('截图与原片已获电脑确认；原片仍保留，时间与帧核验状态单独显示');
    }catch(error){if(error instanceof UploadYield)break;for(const v of candidates.filter(v=>v.clipId===clip.id))await saveVision(failed(v,error));for(const v of reviews.filter(v=>v.clipId===clip.id))await saveOutbox(failed(v,error));this.uploadError=(error as Error).message;}
   }
  }finally{this.largeBusy=false;void this.retryVerification();}
 }
 async retryVerification(force=false){
  if(this.verificationBusy||this.recording||this.analysisBusy||!this.view.connected||this.needsPair)return;this.verificationBusy=true;
  try{
   for(const item of await listVision()){
    if(this.recording||this.analysisBusy)break;
    if(!item.complete||!force&&!retryable(item.verificationRetry??{})||item.verificationState==='verified'&&!item.verificationReportPending||!force&&!item.verificationReportPending&&['needs-review','unmapped'].includes(item.verificationState??''))continue;
    try{
     if(item.verificationReportPending){/* Resume the small status delivery without decoding the original again. */}
     else if(!item.payload.clock||item.payload.origin&&item.payload.origin!=='live'){item.verificationState='unmapped';item.correspondenceReason='原片补检/人工选帧无有效采集时钟映射';}
     else{
      const clip=await getClip(item.clipId);if(!clip)continue;item.verificationState='checking';await saveVision(item);
      const {matchOriginal}=await import('./vision-match');const matched=await matchOriginal(clip,await videoBlob(clip),item,()=>this.recording||this.analysisBusy);
      item.correspondence=matched.proof;item.correspondenceReason=matched.reason;item.verificationState=matched.proof?'verified':'needs-review';
      if(matched.proof)await this.api(`/api/vision-events/${item.id}/correspondence`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(matched.proof)});
     }
     item.verificationReportPending=true;await saveVision(item);
     await this.api(`/api/vision-events/${item.id}/verification`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({state:item.verificationState,reason:item.correspondenceReason??''})});
     item.verificationReportPending=false;item.verificationRetry=acknowledged({});await saveVision(item);
    }catch(error){
     if(error instanceof UploadYield){item.verificationState='queued';await saveVision(item);break;}
     const transport=error instanceof HttpFailure||error instanceof TypeError||(error as Error).name==='AbortError';
     if(transport){item.verificationRetry=failed(item.verificationRetry??{},error);if(!item.verificationReportPending)item.verificationState='queued';}
     else{item.verificationState='needs-review';item.correspondenceReason=(error as Error).message;item.verificationReportPending=true;item.verificationRetry=failed(item.verificationRetry??{},error);}
     await saveVision(item);if(this.needsPair)break;
    }
   }
  }finally{this.verificationBusy=false;}
 }
 private async upload(recording:string,evidence:string,heat:string,kind:string,blob:Blob){
  const fileId=kind==='video'?recording:evidence,base=`/api/heat-evidence/${fileId}/${kind}?heatId=${heat}`;
  const checkpoint=await this.api(base);if(checkpoint.complete)return;let offset:number=checkpoint.offset;
  if(!Number.isInteger(offset)||offset<0||offset>blob.size)throw new Error('上传断点与本机文件不一致');
  const {sha256}=await import('@noble/hashes/sha2.js');const digest=sha256.create();
  for(let n=0;n<blob.size;n+=4*1024*1024){if(kind==='video'&&this.recording)throw new UploadYield();digest.update(new Uint8Array(await blob.slice(n,n+4*1024*1024).arrayBuffer()));await new Promise(resolve=>setTimeout(resolve,0));}
  const hash=Array.from(digest.digest(),v=>v.toString(16).padStart(2,'0')).join('');
  while(offset<blob.size){if(kind==='video'&&this.recording)throw new UploadYield();const block=blob.slice(offset,offset+4*1024*1024),ack=await this.api(`${base}&offset=${offset}&length=${blob.size}&sha256=${hash}`,{method:'POST',body:block},60000);
   if(ack.offset<=offset||ack.offset>blob.size)throw new Error('服务器上传断点无效');offset=ack.offset;if(offset===blob.size&&!ack.complete)throw new Error('电脑尚未确认完整文件');
  }
 }
}
export const link=new PhoneLink();
